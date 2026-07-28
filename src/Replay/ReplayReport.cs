using System;
using SignalRouter.Contracts;

namespace SignalRouter.Replay
{
    /// <summary>The replay comparison mode (recording-replay.md §5.3).</summary>
    public enum ReplayMode
    {
        /// <summary>Typed exact comparison over the full pinned profile — the default.</summary>
        StrictSemantic = 0,

        /// <summary>Canonical-representation (ContentId) equality; the typed diff only explains a mismatch.</summary>
        ExactArtifact = 1,
    }

    /// <summary>
    /// The replay answer (recording-replay.md §6): the first non-Equal
    /// comparison stops the run with a structured report — the recorded
    /// expectation, the actual observation, and the semantic diff, all built
    /// from recording-safe fields only. A full all-Equal run answers Equal with
    /// no stop position.
    /// </summary>
    public sealed class ReplayReport
    {
        private ReplayReport(
            ReplayComparisonOutcome outcome,
            EvidenceSequence? stoppedAt,
            ReplayStopKind? stopKind,
            string? detailCode,
            SemanticDiff? diff)
        {
            Outcome = outcome;
            StoppedAt = stoppedAt;
            StopKind = stopKind;
            DetailCode = detailCode;
            Diff = diff;
        }

        public ReplayComparisonOutcome Outcome { get; }

        /// <summary>The stream position of the stop or divergence; null on a full all-Equal run.</summary>
        public EvidenceSequence? StoppedAt { get; }

        /// <summary>Non-null when the run ended at a pre-scan-planned stop.</summary>
        public ReplayStopKind? StopKind { get; }

        /// <summary>A stable code naming the divergent comparison site (open vocabulary).</summary>
        public string? DetailCode { get; }

        public SemanticDiff? Diff { get; }

        internal static ReplayReport AllEqual() =>
            new ReplayReport(ReplayComparisonOutcome.Equal, null, null, null, null);

        internal static ReplayReport StoppedByPlan(PlannedStop stop) =>
            new ReplayReport(
                stop.Incomparability.HasValue
                    ? ReplayComparisonOutcome.Incomparable(stop.Incomparability.Value)
                    : ReplayComparisonOutcome.Equal,
                stop.Position,
                stop.Kind,
                null,
                null);

        internal static ReplayReport Diverged(
            EvidenceSequence position, string detailCode, SemanticDiff? diff) =>
            new ReplayReport(
                ReplayComparisonOutcome.Diverged, position, null,
                ContractGrammar.ValidateCode(detailCode, nameof(detailCode)), diff);

        internal static ReplayReport Incomparable(
            EvidenceSequence position, IncomparableReason reason) =>
            new ReplayReport(
                ReplayComparisonOutcome.Incomparable(reason), position, null, null, null);
    }
}
