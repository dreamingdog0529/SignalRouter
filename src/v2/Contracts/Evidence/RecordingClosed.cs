using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The close reason an E7 declares: <c>Completed</c> or
    /// <c>Incomplete(reason)</c> (guarantees.md §5.9). A reader honors the
    /// declaration only after verifying closure itself.
    /// </summary>
    public readonly struct RecordingCloseReason : IEquatable<RecordingCloseReason>
    {
        private readonly IncompleteReason reason;

        private RecordingCloseReason(bool completed, IncompleteReason reason)
        {
            IsCompleted = completed;
            this.reason = reason;
        }

        public static RecordingCloseReason Completed => new RecordingCloseReason(true, default);

        public static RecordingCloseReason Incomplete(IncompleteReason reason)
        {
            if (reason.IsDefault)
            {
                throw new ArgumentException(
                    "An Incomplete close requires a reason.", nameof(reason));
            }

            return new RecordingCloseReason(false, reason);
        }

        public bool IsCompleted { get; }

        public IncompleteReason Reason =>
            !IsCompleted && !reason.IsDefault
                ? reason
                : throw new InvalidOperationException("Only an Incomplete close carries a reason.");

        public bool Equals(RecordingCloseReason other) =>
            IsCompleted == other.IsCompleted && reason.Equals(other.reason);

        public override bool Equals(object? obj) => obj is RecordingCloseReason other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(IsCompleted ? 1 : 0, reason.GetHashCode());

        public override string ToString() => IsCompleted ? "Completed" : $"Incomplete({reason})";

        public static bool operator ==(RecordingCloseReason left, RecordingCloseReason right) => left.Equals(right);

        public static bool operator !=(RecordingCloseReason left, RecordingCloseReason right) => !left.Equals(right);
    }

    /// <summary>
    /// E7 — the close fence (guarantees.md §5.9). Carries closure material the reader
    /// can recompute: the ReplayEvidence cut count (E1 and E7 included) and the
    /// reachable-ContentId set (every ContentId referenced by any cut). A
    /// self-declared boolean is never sufficient.
    /// </summary>
    public sealed class RecordingClosed : EvidenceCut
    {
        public RecordingClosed(
            EvidenceSequence sequence,
            RecordingCloseReason reason,
            long declaredEventCount,
            ContentId finalCheckpoint,
            ValueList<ContentId> declaredReachableContentIds)
            : base(sequence)
        {
            if (declaredEventCount < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(declaredEventCount),
                    "A closed artifact contains at least E1 and E7.");
            }

            if (finalCheckpoint.IsDefault)
            {
                throw new ArgumentException(
                    "E7 requires a non-default final checkpoint ContentId.", nameof(finalCheckpoint));
            }

            Reason = reason;
            DeclaredEventCount = declaredEventCount;
            FinalCheckpoint = finalCheckpoint;
            DeclaredReachableContentIds = declaredReachableContentIds
                ?? throw new ArgumentNullException(nameof(declaredReachableContentIds));
        }

        public override EvidenceCutKind Kind => EvidenceCutKind.RecordingClosed;

        public RecordingCloseReason Reason { get; }

        /// <summary>The declared number of ReplayEvidence cuts, E1 and E7 themselves included.</summary>
        public long DeclaredEventCount { get; }

        public ContentId FinalCheckpoint { get; }

        /// <summary>The declared reachable-ContentId set the reader recomputes and verifies.</summary>
        public ValueList<ContentId> DeclaredReachableContentIds { get; }
    }
}
