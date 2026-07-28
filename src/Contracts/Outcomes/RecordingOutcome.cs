using System;

namespace SignalRouter.Contracts
{
    /// <summary>The reader's classification of a recording artifact (guarantees.md §3.2, §6.3).</summary>
    public enum RecordingOutcomeKind
    {
        /// <summary>E7 present with Completed, closure verification passes, no unresolved commitments.</summary>
        Completed,

        /// <summary>E7 present and durable, declaring why the contract was not fully met.</summary>
        Incomplete,

        /// <summary>E7 absent (or untrustworthy); the reader infers interruption. Never self-declared.</summary>
        Interrupted,

        /// <summary>E1 (or its base snapshot) never became durable; no artifact exists.</summary>
        OpenFailed,
    }

    /// <summary>
    /// A recording outcome: the kind plus, exactly for <c>Incomplete</c>, its reason
    /// (guarantees.md §3.2). The factories make a reasonless Incomplete and a
    /// reasoned non-Incomplete unrepresentable.
    /// </summary>
    public readonly struct RecordingOutcome : IEquatable<RecordingOutcome>
    {
        private readonly IncompleteReason reason;

        private RecordingOutcome(RecordingOutcomeKind kind, IncompleteReason reason)
        {
            Kind = kind;
            this.reason = reason;
        }

        public static RecordingOutcome Completed => new RecordingOutcome(RecordingOutcomeKind.Completed, default);

        public static RecordingOutcome Interrupted => new RecordingOutcome(RecordingOutcomeKind.Interrupted, default);

        public static RecordingOutcome OpenFailed => new RecordingOutcome(RecordingOutcomeKind.OpenFailed, default);

        public static RecordingOutcome Incomplete(IncompleteReason reason)
        {
            if (reason.IsDefault)
            {
                throw new ArgumentException(
                    "An Incomplete outcome requires a reason.", nameof(reason));
            }

            return new RecordingOutcome(RecordingOutcomeKind.Incomplete, reason);
        }

        public RecordingOutcomeKind Kind { get; }

        public IncompleteReason Reason =>
            Kind == RecordingOutcomeKind.Incomplete
                ? reason
                : throw new InvalidOperationException("Only an Incomplete outcome carries a reason.");

        public bool Equals(RecordingOutcome other) => Kind == other.Kind && reason.Equals(other.reason);

        public override bool Equals(object? obj) => obj is RecordingOutcome other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes((int)Kind, reason.GetHashCode());

        public override string ToString() =>
            Kind == RecordingOutcomeKind.Incomplete ? $"Incomplete({reason})" : Kind.ToString();

        public static bool operator ==(RecordingOutcome left, RecordingOutcome right) => left.Equals(right);

        public static bool operator !=(RecordingOutcome left, RecordingOutcome right) => !left.Equals(right);
    }
}
