namespace SignalRouter
{
    // Resource bounds for the agent semantic-UI snapshot (design §19, ADR 0008).
    //
    // The snapshot is a FLAT target array linked by parentId, not a recursive tree,
    // so the protocol's JSON-depth limit does not bound it at all. These per-field,
    // cardinality, aggregate-byte, and parent-graph caps are what keep a snapshot
    // bounded. They are enforced at registration and at capture on the trusted Core
    // side, and re-validated on receive by the host, which does not trust the runtime
    // peer (ADR 0008).
    //
    // The cardinality caps prevent pathological allocation and CPU; the aggregate byte
    // cap is the binding ceiling that keeps a captured snapshot within the protocol's
    // receive limit. The wire layer still enforces the negotiated per-direction limit
    // independently, so this Core ceiling stays below the default receive limit with
    // headroom for the envelope.
    public static class InteractionSnapshotLimits
    {
        public const int MaxTargetIdChars = 256;

        public const int MaxRoleChars = 64;

        public const int MaxLabelChars = 256;

        public const int MaxValueChars = 1024;

        public const int MaxInteractionNameChars = 256;

        public const int MaxArgumentNameChars = 256;

        public const int MaxAvailableInteractionsPerTarget = 16;

        public const int MaxArgumentsPerInteraction = 16;

        public const int MaxSnapshotTargets = 1024;

        public const int MaxParentChainDepth = 32;

        // Fits within the protocol's 1 MiB default receive limit with headroom for the
        // envelope. PR-2's benchmark confirms a realistic UI at the cardinality caps
        // stays well within this ceiling.
        public const int MaxSnapshotBytes = 768 * 1024;
    }
}
