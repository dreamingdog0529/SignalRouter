using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The recomputable half of E7 closure verification (guarantees.md §5.9). Blob
    /// existence and digest verification are codec-level facts supplied through
    /// <see cref="ArtifactFacts.ExternalIntegrityFailure"/>.
    /// </summary>
    public enum ClosureCheckResult
    {
        /// <summary>The recomputable closure material matches the evidence.</summary>
        Verified,

        /// <summary>No E7 is present; there is nothing to verify against.</summary>
        MissingClose,

        /// <summary>The declared event count does not equal the ReplayEvidence cut count.</summary>
        EventCountMismatch,

        /// <summary>A ContentId referenced by a cut is missing from the declared reachable set.</summary>
        UnreachableContentId,

        /// <summary>The declared reachable set contains a ContentId no cut references (guarantees.md §5.9 defines the set exactly).</summary>
        SurplusDeclaredContentId,
    }

    /// <summary>
    /// The facts a reader supplies to <see cref="EvidenceSemantics"/>: the durable
    /// evidence cuts in stream order plus the durability/integrity facts only the
    /// storage layer can know. Loss is modeled by absence — the reader only ever sees
    /// durable cuts. Computing these facts (decoding, blob and digest verification)
    /// belongs to the recording codec; fixing the decision tables over them is this
    /// module's job.
    /// </summary>
    public sealed class ArtifactFacts
    {
        public ArtifactFacts(
            bool baseSnapshotDurable,
            ValueList<EvidenceCut> cuts,
            bool externalIntegrityFailure = false)
        {
            BaseSnapshotDurable = baseSnapshotDurable;
            Cuts = cuts ?? throw new ArgumentNullException(nameof(cuts));
            ExternalIntegrityFailure = externalIntegrityFailure;
        }

        /// <summary>Whether the base observation snapshot blob became durable and pinned (guarantees.md §5.1).</summary>
        public bool BaseSnapshotDurable { get; }

        /// <summary>The durable ReplayEvidence cuts in stream order.</summary>
        public ValueList<EvidenceCut> Cuts { get; }

        /// <summary>
        /// True when codec-level integrity verification (blob existence, ContentId
        /// digests, commit markers) failed. The reader supplies this fact; the
        /// decision tables here consume it (guarantees.md §6.3).
        /// </summary>
        public bool ExternalIntegrityFailure { get; }
    }
}
