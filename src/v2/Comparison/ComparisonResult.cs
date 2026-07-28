using System;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Comparison
{
    /// <summary>The three-valued comparison answer (guarantees.md §3.3).</summary>
    public enum ComparisonKind
    {
        Equal,

        Diverged,

        /// <summary>The comparison could not legitimately be performed; never a guess.</summary>
        Incomparable,
    }

    /// <summary>
    /// One comparison verdict. The Incomparable reason vocabulary is open with
    /// the guarantees.md §3.5 codes reserved; every Unevaluable reason code is
    /// also a valid Incomparable reason (§3.3).
    /// </summary>
    public readonly struct ComparisonOutcome : IEquatable<ComparisonOutcome>
    {
        private readonly string? reason;

        private ComparisonOutcome(ComparisonKind kind, string? reason)
        {
            Kind = kind;
            this.reason = reason;
        }

        public static ComparisonOutcome Equal => new ComparisonOutcome(ComparisonKind.Equal, null);

        public static ComparisonOutcome Diverged => new ComparisonOutcome(ComparisonKind.Diverged, null);

        public static ComparisonOutcome Incomparable(string reason) =>
            new ComparisonOutcome(
                ComparisonKind.Incomparable, ContractGrammar.ValidateCode(reason, nameof(reason)));

        public ComparisonKind Kind { get; }

        public string Reason =>
            Kind == ComparisonKind.Incomparable
                ? reason!
                : throw new InvalidOperationException("Only an Incomparable outcome carries a reason.");

        public bool Equals(ComparisonOutcome other) =>
            Kind == other.Kind && string.Equals(reason, other.reason, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ComparisonOutcome other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(
                (int)Kind, reason == null ? 0 : StringComparer.Ordinal.GetHashCode(reason));

        public override string ToString() =>
            Kind == ComparisonKind.Incomparable ? $"Incomparable({reason})" : Kind.ToString();

        public static bool operator ==(ComparisonOutcome left, ComparisonOutcome right) => left.Equals(right);

        public static bool operator !=(ComparisonOutcome left, ComparisonOutcome right) => !left.Equals(right);
    }

    /// <summary>The reserved Incomparable reason codes (guarantees.md §3.5).</summary>
    public static class IncomparableReasons
    {
        public const string UnsupportedProfileVersion = "UnsupportedProfileVersion";

        public const string Incompleteness = "Incompleteness";

        public const string UnknownMandatoryExtension = "UnknownMandatoryExtension";

        public const string MissingMigration = "MissingMigration";

        public const string Contamination = "Contamination";

        public const string CancellationTiming = "CancellationTiming";

        public const string TemporalPredicate = "TemporalPredicate";

        public const string PredicateFault = "PredicateFault";
    }

    /// <summary>
    /// One comparison's full answer: the verdict, with the structured diff
    /// exactly when it diverged — a diff never exists for Equal or Incomparable
    /// (guarantees.md §3.3).
    /// </summary>
    public sealed class ComparisonResult
    {
        private ComparisonResult(ComparisonOutcome outcome, SemanticDiff? diff)
        {
            Outcome = outcome;
            Diff = diff;
        }

        public ComparisonOutcome Outcome { get; }

        public SemanticDiff? Diff { get; }

        public static ComparisonResult Equal { get; } =
            new ComparisonResult(ComparisonOutcome.Equal, null);

        public static ComparisonResult Diverged(SemanticDiff diff) =>
            new ComparisonResult(
                ComparisonOutcome.Diverged,
                diff ?? throw new ArgumentNullException(nameof(diff)));

        public static ComparisonResult Incomparable(string reason) =>
            new ComparisonResult(ComparisonOutcome.Incomparable(reason), null);
    }
}
