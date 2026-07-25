using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// Why a live predicate evaluation could not be performed (verification.md §2.3,
    /// guarantees.md §3.5). The vocabulary deliberately mirrors the CompletenessMap
    /// reasons (observation-state.md §3): an evaluation is unevaluable precisely
    /// because of a completeness condition on its input.
    /// </summary>
    public readonly struct UnevaluableReason : IEquatable<UnevaluableReason>
    {
        private readonly string? value;

        public UnevaluableReason(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        /// <summary>A referenced field is withheld by redaction policy.</summary>
        public static UnevaluableReason Redacted => new UnevaluableReason("Redacted");

        /// <summary>A referenced field is outside the view's scope or exposure policy.</summary>
        public static UnevaluableReason OutOfScope => new UnevaluableReason("OutOfScope");

        /// <summary>A referenced region is incomplete (e.g. virtualized or budget-truncated).</summary>
        public static UnevaluableReason Incompleteness => new UnevaluableReason("Incompleteness");

        /// <summary>The predicate or source contract version is not supported.</summary>
        public static UnevaluableReason UnsupportedContract => new UnevaluableReason("UnsupportedContract");

        /// <summary>A referenced state source produced no document.</summary>
        public static UnevaluableReason SourceUnavailable => new UnevaluableReason("SourceUnavailable");

        /// <summary>A sampled source's document is older than its freshness bound.</summary>
        public static UnevaluableReason Stale => new UnevaluableReason("Stale");

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default UnevaluableReason carries no value.");

        /// <summary>True when this is one of the guarantees.md §3.5 reserved codes.</summary>
        public bool IsCanonical =>
            Equals(Redacted) || Equals(OutOfScope) || Equals(Incompleteness) ||
            Equals(UnsupportedContract) || Equals(SourceUnavailable) || Equals(Stale);

        public bool Equals(UnevaluableReason other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is UnevaluableReason other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(UnevaluableReason left, UnevaluableReason right) => left.Equals(right);

        public static bool operator !=(UnevaluableReason left, UnevaluableReason right) => !left.Equals(right);
    }
}
