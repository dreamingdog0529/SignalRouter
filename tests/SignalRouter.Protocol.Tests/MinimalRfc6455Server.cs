using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SignalRouter.Protocol.Tests;

// Just enough RFC 6455 server to exercise ClientWebSocketChannel over a real
// loopback TCP socket, portable across Windows and the ubuntu CI runner
// (HttpListener's WebSocket upgrade is Windows-only). Serves one client:
// upgrade handshake, masked client frames in, unmasked server frames out with
// controllable fragmentation, and the close handshake.
internal sealed class MinimalRfc6455Server : IDisposable
{
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener listener;

    public MinimalRfc6455Server()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public Uri Endpoint => new("ws://127.0.0.1:" + Port + "/");

    public async Task<Session> AcceptAsync()
    {
        var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        var stream = client.GetStream();
        await PerformUpgradeAsync(stream).ConfigureAwait(false);
        return new Session(client, stream);
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
            var read = await stream.ReadAsync(single.AsMemory(0, 1)).ConfigureAwait(false);
            if (read != 1)
            {
                throw new InvalidOperationException("The upgrade request ended prematurely.");
            }

            request.Append((char)single[0]);
        }

        string? key = null;
        foreach (var line in request.ToString().Split("\r\n"))
        {
            const string header = "Sec-WebSocket-Key:";
            if (line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                key = line[header.Length..].Trim();
            }
        }

        if (key == null)
        {
            throw new InvalidOperationException("The client sent no Sec-WebSocket-Key.");
        }

        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
        await stream.WriteAsync(response).ConfigureAwait(false);
    }

    internal sealed class Session : IDisposable
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;

        internal Session(TcpClient client, NetworkStream stream)
        {
            this.client = client;
            this.stream = stream;
        }

        public async Task<(int Opcode, byte[] Payload)> ReadFrameAsync()
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

        public Task SendTextAsync(byte[] payload)
        {
            return SendFrameAsync(0x1, payload, fin: true);
        }

        // Sends one logical text message split into the given fragment sizes,
        // exercising the client's EndOfMessage reassembly.
        public async Task SendFragmentedTextAsync(byte[] payload, int fragmentBytes)
        {
            var offset = 0;
            var first = true;
            while (offset < payload.Length)
            {
                var count = Math.Min(fragmentBytes, payload.Length - offset);
                var fragment = new byte[count];
                Array.Copy(payload, offset, fragment, 0, count);
                offset += count;
                await SendFrameAsync(
                    first ? 0x1 : 0x0,
                    fragment,
                    fin: offset == payload.Length).ConfigureAwait(false);
                first = false;
            }
        }

        public Task SendCloseAsync()
        {
            return SendFrameAsync(0x8, Array.Empty<byte>(), fin: true);
        }

        public Task SendBinaryAsync(byte[] payload)
        {
            return SendFrameAsync(0x2, payload, fin: true);
        }

        public async Task RunCloseHandshakeAsync()
        {
            while (true)
            {
                var (opcode, payload) = await ReadFrameAsync().ConfigureAwait(false);
                if (opcode == 0x8)
                {
                    await SendFrameAsync(0x8, payload, fin: true).ConfigureAwait(false);
                    return;
                }
            }
        }

        public void Dispose()
        {
            stream.Dispose();
            client.Dispose();
        }

        private async Task SendFrameAsync(int opcode, byte[] payload, bool fin)
        {
            byte[] header;
            var finBit = fin ? 0x80 : 0x00;
            if (payload.Length < 126)
            {
                header = new[] { (byte)(finBit | opcode), (byte)payload.Length };
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                header = new[]
                {
                    (byte)(finBit | opcode),
                    (byte)126,
                    (byte)(payload.Length >> 8),
                    (byte)payload.Length,
                };
            }
            else
            {
                header = new byte[10];
                header[0] = (byte)(finBit | opcode);
                header[1] = 127;
                var length = (long)payload.Length;
                for (var index = 0; index < 8; index++)
                {
                    header[9 - index] = (byte)(length >> (8 * index));
                }
            }

            await stream.WriteAsync(header).ConfigureAwait(false);
            await stream.WriteAsync(payload).ConfigureAwait(false);
        }

        private async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset))
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
