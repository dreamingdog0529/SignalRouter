using System.Net;
using SignalRouter.McpHost;
using SignalRouter.Protocol.Transport;

// The external MCP host process (design §18.1): an MCP client drives it over
// stdio while the Unity runtime connects to its loopback WebSocket endpoint.
// Kestrel binds 127.0.0.1 and ::1 explicitly — never a hostname — per the
// §19 loopback-only posture (token auth lands in item 9).
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

builder.Services.AddSingleton(HostBridgeOptions.CreateDefault());
builder.Services.AddSingleton<HostBridge>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SignalRouterTools>();

var app = builder.Build();
app.UseWebSockets();
app.Map("/", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var channel = new WebSocketChannel(socket);
    var bridge = context.RequestServices.GetRequiredService<HostBridge>();
    await bridge.RunConnectionAsync(channel, context.RequestAborted);
});

await app.RunAsync();
