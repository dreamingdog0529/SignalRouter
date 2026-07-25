using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>The kind of a replay comparison answer (guarantees.md §3.3).</summary>
    public enum ReplayComparisonKind
    {
        /// <summary>Typed exact comparison over the profile's field set matched.</summary>
        Equal,

        /// <summary>The comparison was evaluable and did not match.</summary>
        Diverged,

        /// <summary>The comparison cannot be evaluated.</summary>
        Incomparable,
    }

    /// <summary>
    /// A replay comparison outcome. <c>Incomparable</c> is distinct from
    /// <c>Diverged</c> and MUST NOT be collapsed into it (guarantees.md §2, §3.3):
    /// there is deliberately no conversion between the two.
    /// </summary>
    public readonly struct ReplayComparisonOutcome : IEquatable<ReplayComparisonOutcome>
    {
        private readonly IncomparableReason reason;

        private ReplayComparisonOutcome(ReplayComparisonKind kind, IncomparableReason reason)
        {
            Kind = kind;
            this.reason = reason;
        }

        public static ReplayComparisonOutcome Equal => new ReplayComparisonOutcome(ReplayComparisonKind.Equal, default);

        public static ReplayComparisonOutcome Diverged => new ReplayComparisonOutcome(ReplayComparisonKind.Diverged, default);

        public static ReplayComparisonOutcome Incomparable(IncomparableReason reason)
        {
            if (reason.IsDefault)
            {
                throw new ArgumentException(
                    "An Incomparable outcome requires a reason.", nameof(reason));
            }

            return new ReplayComparisonOutcome(ReplayComparisonKind.Incomparable, reason);
        }

        public ReplayComparisonKind Kind { get; }

        public IncomparableReason Reason =>
            Kind == ReplayComparisonKind.Incomparable
                ? reason
                : throw new InvalidOperationException("Only an Incomparable outcome carries a reason.");

        public bool Equals(ReplayComparisonOutcome other) => Kind == other.Kind && reason.Equals(other.reason);

        public override bool Equals(object? obj) => obj is ReplayComparisonOutcome other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes((int)Kind, reason.GetHashCode());

        public override string ToString() =>
            Kind == ReplayComparisonKind.Incomparable ? $"Incomparable({reason})" : Kind.ToString();

        public static bool operator ==(ReplayComparisonOutcome left, ReplayComparisonOutcome right) => left.Equals(right);

        public static bool operator !=(ReplayComparisonOutcome left, ReplayComparisonOutcome right) => !left.Equals(right);
    }
}
