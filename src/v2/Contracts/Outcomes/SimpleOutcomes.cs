namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The terminal of an admitted mutation interaction (guarantees.md §3.1). Exactly
    /// one applies; <see cref="OutcomeUnknown"/> is first-class honest uncertainty and
    /// MUST never be converted into an invented terminal (guarantees.md §2).
    /// </summary>
    public enum InteractionOutcome
    {
        /// <summary>The capability invocation completed per its completion profile.</summary>
        Succeeded,

        /// <summary>Validation refused the invocation before any effect was permitted; zero effects.</summary>
        Rejected,

        /// <summary>An effect was permitted and execution stopped on a fault — except the pre-effect evidence failure (guarantees.md §3.1).</summary>
        Faulted,

        /// <summary>Cancellation was observed; the phase records whether any effect was permitted.</summary>
        Cancelled,

        /// <summary>The runtime cannot prove which of the above occurred.</summary>
        OutcomeUnknown,
    }

    /// <summary>The resolution of an armed wait predicate (guarantees.md §5.6).</summary>
    public enum PredicateResolution
    {
        Satisfied,
        TimedOut,
        Cancelled,
        Faulted,
        Unknown,
    }

    /// <summary>
    /// The final evaluation of a capability postcondition, embedded in E4
    /// (guarantees.md §5.4).
    /// </summary>
    public enum PostconditionResult
    {
        Satisfied,
        False,
        TimedOut,
        Unknown,
    }

    /// <summary>When cancellation was observed relative to the effect (guarantees.md §5.7).</summary>
    public enum CancellationPhase
    {
        BeforeEffect,
        DuringEffect,
        AfterEffect,
    }

    /// <summary>
    /// The answer of a recording control operation as reported to the caller.
    /// <c>Failed</c> deliberately does not name an artifact state — a sink fault may
    /// make the artifact unwritable while the operation still answers
    /// (guarantees.md §3.2).
    /// </summary>
    public enum RecordingControlOutcome
    {
        Succeeded,
        Failed,
    }
}
