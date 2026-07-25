using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The kinds of strict-replay stop candidates a pre-scan can derive from the
    /// evidence alone (guarantees.md §5.5–§5.7). Hazards requiring contract knowledge
    /// (e.g. temporal predicates) belong to the replayer's pre-scan, not here.
    /// </summary>
    public enum StaticReplayHazardKind
    {
        /// <summary>E2 + E3 without E4: strict replay stops before permitting this effect (guarantees.md §6.1).</summary>
        OutcomeUnknownShape,

        /// <summary>A contaminated interaction's effect: replay stops before permitting it, not at E5's position (guarantees.md §5.5).</summary>
        ContaminatedEffect,

        /// <summary>A DuringEffect cancellation (guarantees.md §5.7).</summary>
        DuringEffectCancellation,

        /// <summary>A Cancelled terminal with phase AfterEffect (guarantees.md §5.7).</summary>
        CancelledAfterEffectTerminal,

        /// <summary>An E6 resolution other than Satisfied: replay stops before executing the wait (guarantees.md §5.6).</summary>
        PredicateResolutionNotSatisfied,
    }

    /// <summary>
    /// One strict-replay stop candidate. <see cref="Reason"/> carries the
    /// <see cref="IncomparableReason"/> the stop reports when the spec names one;
    /// stops that are plain "stop before executing" (timing out of tier) carry none.
    /// The actual stop decision and report belong to the replay layer.
    /// </summary>
    public sealed class StaticReplayHazard : IEquatable<StaticReplayHazard>
    {
        public StaticReplayHazard(
            StaticReplayHazardKind kind,
            EvidenceSequence position,
            RequestId? request = null,
            OperationId? operation = null,
            IncomparableReason? reason = null)
        {
            if (reason.HasValue && reason.Value.IsDefault)
            {
                throw new ArgumentException("A present reason must be non-default.", nameof(reason));
            }

            Kind = kind;
            Position = position;
            Request = request;
            Operation = operation;
            Reason = reason;
        }

        public StaticReplayHazardKind Kind { get; }

        /// <summary>The stream position replay stops at — before permitting the affected effect or wait.</summary>
        public EvidenceSequence Position { get; }

        public RequestId? Request { get; }

        public OperationId? Operation { get; }

        public IncomparableReason? Reason { get; }

        public bool Equals(StaticReplayHazard? other) =>
            other != null &&
            Kind == other.Kind &&
            Position.Equals(other.Position) &&
            Nullable.Equals(Request, other.Request) &&
            Nullable.Equals(Operation, other.Operation) &&
            Nullable.Equals(Reason, other.Reason);

        public override bool Equals(object? obj) => Equals(obj as StaticReplayHazard);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes((int)Kind, Position.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, Request?.GetHashCode() ?? 0);
            hash = ContractGrammar.CombineHashes(hash, Operation?.GetHashCode() ?? 0);
            return ContractGrammar.CombineHashes(hash, Reason?.GetHashCode() ?? 0);
        }

        public override string ToString() => $"{Kind}@{Position}";
    }
}
