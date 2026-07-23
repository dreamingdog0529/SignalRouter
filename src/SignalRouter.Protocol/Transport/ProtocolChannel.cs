using System;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRouter.Protocol.Transport
{
    public enum ProtocolChannelFrameKind
    {
        // A complete protocol message (one whole WebSocket text message).
        Message = 0,

        // The channel ended — peer close, transport failure, or local close.
        Closed = 1,

        // The peer sent a message beyond the receive limit; the channel was
        // aborted mid-reassembly so the oversized payload was never buffered
        // whole (design §19). The session reports and closes.
        Overflow = 2,
    }

    // One receive verdict from a channel. Payload is present exactly for
    // Message frames.
    public readonly struct ProtocolChannelFrame
    {
        private ProtocolChannelFrame(ProtocolChannelFrameKind kind, byte[]? payload)
        {
            Kind = kind;
            Payload = payload;
        }

        public ProtocolChannelFrameKind Kind { get; }

        public byte[]? Payload { get; }

        public static ProtocolChannelFrame Message(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            return new ProtocolChannelFrame(ProtocolChannelFrameKind.Message, payload);
        }

        public static ProtocolChannelFrame Closed()
        {
            return new ProtocolChannelFrame(ProtocolChannelFrameKind.Closed, null);
        }

        public static ProtocolChannelFrame Overflow()
        {
            return new ProtocolChannelFrame(ProtocolChannelFrameKind.Overflow, null);
        }
    }

    // The transport seam the runtime bridge and its tests share: one send is
    // one complete protocol message, one receive returns one complete message
    // (reassembled to EndOfMessage by the implementation) or a terminal
    // verdict. Implementations own the socket; the session logic above this
    // interface stays pure and is tested over an in-memory duplex.
    public interface IProtocolChannel : IDisposable
    {
        ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

        ValueTask<ProtocolChannelFrame> ReceiveAsync(
            int maxMessageBytes,
            CancellationToken cancellationToken);

        ValueTask CloseAsync(CancellationToken cancellationToken);
    }
}
