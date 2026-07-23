using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SignalRouter.Protocol;
using SignalRouter.Protocol.Transport;

namespace SignalRouter.McpHost.Tests;

// Proves the real HTTP stack end of the wiring on the CI platform: a Kestrel
// loopback listener with the same WebSocket mapping the host process uses,
// reached by the same WebSocketChannel the Unity bridge dials with, completes
// the protocol handshake. This is exactly the path HttpListener could not
// provide cross-platform.
public sealed class KestrelEndpointTests
{
    [Test]
    public async Task ARuntimeChannelCompletesTheHandshakeThroughKestrel()
    {
        var bridge = new HostBridge(new HostBridgeOptions(
            "SignalRouter.McpHost test",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10)));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
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
            await bridge.RunConnectionAsync(channel, context.RequestAborted);
        });

        await app.StartAsync();
        try
        {
            var port = new Uri(app.Urls.First()).Port;
            using var channel = await WebSocketChannel.ConnectAsync(
                new Uri("ws://127.0.0.1:" + port + "/"),
                CancellationToken.None);

            var hello = new HelloMessage(
                "m-hello",
                "epoch-1",
                "SignalRouter.Unity test",
                Array.Empty<string>(),
                ProtocolLimits.DefaultMaxReceiveMessageBytes);
            await channel.SendAsync(
                ProtocolMessageWriter.Encode(hello, ProtocolLimits.BootstrapMaxMessageBytes),
                CancellationToken.None);

            var frame = await channel.ReceiveAsync(
                ProtocolLimits.BootstrapMaxMessageBytes,
                CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(frame.Kind, Is.EqualTo(ProtocolChannelFrameKind.Message));
            var read = ProtocolMessageReader.Read(
                frame.Payload!,
                ProtocolLimits.BootstrapMaxMessageBytes);
            Assert.That(read.Status, Is.EqualTo(ProtocolReadStatus.Success));
            Assert.That(read.Message, Is.InstanceOf<WelcomeMessage>());
            Assert.That(read.Message!.SessionEpoch, Is.EqualTo("epoch-1"));
            Assert.That(bridge.IsConnected, Is.True);

            await channel.CloseAsync(CancellationToken.None);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            bridge.Dispose();
        }
    }
}
