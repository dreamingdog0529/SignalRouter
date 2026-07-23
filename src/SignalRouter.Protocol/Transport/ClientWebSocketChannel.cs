using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRouter.Protocol.Transport
{
    // IProtocolChannel over System.Net.WebSockets.ClientWebSocket — the Unity
    // runtime's side of the loopback connection (design §18.1: the runtime is
    // the client so it can reconnect after a domain reload). Receive reassembles
    // fragments to EndOfMessage because ReceiveAsync guarantees no message
    // boundary otherwise, and enforces the receive limit during reassembly so
    // an oversized peer message aborts the socket instead of being buffered
    // whole (design §19). Verified against Unity 6000.5 Mono by the PlayMode
    // spike; the fallback, if a Unity upgrade breaks it, is a hand-rolled
    // RFC 6455 client behind the same interface.
    public sealed class ClientWebSocketChannel : IProtocolChannel
    {
        private const int ReceiveBufferBytes = 16 * 1024;

        private readonly ClientWebSocket socket;

        private ClientWebSocketChannel(ClientWebSocket socket)
        {
            this.socket = socket;
        }

        public static async Task<ClientWebSocketChannel> ConnectAsync(
            Uri endpoint,
            CancellationToken cancellationToken)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            return new ClientWebSocketChannel(socket);
        }

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken)
        {
            await socket.SendAsync(
                GetSegment(message),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<ProtocolChannelFrame> ReceiveAsync(
            int maxMessageBytes,
            CancellationToken cancellationToken)
        {
            if (maxMessageBytes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxMessageBytes),
                    maxMessageBytes,
                    "The size limit must be positive.");
            }

            var buffer = new byte[Math.Min(ReceiveBufferBytes, maxMessageBytes)];
            using var assembled = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    return ProtocolChannelFrame.Closed();
                }
                catch (ObjectDisposedException)
                {
                    return ProtocolChannelFrame.Closed();
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return ProtocolChannelFrame.Closed();
                }

                // The protocol speaks UTF-8 JSON text messages only; a binary
                // message is a peer contract violation, not data to interpret.
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    socket.Abort();
                    return ProtocolChannelFrame.Closed();
                }

                if (assembled.Length + result.Count > maxMessageBytes)
                {
                    socket.Abort();
                    return ProtocolChannelFrame.Overflow();
                }

                assembled.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return ProtocolChannelFrame.Message(assembled.ToArray());
                }
            }
        }

        public async ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            if (socket.State == WebSocketState.Open
                || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "closing",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // The peer vanished mid-close; the socket is finished either
                    // way and the session treats the channel as closed.
                    socket.Abort();
                }
            }
        }

        public void Dispose()
        {
            socket.Dispose();
        }

        private static ArraySegment<byte> GetSegment(ReadOnlyMemory<byte> message)
        {
            return System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                message,
                out var segment)
                ? segment
                : new ArraySegment<byte>(message.ToArray());
        }
    }
}
