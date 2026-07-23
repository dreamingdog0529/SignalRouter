using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace SignalRouter.Tests;

// The go/no-go spike for the runtime bridge transport (plan 8b commit 1):
// System.Net.WebSockets.ClientWebSocket must work under this Unity Editor's
// Mono runtime — connect through a real loopback TCP socket, exchange text
// messages including one large enough to force multi-read reassembly, and
// complete the close handshake. The peer is a minimal in-process RFC 6455
// server (TCP listener + upgrade handshake + frame codec) because the batch
// test run launches no external processes. If this suite ever fails on a
// Unity upgrade, the transport falls back to a hand-rolled RFC 6455 client
// behind IProtocolChannel per the item-8 plan.
public sealed class ClientWebSocketSpikePlayModeTests
{
    [UnityTest]
    public IEnumerator ClientWebSocketConnectsEchoesAndClosesOverLoopback()
    {
        using var server = new MinimalWebSocketServer();
        var serverTask = Task.Run(() => server.RunEchoSessionAsync(expectedMessages: 2));
        var clientTask = Task.Run(() => RunClientSessionAsync(server.Port));

        yield return PlayModeAwait.Completion(
            Task.WhenAll(serverTask, clientTask),
            timeoutSeconds: 30f);
    }

    private static async Task RunClientSessionAsync(int port)
    {
        using var client = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(
            new Uri("ws://127.0.0.1:" + port + "/"),
            timeout.Token).ConfigureAwait(false);
        Assert.That(client.State, Is.EqualTo(WebSocketState.Open));

        var small = Encoding.UTF8.GetBytes("spike-ping");
        await client.SendAsync(
            new ArraySegment<byte>(small),
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeout.Token).ConfigureAwait(false);
        var smallEcho = await ReceiveWholeMessageAsync(client, timeout.Token)
            .ConfigureAwait(false);
        Assert.That(Encoding.UTF8.GetString(smallEcho), Is.EqualTo("spike-ping"));

        // Large enough that a 4 KiB receive buffer needs many reads: proves the
        // EndOfMessage reassembly pattern the transport channel will rely on.
        var large = new byte[64 * 1024];
        for (var index = 0; index < large.Length; index++)
        {
            large[index] = (byte)('a' + (index % 26));
        }

        await client.SendAsync(
            new ArraySegment<byte>(large),
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeout.Token).ConfigureAwait(false);
        var largeEcho = await ReceiveWholeMessageAsync(client, timeout.Token)
            .ConfigureAwait(false);
        Assert.That(largeEcho, Is.EqualTo(large));

        await client.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "spike-done",
            timeout.Token).ConfigureAwait(false);
        Assert.That(client.State, Is.EqualTo(WebSocketState.Closed));
    }

    private static async Task<byte[]> ReceiveWholeMessageAsync(
        ClientWebSocket client,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var assembled = new System.IO.MemoryStream();
        while (true)
        {
            var result = await client.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken).ConfigureAwait(false);
            Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Text));
            assembled.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return assembled.ToArray();
            }
        }
    }

    // Just enough RFC 6455 to serve one loopback client: the upgrade
    // handshake, masked client-to-server frames with 7/16/64-bit payload
    // lengths, unmasked text echoes, and the close reply.
    private sealed class MinimalWebSocketServer : IDisposable
    {
        private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpListener listener;

        public MinimalWebSocketServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public async Task RunEchoSessionAsync(int expectedMessages)
        {
            using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using var stream = client.GetStream();
            await PerformUpgradeAsync(stream).ConfigureAwait(false);

            for (var handled = 0; handled < expectedMessages; handled++)
            {
                var (opcode, payload) = await ReadFrameAsync(stream).ConfigureAwait(false);
                Assert.That(opcode, Is.EqualTo(0x1), "Expected a text frame.");
                await WriteFrameAsync(stream, 0x1, payload).ConfigureAwait(false);
            }

            var (closeOpcode, closePayload) = await ReadFrameAsync(stream).ConfigureAwait(false);
            Assert.That(closeOpcode, Is.EqualTo(0x8), "Expected a close frame.");
            await WriteFrameAsync(stream, 0x8, closePayload).ConfigureAwait(false);
        }

        public void Dispose()
        {
            listener.Stop();
        }

        private static async Task PerformUpgradeAsync(NetworkStream stream)
        {
            var request = new StringBuilder();
            var single = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(single, 0, 1).ConfigureAwait(false);
                Assert.That(read, Is.EqualTo(1), "The upgrade request ended prematurely.");
                request.Append((char)single[0]);
            }

            string? key = null;
            foreach (var line in request.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                const string header = "Sec-WebSocket-Key:";
                if (line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
                {
                    key = line.Substring(header.Length).Trim();
                }
            }

            Assert.That(key, Is.Not.Null, "The client sent no Sec-WebSocket-Key.");
            string accept;
            using (var sha1 = SHA1.Create())
            {
                accept = Convert.ToBase64String(
                    sha1.ComputeHash(Encoding.ASCII.GetBytes(key + HandshakeGuid)));
            }

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
            await stream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
        }

        private static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(
            NetworkStream stream)
        {
            var header = await ReadExactAsync(stream, 2).ConfigureAwait(false);
            var opcode = header[0] & 0x0F;
            var masked = (header[1] & 0x80) != 0;
            Assert.That(masked, Is.True, "Client frames must be masked.");
            long length = header[1] & 0x7F;
            if (length == 126)
            {
                var extended = await ReadExactAsync(stream, 2).ConfigureAwait(false);
                length = (extended[0] << 8) | extended[1];
            }
            else if (length == 127)
            {
                var extended = await ReadExactAsync(stream, 8).ConfigureAwait(false);
                length = 0;
                for (var index = 0; index < 8; index++)
                {
                    length = (length << 8) | extended[index];
                }
            }

            var mask = await ReadExactAsync(stream, 4).ConfigureAwait(false);
            var payload = await ReadExactAsync(stream, checked((int)length)).ConfigureAwait(false);
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)(payload[index] ^ mask[index % 4]);
            }

            return (opcode, payload);
        }

        private static async Task WriteFrameAsync(NetworkStream stream, int opcode, byte[] payload)
        {
            byte[] header;
            if (payload.Length < 126)
            {
                header = new[] { (byte)(0x80 | opcode), (byte)payload.Length };
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                header = new[]
                {
                    (byte)(0x80 | opcode),
                    (byte)126,
                    (byte)(payload.Length >> 8),
                    (byte)payload.Length,
                };
            }
            else
            {
                header = new byte[10];
                header[0] = (byte)(0x80 | opcode);
                header[1] = 127;
                var length = (long)payload.Length;
                for (var index = 0; index < 8; index++)
                {
                    header[9 - index] = (byte)(length >> (8 * index));
                }
            }

            await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer, offset, count - offset)
                    .ConfigureAwait(false);
                Assert.That(read, Is.GreaterThan(0), "The stream ended prematurely.");
                offset += read;
            }

            return buffer;
        }
    }
}
