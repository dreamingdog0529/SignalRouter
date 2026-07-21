using System;
using System.Collections.Generic;

namespace SignalRouter
{
    // Resolves the in-memory plaintext for a secret reference recorded as
    // {"$secret":"<name>@<version>/<argument>"} (design §16.1 step 2, ADR 0005).
    // The requestId scopes per-occurrence lookups; the key alone is stable across
    // occurrences. Returning false means the secret is unavailable and replay
    // reports a divergence; resolved values must never be null-kinded because
    // schema v1 arguments are always string, boolean, or number scalars.
    public interface IInteractionSecretResolver
    {
        bool TryResolve(string requestId, string key, out InteractionValue? value);
    }

    public enum InteractionReplayError
    {
        SessionEpochMismatch = 0,
        ReplayReentrancy = 1,
        DispatcherBusy = 2,
        RecorderAttached = 3,
        SecretResolverMissing = 4,
        SecretResolverContract = 5,
    }

    // Thrown when replay cannot run or continue for reasons that are not evidence
    // about the recorded behavior: caller misconfiguration or a broken resolver
    // contract. Genuine differences between the recording and the current build
    // are never exceptions; they are reported as a divergence (design §16.1).
    public sealed class InteractionReplayException : Exception
    {
        public InteractionReplayException(InteractionReplayError error, string message)
            : this(error, message, null)
        {
        }

        public InteractionReplayException(
            InteractionReplayError error,
            string message,
            Exception? innerException)
            : base(ValidateMessage(message), innerException)
        {
            InteractionContract.RequireDefinedEnum(error, nameof(error));
            Error = error;
        }

        public InteractionReplayError Error { get; }

        private static string ValidateMessage(string message)
        {
            InteractionContract.RequireMessage(message, nameof(message));
            return message;
        }
    }

    public enum InteractionReplayOutcome
    {
        Completed = 0,
        Diverged = 1,
        Stopped = 2,
    }

    public enum InteractionReplayStopReason
    {
        // The next entry has no terminal event (design §15.1): strict replay stops
        // before it; no recovery policy is defined in v1.
        OutcomeUnknown = 0,

        // The next entry was cancelled after its stages began. Its timing and
        // partial effects are not reproducible, so strict replay stops before it
        // instead of manufacturing a guaranteed false divergence.
        CancelledDuringExecution = 1,

        // A replayed stage enqueued a continuation. Schema v1 carries no parent
        // linkage, so the continuation cannot be matched against recorded entries;
        // the suppressed continuation is never executed and replay stops after
        // fully verifying the entry that requested it.
        ContinuationRequested = 2,
    }

    public enum InteractionReplayDivergenceKind
    {
        // §16.1 step 1: the recorded command name@version is not in the catalog.
        CommandNotInCatalog = 0,

        // The recorded arguments no longer fit the current catalog schema: an
        // unknown argument, a missing required argument, a scalar of the wrong
        // type, a secret marker for a non-sensitive argument, plaintext for a
        // now-sensitive argument, or a resolved secret of the wrong kind.
        ArgumentSchemaMismatch = 1,

        // §16.1 step 2: the resolver could not supply the referenced secret.
        SecretUnavailable = 2,

        // The catalog codec rejected the recorded arguments.
        ArgumentsNotDecodable = 3,

        // §16.1 step 3/7: the recorded before-state hashes do not match the
        // replay-time state (before dispatch, or defensively on the dispatcher's
        // own capture).
        BeforeStateMismatch = 4,

        // §16.1 step 5.
        StatusMismatch = 5,

        // The re-dispatched Rejected entry rejected with a different code.
        RejectionCodeMismatch = 6,

        // §16.1 step 6 (§12.2): stable fault codes differ (null-sensitive).
        FaultCodeMismatch = 7,

        // §16.1 step 6: the stage array of a Faulted entry differs, or a
        // pre-start-cancelled entry replayed with stages.
        StageProgressMismatch = 8,

        // §16.1 step 7.
        AfterStateMismatch = 9,

        // A re-dispatched Rejected or pre-start-cancelled entry changed
        // probe-observable state, violating the zero-side-effect guarantee
        // (design §8.1, §16.1).
        UnexpectedStateChange = 10,
    }

    // Identifies a recorded entry inside a replay report without carrying its
    // argument payload: ArgumentsJson may contain plaintext for an argument the
    // current catalog marks sensitive (sensitivity can upgrade, §13.3), and a
    // report that callers forward must never republish it (§19).
    public sealed record InteractionReplayEntryRef
    {
        public InteractionReplayEntryRef(
            long sequence,
            string requestId,
            string commandName,
            int commandVersion,
            string targetId)
        {
            if (sequence <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequence),
                    sequence,
                    "Sequence numbers must be positive.");
            }

            InteractionContract.RequireIdentifier(requestId, nameof(requestId));
            InteractionContract.RequireIdentifier(commandName, nameof(commandName));
            if (commandVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandVersion),
                    commandVersion,
                    "Command versions must be positive.");
            }

            InteractionContract.RequireTargetId(targetId, nameof(targetId));
            Sequence = sequence;
            RequestId = requestId;
            CommandName = commandName;
            CommandVersion = commandVersion;
            TargetId = targetId;
        }

        public long Sequence { get; }

        public string RequestId { get; }

        public string CommandName { get; }

        public int CommandVersion { get; }

        public string TargetId { get; }

        internal static InteractionReplayEntryRef From(RecordedInteraction entry)
        {
            return new InteractionReplayEntryRef(
                entry.Sequence,
                entry.RequestId,
                entry.CommandName,
                entry.CommandVersion,
                entry.TargetId);
        }
    }

    // One probe's hash-level difference (ADR 0005: recordings support hash-level
    // divergence only until snapshots are retained, §14.1). A null hash means the
    // probe is absent on that side entirely.
    public sealed record InteractionReplayStateDifference
    {
        public InteractionReplayStateDifference(
            string probeId,
            string? expectedHash,
            string? actualHash)
        {
            InteractionContract.RequireIdentifier(probeId, nameof(probeId));
            InteractionContract.RequireOptionalIdentifier(expectedHash, nameof(expectedHash));
            InteractionContract.RequireOptionalIdentifier(actualHash, nameof(actualHash));
            if (expectedHash == null && actualHash == null)
            {
                throw new ArgumentException(
                    "A state difference requires at least one hash.",
                    nameof(actualHash));
            }

            if (expectedHash != null
                && actualHash != null
                && string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A state difference requires the hashes to differ.",
                    nameof(actualHash));
            }

            ProbeId = probeId;
            ExpectedHash = expectedHash;
            ActualHash = actualHash;
        }

        public string ProbeId { get; }

        public string? ExpectedHash { get; }

        public string? ActualHash { get; }
    }

    // The structured report of the first divergence (design §16.1): the recorded
    // expectation, the sanitized actual outcome, and the hash-level state
    // differences. Actual deliberately reuses RecordedOutcome — the projection of
    // a result that a recording is allowed to persist — so the report can never
    // carry exception types, messages, stack traces, or argument plaintext.
    public sealed record InteractionReplayDivergence
    {
        internal InteractionReplayDivergence(
            InteractionReplayEntryRef entry,
            InteractionReplayDivergenceKind kind,
            string? argumentName,
            string? secretKey,
            RecordedOutcome expected,
            RecordedOutcome? actual,
            IEnumerable<InteractionReplayStateDifference> stateDifferences)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (expected == null)
            {
                throw new ArgumentNullException(nameof(expected));
            }

            InteractionContract.RequireDefinedEnum(kind, nameof(kind));
            InteractionContract.RequireOptionalIdentifier(argumentName, nameof(argumentName));
            InteractionContract.RequireOptionalIdentifier(secretKey, nameof(secretKey));
            if ((argumentName != null)
                != (kind == InteractionReplayDivergenceKind.ArgumentSchemaMismatch))
            {
                throw new ArgumentException(
                    "An argument name is carried exactly by argument-schema divergences.",
                    nameof(argumentName));
            }

            if ((secretKey != null)
                != (kind == InteractionReplayDivergenceKind.SecretUnavailable))
            {
                throw new ArgumentException(
                    "A secret key is carried exactly by secret-unavailable divergences.",
                    nameof(secretKey));
            }

            var differences = EquatableList<InteractionReplayStateDifference>.Create(
                stateDifferences,
                nameof(stateDifferences),
                "State differences must not contain null.");
            if ((differences.Count != 0) != IsStateKind(kind))
            {
                throw new ArgumentException(
                    "State differences are carried exactly by state divergences.",
                    nameof(stateDifferences));
            }

            ValidateActualPresence(kind, actual);
            Entry = entry;
            Kind = kind;
            ArgumentName = argumentName;
            SecretKey = secretKey;
            Expected = expected;
            Actual = actual;
            StateDifferences = differences;
        }

        public InteractionReplayEntryRef Entry { get; }

        public InteractionReplayDivergenceKind Kind { get; }

        // The offending argument name for ArgumentSchemaMismatch; the value itself
        // is never reported.
        public string? ArgumentName { get; }

        // The unresolvable secret key for SecretUnavailable; never a secret value.
        public string? SecretKey { get; }

        // The recorded expectation (the entry's terminal outcome).
        public RecordedOutcome Expected { get; }

        // The sanitized observed outcome; null when the divergence was detected
        // before anything was dispatched.
        public RecordedOutcome? Actual { get; }

        // Per-probe hash differences. For Before/AfterStateMismatch the expected
        // side is the recording; for UnexpectedStateChange it is the pre-dispatch
        // reading the dispatch was required to preserve.
        public EquatableList<InteractionReplayStateDifference> StateDifferences { get; }

        private static bool IsStateKind(InteractionReplayDivergenceKind kind)
        {
            return kind == InteractionReplayDivergenceKind.BeforeStateMismatch
                || kind == InteractionReplayDivergenceKind.AfterStateMismatch
                || kind == InteractionReplayDivergenceKind.UnexpectedStateChange;
        }

        private static void ValidateActualPresence(
            InteractionReplayDivergenceKind kind,
            RecordedOutcome? actual)
        {
            switch (kind)
            {
                case InteractionReplayDivergenceKind.CommandNotInCatalog:
                case InteractionReplayDivergenceKind.ArgumentSchemaMismatch:
                case InteractionReplayDivergenceKind.SecretUnavailable:
                case InteractionReplayDivergenceKind.ArgumentsNotDecodable:
                    if (actual != null)
                    {
                        throw new ArgumentException(
                            "Pre-dispatch divergences must not carry an actual outcome.",
                            nameof(actual));
                    }

                    break;
                case InteractionReplayDivergenceKind.BeforeStateMismatch:
                    // Null when detected before dispatch (§16.1 step 3); present when
                    // the defensive re-check against the dispatcher's own capture
                    // detected it after dispatch.
                    break;
                default:
                    if (actual == null)
                    {
                        throw new ArgumentException(
                            "Post-dispatch divergences require an actual outcome.",
                            nameof(actual));
                    }

                    break;
            }
        }
    }

    public sealed record InteractionReplayReport
    {
        internal InteractionReplayReport(
            InteractionReplayOutcome outcome,
            int totalInteractions,
            int verifiedInteractions,
            InteractionReplayStopReason? stopReason,
            InteractionReplayEntryRef? stoppedBefore,
            InteractionReplayDivergence? divergence)
        {
            InteractionContract.RequireDefinedEnum(outcome, nameof(outcome));
            if (totalInteractions < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(totalInteractions),
                    totalInteractions,
                    "The interaction count must be non-negative.");
            }

            if (verifiedInteractions < 0 || verifiedInteractions > totalInteractions)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(verifiedInteractions),
                    verifiedInteractions,
                    "Verified interactions must lie within the recording.");
            }

            if (stopReason != null)
            {
                InteractionContract.RequireDefinedEnum(stopReason.Value, nameof(stopReason));
            }

            switch (outcome)
            {
                case InteractionReplayOutcome.Completed:
                    if (verifiedInteractions != totalInteractions)
                    {
                        throw new ArgumentException(
                            "A completed replay must have verified every interaction.",
                            nameof(verifiedInteractions));
                    }

                    RequireNoStop(stopReason, stoppedBefore);
                    RequireNoDivergence(divergence);
                    break;
                case InteractionReplayOutcome.Diverged:
                    if (divergence == null)
                    {
                        throw new ArgumentException(
                            "A diverged replay requires divergence information.",
                            nameof(divergence));
                    }

                    if (verifiedInteractions >= totalInteractions)
                    {
                        throw new ArgumentException(
                            "A diverged replay cannot have verified every interaction.",
                            nameof(verifiedInteractions));
                    }

                    RequireNoStop(stopReason, stoppedBefore);
                    break;
                default:
                    if (stopReason == null)
                    {
                        throw new ArgumentException(
                            "A stopped replay requires a stop reason.",
                            nameof(stopReason));
                    }

                    RequireNoDivergence(divergence);
                    if (stoppedBefore == null)
                    {
                        // Only a continuation requested by the recording's final entry
                        // leaves nothing to stop before.
                        if (stopReason != InteractionReplayStopReason.ContinuationRequested
                            || verifiedInteractions != totalInteractions)
                        {
                            throw new ArgumentException(
                                "A stopped replay must identify the first entry it did not replay.",
                                nameof(stoppedBefore));
                        }
                    }
                    else if (verifiedInteractions >= totalInteractions)
                    {
                        throw new ArgumentException(
                            "A replay stopped before an entry cannot have verified every interaction.",
                            nameof(verifiedInteractions));
                    }

                    break;
            }

            Outcome = outcome;
            TotalInteractions = totalInteractions;
            VerifiedInteractions = verifiedInteractions;
            StopReason = stopReason;
            StoppedBefore = stoppedBefore;
            Divergence = divergence;
        }

        public InteractionReplayOutcome Outcome { get; }

        public int TotalInteractions { get; }

        // Entries that passed every §16.1 verification step.
        public int VerifiedInteractions { get; }

        // Non-null exactly when the replay stopped.
        public InteractionReplayStopReason? StopReason { get; }

        // The first entry the replay did not execute; null only when the final
        // entry verified but requested a continuation.
        public InteractionReplayEntryRef? StoppedBefore { get; }

        // Non-null exactly when the replay diverged.
        public InteractionReplayDivergence? Divergence { get; }

        private static void RequireNoStop(
            InteractionReplayStopReason? stopReason,
            InteractionReplayEntryRef? stoppedBefore)
        {
            if (stopReason != null || stoppedBefore != null)
            {
                throw new ArgumentException(
                    "Only a stopped replay carries stop information.",
                    nameof(stopReason));
            }
        }

        private static void RequireNoDivergence(InteractionReplayDivergence? divergence)
        {
            if (divergence != null)
            {
                throw new ArgumentException(
                    "Only a diverged replay carries divergence information.",
                    nameof(divergence));
            }
        }
    }
}
