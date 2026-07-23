#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SignalRouter.Protocol;

namespace SignalRouter.Tests;

// A scripted MCP-host stand-in for bridge PlayMode tests: a minimal RFC 6455
// server (the batch test run launches no external processes) that speaks the
// item-7 protocol via the real codecs. Connections can be dropped abruptly —
// no close frame — to drive the bridge's reconnect path.
internal sealed class WireHostPeer : IDisposable
{
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener listener;
    private int nextMessageId;

    public WireHostPeer()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string EndpointUrl => "ws://127.0.0.1:" + Port + "/";

    public string NextMessageId() => "h-" + (++nextMessageId);

    public async Task<Connection> AcceptAsync()
    {
        var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        var stream = client.GetStream();
        await UpgradeAsync(stream).ConfigureAwait(false);
        return new Connection(client, stream);
    }

    public void Dispose()
    {
        listener.Stop();
    }

    private static async Task UpgradeAsync(NetworkStream stream)
    {
        var request = new StringBuilder();
        var single = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(single, 0, 1).ConfigureAwait(false);
            if (read != 1)
            {
                throw new InvalidOperationException("The upgrade request ended prematurely.");
            }

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

        if (key == null)
        {
            throw new InvalidOperationException("The client sent no Sec-WebSocket-Key.");
        }

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

    internal sealed class Connection : IDisposable
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;

        internal Connection(TcpClient client, NetworkStream stream)
        {
            this.client = client;
            this.stream = stream;
        }

        public async Task<ProtocolMessage?> ReceiveAsync()
        {
            while (true)
            {
                var (opcode, payload) = await ReadFrameAsync().ConfigureAwait(false);
                switch (opcode)
                {
                    case 0x1:
                        var read = ProtocolMessageReader.Read(
                            payload,
                            ProtocolLimits.DefaultMaxReceiveMessageBytes);
                        if (read.Status != ProtocolReadStatus.Success)
                        {
                            throw new InvalidOperationException(
                                "The runtime sent an undecodable message: " + read.ErrorMessage);
                        }

                        return read.Message;
                    case 0x8:
                        await WriteFrameAsync(0x8, payload).ConfigureAwait(false);
                        return null;
                    case 0x9:
                        await WriteFrameAsync(0xA, payload).ConfigureAwait(false);
                        break;
                    default:
                        break;
                }
            }
        }

        public Task SendAsync(ProtocolMessage message)
        {
            return WriteFrameAsync(
                0x1,
                ProtocolMessageWriter.Encode(
                    message,
                    ProtocolLimits.DefaultMaxReceiveMessageBytes));
        }

        // Abrupt TCP teardown with no close frame — the disconnect the
        // reconnect tests need.
        public void Drop()
        {
            client.Close();
        }

        public void Dispose()
        {
            stream.Dispose();
            client.Dispose();
        }

        private async Task<(int Opcode, byte[] Payload)> ReadFrameAsync()
        {
            var header = await ReadExactAsync(2).ConfigureAwait(false);
            var opcode = header[0] & 0x0F;
            if ((header[1] & 0x80) == 0)
            {
                throw new InvalidOperationException("Client frames must be masked.");
            }

            long length = header[1] & 0x7F;
            if (length == 126)
            {
                var extended = await ReadExactAsync(2).ConfigureAwait(false);
                length = (extended[0] << 8) | extended[1];
            }
            else if (length == 127)
            {
                var extended = await ReadExactAsync(8).ConfigureAwait(false);
                length = 0;
                for (var index = 0; index < 8; index++)
                {
                    length = (length << 8) | extended[index];
                }
            }

            var mask = await ReadExactAsync(4).ConfigureAwait(false);
            var payload = await ReadExactAsync(checked((int)length)).ConfigureAwait(false);
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)(payload[index] ^ mask[index % 4]);
            }

            return (opcode, payload);
        }

        private async Task WriteFrameAsync(int opcode, byte[] payload)
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

        private async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer, offset, count - offset)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new InvalidOperationException("The stream ended prematurely.");
                }

                offset += read;
            }

            return buffer;
        }
    }
}
