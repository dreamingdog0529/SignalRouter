using System;

namespace SignalRouter.V2.Codec.Recording
{
    /// <summary>The record kinds of RecordingEventSchema@1.x (ADR 0016).</summary>
    public enum RecordKind : byte
    {
        EvidenceCut = 0x01,
        Blob = 0x02,

        /// <summary>The TimelineTrack lane (1.1): droppable diagnostics, excluded from closure.</summary>
        Timeline = 0x03,

        ComparisonProfile = 0x04,

        /// <summary>
        /// A delta-encoded blob (1.1): base ContentId plus a patch reconstructing
        /// the result payload, verified against the result ContentId like any blob.
        /// </summary>
        DeltaBlob = 0x05,
    }

    /// <summary>RecordingEventSchema identity and framing constants (ADR 0016).</summary>
    public static class RecordingSchema
    {
        public const int MajorVersion = 1;

        public const int MinorVersion = 1;

        /// <summary>
        /// The structural bound on delta chains between checkpoints
        /// (StateStore.MaxChainLength, security-resources.md §5): writers honor
        /// min(declared, this); a reader refuses a deeper chain as structural.
        /// </summary>
        public const int MaxDeltaChainDepth = 32;

        /// <summary>The file magic "SRRE".</summary>
        public static ReadOnlySpan<byte> Magic => new byte[] { 0x53, 0x52, 0x52, 0x45 };

        /// <summary>The per-record commit byte; a record without it is torn.</summary>
        public const byte CommitByte = 0xC7;
    }

    /// <summary>
    /// Caller-supplied decode budgets, enforced before any allocation from an
    /// untrusted length field (ADR 0016; recording-replay.md §7 resource limits).
    /// </summary>
    public sealed class ArtifactReadLimits
    {
        public ArtifactReadLimits(
            long maxArtifactBytes,
            int maxRecordCount,
            int maxRecordBytes,
            int maxBlobBytes,
            int maxStringLength)
        {
            if (maxArtifactBytes < 1 || maxRecordCount < 1 || maxRecordBytes < 1 ||
                maxBlobBytes < 1 || maxStringLength < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxArtifactBytes), "Read limits must be positive.");
            }

            MaxArtifactBytes = maxArtifactBytes;
            MaxRecordCount = maxRecordCount;
            MaxRecordBytes = maxRecordBytes;
            MaxBlobBytes = maxBlobBytes;
            MaxStringLength = maxStringLength;
        }

        public long MaxArtifactBytes { get; }

        public int MaxRecordCount { get; }

        public int MaxRecordBytes { get; }

        public int MaxBlobBytes { get; }

        public int MaxStringLength { get; }
    }
}
