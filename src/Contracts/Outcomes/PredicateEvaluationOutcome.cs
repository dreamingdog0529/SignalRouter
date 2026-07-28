using System;

namespace SignalRouter.Contracts
{
    /// <summary>The kind of a predicate evaluation answer (verification.md §2.3).</summary>
    public enum PredicateEvaluationKind
    {
        /// <summary>The predicate evaluated true against the materialization.</summary>
        Satisfied,

        /// <summary>It evaluated false.</summary>
        False,

        /// <summary>It could not be evaluated.</summary>
        Unevaluable,
    }

    /// <summary>
    /// A predicate evaluation answer (verification.md §2.3; recorded in E8,
    /// guarantees.md §5.10). <c>Incomparable</c> does not exist on the live side; it
    /// is the replay-side mapping of <c>Unevaluable</c>.
    /// </summary>
    public readonly struct PredicateEvaluationOutcome : IEquatable<PredicateEvaluationOutcome>
    {
        private readonly UnevaluableReason reason;

        private PredicateEvaluationOutcome(PredicateEvaluationKind kind, UnevaluableReason reason)
        {
            Kind = kind;
            this.reason = reason;
        }

        public static PredicateEvaluationOutcome Satisfied =>
            new PredicateEvaluationOutcome(PredicateEvaluationKind.Satisfied, default);

        public static PredicateEvaluationOutcome False =>
            new PredicateEvaluationOutcome(PredicateEvaluationKind.False, default);

        public static PredicateEvaluationOutcome Unevaluable(UnevaluableReason reason)
        {
            if (reason.IsDefault)
            {
                throw new ArgumentException(
                    "An Unevaluable outcome requires a reason.", nameof(reason));
            }

            return new PredicateEvaluationOutcome(PredicateEvaluationKind.Unevaluable, reason);
        }

        public PredicateEvaluationKind Kind { get; }

        public UnevaluableReason Reason =>
            Kind == PredicateEvaluationKind.Unevaluable
                ? reason
                : throw new InvalidOperationException("Only an Unevaluable outcome carries a reason.");

        public bool Equals(PredicateEvaluationOutcome other) => Kind == other.Kind && reason.Equals(other.reason);

        public override bool Equals(object? obj) => obj is PredicateEvaluationOutcome other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes((int)Kind, reason.GetHashCode());

        public override string ToString() =>
            Kind == PredicateEvaluationKind.Unevaluable ? $"Unevaluable({reason})" : Kind.ToString();

        public static bool operator ==(PredicateEvaluationOutcome left, PredicateEvaluationOutcome right) => left.Equals(right);

        public static bool operator !=(PredicateEvaluationOutcome left, PredicateEvaluationOutcome right) => !left.Equals(right);
    }
}
