using System;

namespace SignalRouter.Protocol
{
    public enum ProtocolConnectionRole
    {
        Runtime = 0,
        Host = 1,
    }

    public enum ProtocolConnectionPhase
    {
        Handshaking = 0,
        Ready = 1,
        Closed = 2,
    }

    public enum ProtocolConnectionVerdict
    {
        // Process the message; for a hello or welcome this also means the
        // handshake succeeded and the session is established.
        Accept = 0,

        // Answer with the carried error code and keep the connection open.
        Reject = 1,

        // Answer with the carried error code and close the connection.
        RejectAndClose = 2,
    }

    public sealed class ProtocolConnectionDecision
    {
        private ProtocolConnectionDecision(
            ProtocolConnectionVerdict verdict,
            string? errorCode,
            string? errorMessage,
            string? peerHandshakeErrorCode)
        {
            Verdict = verdict;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            PeerHandshakeErrorCode = peerHandshakeErrorCode;
        }

        public ProtocolConnectionVerdict Verdict { get; }

        // The error THIS side sends back to the peer (for a Reject/RejectAndClose).
        public string? ErrorCode { get; }

        public string? ErrorMessage { get; }

        // The error code the PEER sent to abort the handshake, surfaced to the
        // owner without ever being echoed back (that would reflect the peer's
        // error at the peer). Set only when a handshake ErrorMessage answered the
        // hello this side sent; null otherwise (ADR 0008).
        public string? PeerHandshakeErrorCode { get; }

        internal static ProtocolConnectionDecision Accept()
        {
            return new ProtocolConnectionDecision(
                ProtocolConnectionVerdict.Accept,
                null,
                null,
                null);
        }

        // The peer aborted the handshake with an ErrorMessage: close locally,
        // send nothing back (verdict Accept), but preserve the peer's code.
        internal static ProtocolConnectionDecision AcceptClosedByPeer(string? peerHandshakeErrorCode)
        {
            return new ProtocolConnectionDecision(
                ProtocolConnectionVerdict.Accept,
                null,
                null,
                peerHandshakeErrorCode);
        }

        internal static ProtocolConnectionDecision Reject(string errorCode, string errorMessage)
        {
            return new ProtocolConnectionDecision(
                ProtocolConnectionVerdict.Reject,
                errorCode,
                errorMessage,
                null);
        }

        internal static ProtocolConnectionDecision RejectAndClose(
            string errorCode,
            string errorMessage)
        {
            return new ProtocolConnectionDecision(
                ProtocolConnectionVerdict.RejectAndClose,
                errorCode,
                errorMessage,
                null);
        }
    }

    // The per-connection protocol state machine (design §17.2, §18.3): pure
    // transitions with no I/O, driven by decoded messages. Both peers run one —
    // the role selects which message directions are legal. Until the handshake
    // completes only hello, welcome, and error may flow; afterwards every
    // epoch-stamped message must match the established session epoch, and a
    // mismatch closes the connection because a changed epoch is by definition a
    // new runtime session (design §13.3). The transport pumps messages in and
    // performs the verdicts; it never interprets protocol state itself
    // (design §5: no transport component may bypass the dispatcher).
    public sealed class ProtocolConnectionStateMachine
    {
        private readonly ProtocolPeerOptions localOptions;
        private readonly HostHelloAuthenticationPolicy? hostAuthentication;
        private HelloMessage? sentHello;

        // Preserves the original two-argument CLR signature so an assembly-only
        // upgrade of a consumer compiled against it does not break.
        public ProtocolConnectionStateMachine(
            ProtocolConnectionRole role,
            ProtocolPeerOptions localOptions)
            : this(role, localOptions, null)
        {
        }

        public ProtocolConnectionStateMachine(
            ProtocolConnectionRole role,
            ProtocolPeerOptions localOptions,
            HostHelloAuthenticationPolicy? hostAuthentication)
        {
            ProtocolContract.RequireDefinedEnum(role, nameof(role));
            this.localOptions = localOptions
                ?? throw new ArgumentNullException(nameof(localOptions));
            // Host-only: enforcing hello authentication requires the host role. A
            // policy supplied to a runtime-role machine is a wiring error.
            if (hostAuthentication != null && role != ProtocolConnectionRole.Host)
            {
                throw new ArgumentException(
                    "Only a host-role connection authenticates the hello.",
                    nameof(hostAuthentication));
            }

            this.hostAuthentication = hostAuthentication;
            Role = role;
            Phase = ProtocolConnectionPhase.Handshaking;
        }

        public ProtocolConnectionRole Role { get; }

        public ProtocolConnectionPhase Phase { get; private set; }

        public ProtocolSession? Session { get; private set; }

        // Runtime side only: records the hello this runtime sent so the incoming
        // welcome can be evaluated against it. Calling out of order is a local
        // programming error, not peer behavior, and fails fast.
        public void OnHelloSent(HelloMessage hello)
        {
            if (hello == null)
            {
                throw new ArgumentNullException(nameof(hello));
            }

            if (Role != ProtocolConnectionRole.Runtime)
            {
                throw new InvalidOperationException("Only the runtime side sends a hello.");
            }

            if (Phase != ProtocolConnectionPhase.Handshaking || sentHello != null)
            {
                throw new InvalidOperationException("A hello was already sent on this connection.");
            }

            sentHello = hello;
        }

        public ProtocolConnectionDecision OnMessageReceived(ProtocolMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            switch (Phase)
            {
                case ProtocolConnectionPhase.Handshaking:
                    return OnHandshakingMessage(message);
                case ProtocolConnectionPhase.Ready:
                    return OnReadyMessage(message);
                default:
                    return ProtocolConnectionDecision.RejectAndClose(
                        ProtocolErrorCodes.HandshakeRequired,
                        "The connection is closed.");
            }
        }

        public void Close()
        {
            Phase = ProtocolConnectionPhase.Closed;
        }

        private ProtocolConnectionDecision OnHandshakingMessage(ProtocolMessage message)
        {
            switch (message)
            {
                case HelloMessage hello when Role == ProtocolConnectionRole.Host:
                    var helloDecision = ProtocolHandshake.EvaluateHello(
                        localOptions,
                        hello,
                        hostAuthentication);
                    return CompleteHandshake(helloDecision);
                case WelcomeMessage welcome when Role == ProtocolConnectionRole.Runtime:
                    if (sentHello == null)
                    {
                        // A welcome that answers no hello is peer misbehavior,
                        // not a local ordering bug: nothing was sent yet.
                        Phase = ProtocolConnectionPhase.Closed;
                        return ProtocolConnectionDecision.RejectAndClose(
                            ProtocolErrorCodes.MalformedMessage,
                            "A welcome arrived before any hello was sent.");
                    }

                    var welcomeDecision = ProtocolHandshake.EvaluateWelcome(sentHello, welcome);
                    return CompleteHandshake(welcomeDecision);
                case ErrorMessage error:
                    // The peer aborted the handshake; take its word and close.
                    // Surface the peer's code as a distinct outcome (never the
                    // outbound ErrorCode, which would reflect it back), and only
                    // when it answers the hello this runtime actually sent — an
                    // unsolicited or mismatched error carries no trusted code
                    // (ADR 0008).
                    Phase = ProtocolConnectionPhase.Closed;
                    var peerCode = Role == ProtocolConnectionRole.Runtime
                        && sentHello != null
                        && error.InReplyTo != null
                        && string.Equals(error.InReplyTo, sentHello.MessageId, StringComparison.Ordinal)
                            ? error.Code
                            : null;
                    return ProtocolConnectionDecision.AcceptClosedByPeer(peerCode);
                case HelloMessage _:
                case WelcomeMessage _:
                    Phase = ProtocolConnectionPhase.Closed;
                    return ProtocolConnectionDecision.RejectAndClose(
                        ProtocolErrorCodes.MalformedMessage,
                        "The handshake message is not valid for this peer's role.");
                default:
                    return ProtocolConnectionDecision.Reject(
                        ProtocolErrorCodes.HandshakeRequired,
                        "The connection has not completed its handshake.");
            }
        }

        private ProtocolConnectionDecision CompleteHandshake(ProtocolHandshakeDecision decision)
        {
            if (!decision.Accepted)
            {
                Phase = ProtocolConnectionPhase.Closed;
                return ProtocolConnectionDecision.RejectAndClose(
                    decision.ErrorCode!,
                    decision.ErrorMessage!);
            }

            Session = decision.Session;
            Phase = ProtocolConnectionPhase.Ready;
            return ProtocolConnectionDecision.Accept();
        }

        private ProtocolConnectionDecision OnReadyMessage(ProtocolMessage message)
        {
            if (message is HelloMessage || message is WelcomeMessage)
            {
                return ProtocolConnectionDecision.Reject(
                    ProtocolErrorCodes.MalformedMessage,
                    "The handshake already completed on this connection.");
            }

            // The handshake selected the session version; a peer that keeps
            // speaking a higher minor afterwards is using schema it agreed not
            // to, so lower-minor negotiation would be meaningless without this.
            if (!message.Protocol.IsMajorCompatibleWith(Session!.Version)
                || message.Protocol.Minor > Session.Version.Minor)
            {
                return ProtocolConnectionDecision.Reject(
                    ProtocolErrorCodes.ProtocolVersionIncompatible,
                    "The message uses a version beyond the negotiated session version.");
            }

            if (message.SessionEpoch != null
                && !string.Equals(message.SessionEpoch, Session!.SessionEpoch, StringComparison.Ordinal))
            {
                Phase = ProtocolConnectionPhase.Closed;
                return ProtocolConnectionDecision.RejectAndClose(
                    ProtocolErrorCodes.SessionEpochMismatch,
                    "The message belongs to a different runtime session.");
            }

            return IsInboundTypeAllowed(message)
                ? ProtocolConnectionDecision.Accept()
                : ProtocolConnectionDecision.Reject(
                    ProtocolErrorCodes.MalformedMessage,
                    "The message type is not valid in this direction.");
        }

        private bool IsInboundTypeAllowed(ProtocolMessage message)
        {
            switch (message)
            {
                case ErrorMessage _:
                case PingMessage _:
                case PongMessage _:
                    return true;
                case ExecuteInteractionMessage _:
                case GetInteractionResultMessage _:
                case CancelInteractionMessage _:
                case GetRegistrySnapshotMessage _:
                case WaitForMessage _:
                case StartRecordingMessage _:
                case StopRecordingMessage _:
                case ReplayRecordingMessage _:
                case GetControlOperationResultMessage _:
                    return Role == ProtocolConnectionRole.Runtime;
                case InteractionAcceptedMessage _:
                case InteractionResultMessage _:
                case InteractionStatusMessage _:
                case RegistrySnapshotMessage _:
                case WaitResultMessage _:
                case RecordingStartedMessage _:
                case RecordingStoppedMessage _:
                case ReplayReportMessage _:
                case ControlOperationResultMessage _:
                    return Role == ProtocolConnectionRole.Host;
                default:
                    return false;
            }
        }
    }
}
