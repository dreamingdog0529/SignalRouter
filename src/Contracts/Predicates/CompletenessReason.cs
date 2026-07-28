namespace SignalRouter.Contracts
{
    /// <summary>
    /// Why a region of an observation materialization is not present
    /// (observation-state.md §3). Completeness is local, never a global boolean;
    /// every omission carries one of these reasons.
    /// </summary>
    public enum CompletenessReason
    {
        /// <summary>Subtree not materialized (e.g. a virtualized list region).</summary>
        Virtualized,

        /// <summary>Field/value withheld by redaction policy.</summary>
        Redacted,

        /// <summary>Outside the view's scope or exposure policy.</summary>
        OutOfScope,

        /// <summary>A per-pump or per-view budget cut materialization short.</summary>
        BudgetTruncated,

        /// <summary>A state source produced no document.</summary>
        SourceUnavailable,

        /// <summary>A sampled source's document is older than its declared freshness bound.</summary>
        Stale,

        /// <summary>The source's contract version is not supported by this view contract.</summary>
        UnsupportedContract,
    }

    /// <summary>Maps completeness conditions onto the evaluation-answer vocabulary (guarantees.md §3.5).</summary>
    public static class CompletenessReasons
    {
        public static UnevaluableReason ToUnevaluable(CompletenessReason reason)
        {
            switch (reason)
            {
                case CompletenessReason.Redacted:
                    return UnevaluableReason.Redacted;
                case CompletenessReason.OutOfScope:
                    return UnevaluableReason.OutOfScope;
                case CompletenessReason.SourceUnavailable:
                    return UnevaluableReason.SourceUnavailable;
                case CompletenessReason.Stale:
                    return UnevaluableReason.Stale;
                case CompletenessReason.UnsupportedContract:
                    return UnevaluableReason.UnsupportedContract;
                default:
                    return UnevaluableReason.Incompleteness;
            }
        }
    }
}
