using System.Text;
using NUnit.Framework;
using SignalRouter.Protocol.Transport;

namespace SignalRouter.Protocol.Tests;

public sealed class ClientWebSocketChannelTests
{
    private const int Limit = ProtocolLimits.DefaultMaxReceiveMessageBytes;

    [Test]
    public async Task SendsAndReceivesWholeMessages()
    {
        using var server = new MinimalRfc6455Server();
        var sessionTask = server.AcceptAsync();
        using var channel = await ClientWebSocketChannel.ConnectAsync(
            server.Endpoint,
            CancellationToken.None);
        using var session = await sessionTask;

        await channel.SendAsync(Encoding.UTF8.GetBytes("outbound"), CancellationToken.None);
        var (opcode, payload) = await session.ReadFrameAsync();
        Assert.That(opcode, Is.EqualTo(0x1));
        Assert.That(Encoding.UTF8.GetString(payload), Is.EqualTo("outbound"));

        await session.SendTextAsync(Encoding.UTF8.GetBytes("inbound"));
        var frame = await channel.ReceiveAsync(Limit, CancellationToken.None);
        Assert.That(frame.Kind, Is.EqualTo(ProtocolChannelFrameKind.Message));
        Assert.That(Encoding.UTF8.GetString(frame.Payload!), Is.EqualTo("inbound"));
    }

    [Test]
    public async Task ReassemblesFragmentedMessagesToEndOfMessage()
    {
        using var server = new MinimalRfc6455Server();
        var sessionTask = server.AcceptAsync();
        using var channel = await ClientWebSocketChannel.ConnectAsync(
            server.Endpoint,
            CancellationToken.None);
        using var session = await sessionTask;

        var payload = new byte[100 * 1024];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)('a' + (index % 26));
        }

        await session.SendFragmentedTextAsync(payload, fragmentBytes: 7 * 1024);
        var frame = await channel.ReceiveAsync(Limit, CancellationToken.None);

        Assert.That(frame.Kind, Is.EqualTo(ProtocolChannelFrameKind.Message));
        Assert.That(frame.Payload, Is.EqualTo(payload));
    }

    [Test]
    public async Task AbortsOnMessagesBeyondTheReceiveLimit()
    {
        using var server = new MinimalRfc6455Server();
        var sessionTask = server.AcceptAsync();
        using var channel = await ClientWebSocketChannel.ConnectAsync(
            server.Endpoint,
            CancellationToken.None);
        using var session = await sessionTask;

        var oversized = new byte[128 * 1024];
        await session.SendFragmentedTextAsync(oversized, fragmentBytes: 32 * 1024);
        var frame = await channel.ReceiveAsync(64 * 1024, CancellationToken.None);

        Assert.That(frame.Kind, Is.EqualTo(ProtocolChannelFrameKind.Overflow));
    }

    [Test]
    public async Task PeerCloseSurfacesAsAClosedFrame()
    {
        using var server = new MinimalRfc6455Server();
        var sessionTask = server.AcceptAsync();
        using var channel = await ClientWebSocketChannel.ConnectAsync(
            server.Endpoint,
            CancellationToken.None);
        using var session = await sessionTask;

        await session.SendCloseAsync();
        var frame = await channel.ReceiveAsync(Limit, CancellationToken.None);

        Assert.That(frame.Kind, Is.EqualTo(ProtocolChannelFrameKind.Closed));
    }

    [Test]
    public async Task BinaryMessagesAreAContractViolationThatClosesTheChannel()
    {
        using var server = new MinimalRfc6455Server();
        var sessionTask = server.AcceptAsync();
        using var channel = await ClientWebSocketChannel.ConnectAsync(
            server.Endpoint,
            CancellationToken.None);
        using var session = await sessionTask;

        await session.SendBinaryAsync(new byte[] { 1, 2, 3 });
        var frame = await channel.ReceiveAsync(Limit, CancellationToken.None);

        Assert.That(frame.Kind, Is.EqualTo(ProtocolChannelFrameKind.Closed));
    }

    [Test]
    public async Task CloseCompletesTheHandshakeWithThePeer()
    {
        using var server = new MinimalRfc6455Server();
        var sessionTask = server.AcceptAsync();
        using var channel = await ClientWebSocketChannel.ConnectAsync(
            server.Endpoint,
            CancellationToken.None);
        using var session = await sessionTask;

        var closeHandshake = session.RunCloseHandshakeAsync();
        await channel.CloseAsync(CancellationToken.None);
        await closeHandshake;
    }
}
