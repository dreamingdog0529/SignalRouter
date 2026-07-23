using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SignalRouter.Protocol
{
    // One side's own handshake declaration: its informational version string, its
    // capability set, and the largest message it is willing to receive. The
    // protocol version is implied — a local declaration always speaks
    // ProtocolVersion.Current.
    public sealed class ProtocolPeerOptions
    {
        public ProtocolPeerOptions(
            string peerVersion,
            IEnumerable<string> capabilities,
            int maxReceiveMessageBytes)
        {
            ProtocolContract.RequireText(
                peerVersion,
                ProtocolLimits.MaxTextChars,
                nameof(peerVersion));
            Capabilities = ProtocolContract.CreateCapabilities(capabilities, nameof(capabilities));
            HelloMessage.RequireReceiveLimit(maxReceiveMessageBytes);
            PeerVersion = peerVersion;
            MaxReceiveMessageBytes = maxReceiveMessageBytes;
        }

        public string PeerVersion { get; }

        public ReadOnlyCollection<string> Capabilities { get; }

        public int MaxReceiveMessageBytes { get; }
    }

    // The negotiated outcome of a successful handshake, as seen from one side.
    // Size limits are per direction (ADR 0007): what this side may send is the
    // peer's declared receive limit, and what it must enforce on its own receive
    // path stays its own declaration.
    public sealed class ProtocolSession
    {
        internal ProtocolSession(
            ProtocolVersion version,
            string sessionEpoch,
            string remotePeerVersion,
            ReadOnlyCollection<string> capabilities,
            int maxSendMessageBytes,
            int maxReceiveMessageBytes,
            TimeSpan recoveryWindow)
        {
            Version = version;
            SessionEpoch = sessionEpoch;
            RemotePeerVersion = remotePeerVersion;
            Capabilities = capabilities;
            MaxSendMessageBytes = maxSendMessageBytes;
            MaxReceiveMessageBytes = maxReceiveMessageBytes;
            RecoveryWindow = recoveryWindow;
        }

        public ProtocolVersion Version { get; }

        public string SessionEpoch { get; }

        public string RemotePeerVersion { get; }

        public ReadOnlyCollection<string> Capabilities { get; }

        public int MaxSendMessageBytes { get; }

        public int MaxReceiveMessageBytes { get; }

        // The runtime ledger's advertised retention: how long results stay
        // queryable, and the window inside which an unavailable query answer
        // makes a byte-exact resend safe (ADR 0007).
        public TimeSpan RecoveryWindow { get; }
    }

    public sealed class ProtocolHandshakeDecision
    {
        private ProtocolHandshakeDecision(
            ProtocolSession? session,
            string? errorCode,
            string? errorMessage)
        {
            Session = session;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public bool Accepted
        {
            get { return Session != null; }
        }

        public ProtocolSession? Session { get; }

        public string? ErrorCode { get; }

        public string? ErrorMessage { get; }

        internal static ProtocolHandshakeDecision Accept(ProtocolSession session)
        {
            return new ProtocolHandshakeDecision(session, null, null);
        }

        internal static ProtocolHandshakeDecision Reject(string errorCode, string errorMessage)
        {
            return new ProtocolHandshakeDecision(null, errorCode, errorMessage);
        }
    }

    // Pure handshake negotiation (design §18.3): majors must match, the
    // negotiated minor is the lower of the two because minors are cumulative,
    // capabilities intersect ordinally so unknown names degrade silently, and
    // each side keeps its own receive limit while adopting the peer's as its
    // send limit. The welcome's envelope version IS the selected version; the
    // runtime verifies the host never selects beyond what the hello offered.
    public static class ProtocolHandshake
    {
        // Host side: evaluates a received hello against the host's own
        // declaration. An accepted decision carries the session the welcome
        // must reflect.
        public static ProtocolHandshakeDecision EvaluateHello(
            ProtocolPeerOptions local,
            HelloMessage hello)
        {
            if (local == null)
            {
                throw new ArgumentNullException(nameof(local));
            }

            if (hello == null)
            {
                throw new ArgumentNullException(nameof(hello));
            }

            if (!hello.Protocol.IsMajorCompatibleWith(ProtocolVersion.Current))
            {
                return ProtocolHandshakeDecision.Reject(
                    ProtocolErrorCodes.ProtocolVersionIncompatible,
                    "The runtime speaks an incompatible major protocol version.");
            }

            var minor = Math.Min(hello.Protocol.Minor, ProtocolVersion.CurrentMinor);
            return ProtocolHandshakeDecision.Accept(new ProtocolSession(
                new ProtocolVersion(ProtocolVersion.CurrentMajor, minor),
                hello.SessionEpoch!,
                hello.PeerVersion,
                Intersect(local.Capabilities, hello.Capabilities),
                hello.MaxReceiveMessageBytes,
                local.MaxReceiveMessageBytes,
                TimeSpan.FromMilliseconds(hello.RecoveryWindowMs)));
        }

        // Runtime side: evaluates the received welcome against the hello this
        // runtime sent. The hello is the local declaration, so no separate
        // options are needed.
        public static ProtocolHandshakeDecision EvaluateWelcome(
            HelloMessage sentHello,
            WelcomeMessage welcome)
        {
            if (sentHello == null)
            {
                throw new ArgumentNullException(nameof(sentHello));
            }

            if (welcome == null)
            {
                throw new ArgumentNullException(nameof(welcome));
            }

            if (!string.Equals(welcome.InReplyTo, sentHello.MessageId, StringComparison.Ordinal))
            {
                return ProtocolHandshakeDecision.Reject(
                    ProtocolErrorCodes.MalformedMessage,
                    "The welcome does not answer the hello that was sent.");
            }

            if (!string.Equals(welcome.SessionEpoch, sentHello.SessionEpoch, StringComparison.Ordinal))
            {
                return ProtocolHandshakeDecision.Reject(
                    ProtocolErrorCodes.SessionEpochMismatch,
                    "The welcome does not echo the hello's session epoch.");
            }

            if (!welcome.Protocol.IsMajorCompatibleWith(ProtocolVersion.Current))
            {
                return ProtocolHandshakeDecision.Reject(
                    ProtocolErrorCodes.ProtocolVersionIncompatible,
                    "The host selected an incompatible major protocol version.");
            }

            if (welcome.Protocol.Minor > sentHello.Protocol.Minor)
            {
                return ProtocolHandshakeDecision.Reject(
                    ProtocolErrorCodes.ProtocolVersionIncompatible,
                    "The host selected a minor version beyond the hello's offer.");
            }

            return ProtocolHandshakeDecision.Accept(new ProtocolSession(
                welcome.Protocol,
                sentHello.SessionEpoch!,
                welcome.PeerVersion,
                Intersect(sentHello.Capabilities, welcome.Capabilities),
                welcome.MaxReceiveMessageBytes,
                sentHello.MaxReceiveMessageBytes,
                TimeSpan.FromMilliseconds(sentHello.RecoveryWindowMs)));
        }

        private static ReadOnlyCollection<string> Intersect(
            ReadOnlyCollection<string> local,
            ReadOnlyCollection<string> remote)
        {
            var remoteSet = new HashSet<string>(remote, StringComparer.Ordinal);
            var shared = new List<string>();
            for (var index = 0; index < local.Count; index++)
            {
                if (remoteSet.Contains(local[index]))
                {
                    shared.Add(local[index]);
                }
            }

            // Both inputs are already ordinal-sorted, so the intersection is too.
            return new ReadOnlyCollection<string>(shared);
        }
    }
}
