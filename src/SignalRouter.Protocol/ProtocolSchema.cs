namespace SignalRouter.Protocol
{
    // Wire-level property names for protocol envelope v1 (design §18.3, ADR 0007).
    // The envelope is versioned as a whole by ProtocolVersion, so message types
    // carry no per-type version suffix; command identity inside execute_interaction
    // still travels as wireName + version (design §6.1). The v1 contract remains an
    // internal draft until the MCP host ships (ADR 0007).
    internal static class ProtocolSchema
    {
        public const string ProtocolProperty = "protocol";
        public const string MessageIdProperty = "messageId";
        public const string TypeProperty = "type";
        public const string SessionEpochProperty = "sessionEpoch";
        public const string RequestIdProperty = "requestId";
        public const string InReplyToProperty = "inReplyTo";
        public const string PayloadProperty = "payload";

        public const string PeerVersionProperty = "peerVersion";
        public const string CapabilitiesProperty = "capabilities";
        public const string MaxReceiveMessageBytesProperty = "maxReceiveMessageBytes";
        public const string AuthTokenProperty = "authToken";

        public const string CodeProperty = "code";
        public const string MessageProperty = "message";

        public const string CommandProperty = "command";
        public const string NameProperty = "name";
        public const string VersionProperty = "version";
        public const string TargetIdProperty = "targetId";
        public const string ArgumentsProperty = "arguments";
        public const string CorrelationIdProperty = "correlationId";
        public const string IdempotencyKeyProperty = "idempotencyKey";

        public const string SequenceProperty = "sequence";
        public const string ResultProperty = "result";
        public const string OriginProperty = "origin";
        public const string StatusProperty = "status";
        public const string StagesProperty = "stages";
        public const string StageIdProperty = "id";
        public const string RejectionCodeProperty = "rejectionCode";
        public const string FaultCodeProperty = "faultCode";
        public const string StateProperty = "state";
        public const string BeforeProperty = "before";
        public const string AfterProperty = "after";

        public const string ProbeVersionProperty = "probeVersion";
        public const string SnapshotProperty = "snapshot";
    }

    public static class ProtocolMessageTypes
    {
        public const string Hello = "hello";
        public const string Welcome = "welcome";
        public const string Error = "error";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string ExecuteInteraction = "execute_interaction";
        public const string InteractionAccepted = "interaction_accepted";
        public const string InteractionResult = "interaction_result";
        public const string GetInteractionResult = "get_interaction_result";
        public const string CancelInteraction = "cancel_interaction";
        public const string GetRegistrySnapshot = "get_registry_snapshot";
        public const string RegistrySnapshot = "registry_snapshot";
    }

    // Transport-plane error codes. Interaction rejections are payload data inside
    // interaction_result — a rejected dispatch is a successful protocol exchange —
    // so these codes never overlap InteractionRejectionCode. Item 9 adds
    // "unauthorized" alongside auth-token validation (design §19).
    public static class ProtocolErrorCodes
    {
        public const string ProtocolVersionIncompatible = "protocol_version_incompatible";
        public const string MalformedMessage = "malformed_message";
        public const string UnknownMessageType = "unknown_message_type";
        public const string PayloadTooLarge = "payload_too_large";
        public const string SessionEpochMismatch = "session_epoch_mismatch";
        public const string HandshakeRequired = "handshake_required";
        public const string RequestIdConflict = "request_id_conflict";
        public const string ResultUnavailable = "result_unavailable";
    }

    // Pre-item-9 defaults; the security pass (design §19, §25) finalizes the
    // numbers. Depth is counted over the whole envelope, so opaque payloads carry
    // a reduced budget that accounts for their nesting offset inside it.
    public static class ProtocolLimits
    {
        // Applied to every message received before the handshake negotiates the
        // per-direction receive limit; hello, welcome, and error always fit.
        public const int BootstrapMaxMessageBytes = 64 * 1024;

        public const int DefaultMaxReceiveMessageBytes = 1024 * 1024;

        public const int MaxJsonDepth = 64;

        public const int MaxIdentifierChars = 256;

        public const int MaxTextChars = 256;

        public const int MaxErrorMessageChars = 1024;

        public const int MaxCapabilities = 32;

        public const int MaxCapabilityChars = 64;

        // Command arguments sit under envelope → payload → command, three
        // containers deep, so their own nesting may use the remaining budget.
        internal const int ArgumentsMaxDepth = MaxJsonDepth - 3;

        // Registry snapshots sit under envelope → payload, two containers deep.
        internal const int SnapshotMaxDepth = MaxJsonDepth - 2;
    }
}
