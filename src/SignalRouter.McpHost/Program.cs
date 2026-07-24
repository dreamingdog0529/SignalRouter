using System.Net;
using Microsoft.Extensions.Hosting;
using SignalRouter.McpHost;
using SignalRouter.Protocol.HostDiscovery;
using SignalRouter.Protocol.Transport;

// The external MCP host process (design §18.1): an MCP client drives it over
// stdio while the Unity runtime connects to its loopback WebSocket endpoint.
// Kestrel binds 127.0.0.1 and ::1 explicitly — never a hostname — per the
// §19 loopback-only posture. Item 9 (ADR 0008) adds a per-instance token,
// published in an owner-only discovery descriptor keyed by port; this phase
// publishes it but does not yet verify it in the handshake (enforcement lands
// last, in phase 6, to avoid a flag day).
var builder = WebApplication.CreateBuilder(args);

var port = 8017;
var configured = Environment.GetEnvironmentVariable("SIGNALROUTER_PORT");
if (configured != null && (!int.TryParse(configured, out port) || port < 1 || port > 65535))
{
    throw new InvalidOperationException(
        "SIGNALROUTER_PORT must be a TCP port number; got '" + configured + "'.");
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, port);
    kestrel.Listen(IPAddress.IPv6Loopback, port);
});

// Stdout carries the MCP stdio transport; every log line must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(console =>
{
    console.LogToStandardErrorThreshold = LogLevel.Trace;
});

// One identity minted per host instance: the token published to disk is the token
// the handshake will later expect (ADR 0008). The descriptor advertises the IPv4
// loopback endpoint keyed by the bound port, which the strict reader requires.
var identity = HostInstanceIdentity.Create();
var location = HostDiscoveryPaths.Resolve(port);
var descriptorJson = HostDiscoveryDescriptor.Serialize(
    identity.InstanceId,
    new Uri("ws://127.0.0.1:" + port + "/"),
    identity.TokenHex,
    identity.ProcessId,
    identity.StartedAt);

builder.Services.AddSingleton(HostBridgeOptions.CreateDefault());
builder.Services.AddSingleton<HostBridge>();
builder.Services.AddSingleton(new HostDescriptorLifecycle(
    new HostDiscoveryStore(),
    location,
    descriptorJson,
    identity.InstanceId));
// Registered as a hosted service so the generic host drives its IHostedLifecycleService
// hooks: publish after every service has started (Kestrel is bound), remove as
// shutdown begins (the listener still holds the port).
builder.Services.AddSingleton<IHostedService>(
    sp => sp.GetRequiredService<HostDescriptorLifecycle>());
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SignalRouterTools>();

var app = builder.Build();

// Close the readiness gate and remove the descriptor the moment shutdown is
// signaled. ApplicationStopping fires before the host invokes the lifecycle's
// StoppingAsync, so registering here shrinks the window in which an externally
// initiated shutdown (Ctrl+C, SIGTERM, StopApplication) would otherwise leave the
// host discoverable and ready. Stop is idempotent, so StoppingAsync remains a
// backup that also runs it. Both still precede Kestrel releasing the port.
var descriptorLifecycle = app.Services.GetRequiredService<HostDescriptorLifecycle>();
app.Lifetime.ApplicationStopping.Register(descriptorLifecycle.Stop);

app.UseWebSockets();
app.Map("/", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Readiness gate (ADR 0008): refuse the handshake until the descriptor is
    // published and until shutdown has begun. A runtime that connects on a stale
    // descriptor before publication, or during teardown, is told to retry rather
    // than handed a welcome the host is not yet (or no longer) prepared to honor.
    var lifecycle = context.RequestServices.GetRequiredService<HostDescriptorLifecycle>();
    if (!lifecycle.IsReady)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var channel = new WebSocketChannel(socket);
    var bridge = context.RequestServices.GetRequiredService<HostBridge>();
    await bridge.RunConnectionAsync(channel, context.RequestAborted);
});

await app.RunAsync();
