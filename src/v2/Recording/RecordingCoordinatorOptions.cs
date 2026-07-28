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

    /// <summary>The declared E5 policy (guarantees.md §5.5; ADR 0015). Chosen at open, never improvised.</summary>
    public enum ExternalMutationPolicy
    {
        /// <summary>The default: the barrier is recorded and the artifact continues (§5.3 fresh-materialization rule).</summary>
        BarrierContinue = 0,

        /// <summary>The barrier is recorded and the artifact terminates Incomplete(ExternalMutation).</summary>
        Terminate = 1,
    }

    /// <summary>
    /// What the durable coordinator declares before any overflow can occur
    /// (ADR 0015): the comparison-profile document the artifact embeds, the
    /// RecordingSink bounds, and the store discipline.
    /// </summary>
    public sealed class RecordingCoordinatorOptions
    {
        /// <summary>StateStore.MaxChainLength (security-resources.md §5): the store's chain ceiling.</summary>
        public const int StoreMaxChainLength = 32;

        public RecordingCoordinatorOptions(
            ReplayComparisonProfile profile,
            long maxArtifactBytes = 64L * 1024 * 1024,
            int maxEventCount = 65536,
            int maxBlobBytes = 8 * 1024 * 1024,
            RecordingCapacityPolicy capacityPolicy = RecordingCapacityPolicy.CloseIncompleteOnSizeLimit,
            bool allowNonDurableStore = false,
            ExternalMutationPolicy externalMutationPolicy = ExternalMutationPolicy.BarrierContinue,
            int maxDeltaChainLength = 8,
            long timelineByteBudget = 0)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            if (maxArtifactBytes < 1 || maxEventCount < 2 || maxBlobBytes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxArtifactBytes), "Recording bounds must be positive (and allow E1+E7).");
            }

            if (maxDeltaChainLength < 0 || maxDeltaChainLength > StoreMaxChainLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDeltaChainLength),
                    "The delta chain bound is 0 (no deltas) through StateStore.MaxChainLength.");
            }

            if (timelineByteBudget < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timelineByteBudget), "The timeline budget is 0 (lane off) or positive.");
            }

            MaxArtifactBytes = maxArtifactBytes;
            MaxEventCount = maxEventCount;
            MaxBlobBytes = maxBlobBytes;
            CapacityPolicy = capacityPolicy;
            AllowNonDurableStore = allowNonDurableStore;
            ExternalMutation = externalMutationPolicy;
            MaxDeltaChainLength = maxDeltaChainLength;
            TimelineByteBudget = timelineByteBudget;
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

        /// <summary>How an E5 barrier disposes the artifact (guarantees.md §5.5).</summary>
        public ExternalMutationPolicy ExternalMutation { get; }

        /// <summary>
        /// Test-only opt-in: a non-durable store (the memory backend) is refused
        /// at open unless explicitly allowed (ADR 0015/0016).
        /// </summary>
        public bool AllowNonDurableStore { get; }

        /// <summary>
        /// The declared bound on delta chains between full checkpoints
        /// (recording-replay.md §4); recordings honor min(this,
        /// StateStore.MaxChainLength). Zero writes full blobs only. Chain
        /// length is storage encoding, never comparison semantics — it lives
        /// here, not in the comparison-profile document.
        /// </summary>
        public int MaxDeltaChainLength { get; }

        /// <summary>
        /// TimelineTrack byte-rate cap (recording-replay.md §3): total framed
        /// timeline bytes per artifact. Zero disables the lane. Overflow drops
        /// events; loss is marked with a gap record at close.
        /// </summary>
        public long TimelineByteBudget { get; }
    }
}
