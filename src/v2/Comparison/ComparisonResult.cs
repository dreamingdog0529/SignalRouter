using System;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Comparison
{
    /// <summary>
    /// One comparison's full answer over the canonical replay outcome contracts
    /// (guarantees.md §3.3): the three-valued verdict, with the structured diff
    /// exactly when it diverged — a diff never exists for Equal or Incomparable.
    /// </summary>
    public sealed class ComparisonResult
    {
        private ComparisonResult(ReplayComparisonOutcome outcome, SemanticDiff? diff)
        {
            Outcome = outcome;
            Diff = diff;
        }

        public ReplayComparisonOutcome Outcome { get; }

        public SemanticDiff? Diff { get; }

        public static ComparisonResult Equal { get; } =
            new ComparisonResult(ReplayComparisonOutcome.Equal, null);

        public static ComparisonResult Diverged(SemanticDiff diff) =>
            new ComparisonResult(
                ReplayComparisonOutcome.Diverged,
                diff ?? throw new ArgumentNullException(nameof(diff)));

        public static ComparisonResult Incomparable(IncomparableReason reason) =>
            new ComparisonResult(ReplayComparisonOutcome.Incomparable(reason), null);
    }
}
