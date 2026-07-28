using System;
using SignalRouter.V2.Codec.Recording;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Recording
{
    /// <summary>The declared capacity policy (guarantees.md §8; ADR 0015). Chosen at open, never improvised.</summary>
    public enum RecordingCapacityPolicy
    {
        /// <summary>The default: reaching a bound closes the artifact Incomplete(SizeLimit).</summary>
        CloseIncompleteOnSizeLimit = 0,
    }

    /// <summary>
    /// What the durable coordinator declares before any overflow can occur
    /// (ADR 0015): the comparison-profile document the artifact embeds, the
    /// RecordingSink bounds, and the store discipline.
    /// </summary>
    public sealed class RecordingCoordinatorOptions
    {
        public RecordingCoordinatorOptions(
            ReplayComparisonProfile profile,
            long maxArtifactBytes = 64L * 1024 * 1024,
            int maxEventCount = 65536,
            int maxBlobBytes = 8 * 1024 * 1024,
            RecordingCapacityPolicy capacityPolicy = RecordingCapacityPolicy.CloseIncompleteOnSizeLimit,
            bool allowNonDurableStore = false)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            if (maxArtifactBytes < 1 || maxEventCount < 2 || maxBlobBytes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxArtifactBytes), "Recording bounds must be positive (and allow E1+E7).");
            }

            MaxArtifactBytes = maxArtifactBytes;
            MaxEventCount = maxEventCount;
            MaxBlobBytes = maxBlobBytes;
            CapacityPolicy = capacityPolicy;
            AllowNonDurableStore = allowNonDurableStore;
        }

        /// <summary>The declarative document embedded in the artifact (record kind 0x04, ADR 0016).</summary>
        public ReplayComparisonProfile Profile { get; }

        /// <summary>RecordingSink.MaxArtifactBytes: total file bytes, framing and blobs included.</summary>
        public long MaxArtifactBytes { get; }

        /// <summary>RecordingSink.MaxEventCount: evidence-cut records only.</summary>
        public int MaxEventCount { get; }

        /// <summary>RecordingSink.MaxBlobBytes: one blob's canonical payload.</summary>
        public int MaxBlobBytes { get; }

        public RecordingCapacityPolicy CapacityPolicy { get; }

        /// <summary>
        /// Test-only opt-in: a non-durable store (the memory backend) is refused
        /// at open unless explicitly allowed (ADR 0015/0016).
        /// </summary>
        public bool AllowNonDurableStore { get; }
    }
}
