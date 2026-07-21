using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRouter
{
    // Strict replayer (design §16.1): re-executes a recording one entry at a time
    // through the normal dispatcher and verifies, in order, catalog existence,
    // secret resolution, the before-state hashes, the terminal status, the
    // per-status detail (rejection code; fault code and stage progress), and the
    // after-state hashes. Replay stops at the first divergence and returns a
    // structured report; entries whose behavior is not reproducible (unknown
    // outcomes, mid-execution cancellations, requested continuations) stop the
    // replay instead of manufacturing a false divergence.
    //
    // Preconditions are exceptions, not divergences: the caller must reconstruct
    // the registry with the recording's session ID (ADR 0005), hand over an idle,
    // recorder-free dispatcher, and supply a resolver when the replayed prefix
    // references secrets. The dispatcher is exclusively leased for the duration:
    // external dispatches are rejected and replayed-stage continuations are
    // suppressed rather than double-executed.
    public static class InteractionReplayer
    {
        public static async ValueTask<InteractionReplayReport> ReplayAsync(
            InteractionRecording recording,
            InteractionDispatcher dispatcher,
            IInteractionSecretResolver? secretResolver = null,
            CancellationToken cancellationToken = default)
        {
            if (recording == null)
            {
                throw new ArgumentNullException(nameof(recording));
            }

            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            if (!string.Equals(
                recording.Session.SessionId,
                dispatcher.Registry.SessionEpoch,
                StringComparison.Ordinal))
            {
                // Both built-in probes hash the session epoch (ADR 0001), so a
                // mismatched registry could never reproduce a recorded hash; this is
                // caller misconfiguration, not evidence about the recording.
                throw new InteractionReplayException(
                    InteractionReplayError.SessionEpochMismatch,
                    "The registry must be reconstructed with the recording's session "
                    + "ID '" + recording.Session.SessionId + "'; its epoch is '"
                    + dispatcher.Registry.SessionEpoch + "'.");
            }

            if (dispatcher.Recorder != null)
            {
                throw new InteractionReplayException(
                    InteractionReplayError.RecorderAttached,
                    "Replay requires a recorder-free dispatcher: a recorder would "
                    + "append request and terminal events for every replayed entry "
                    + "and its failure semantics would mix into verification.");
            }

            using var lease = dispatcher.AcquireReplayLease();
            var interactions = recording.Interactions;
            var verified = 0;
            for (var index = 0; index < interactions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = interactions[index];
                var expected = entry.Outcome;
                if (expected == null)
                {
                    // Design §15.1: strict replay stops before an outcome-unknown
                    // entry; no recovery policy is defined in v1.
                    return Stopped(
                        InteractionReplayStopReason.OutcomeUnknown,
                        entry,
                        verified,
                        interactions.Count);
                }

                if (expected.Status == InteractionStatus.Cancelled
                    && expected.Stages.Count > 0)
                {
                    return Stopped(
                        InteractionReplayStopReason.CancelledDuringExecution,
                        entry,
                        verified,
                        interactions.Count);
                }

                var divergence = await ReplayEntryAsync(
                    dispatcher,
                    lease,
                    entry,
                    expected,
                    secretResolver,
                    cancellationToken).ConfigureAwait(false);
                if (divergence != null)
                {
                    return new InteractionReplayReport(
                        InteractionReplayOutcome.Diverged,
                        interactions.Count,
                        verified,
                        stopReason: null,
                        stoppedBefore: null,
                        divergence);
                }

                verified++;
                if (lease.TakeSuppressedContinuationCount() > 0)
                {
                    var next = index + 1 < interactions.Count
                        ? interactions[index + 1]
                        : null;
                    return Stopped(
                        InteractionReplayStopReason.ContinuationRequested,
                        next,
                        verified,
                        interactions.Count);
                }
            }

            return new InteractionReplayReport(
                InteractionReplayOutcome.Completed,
                interactions.Count,
                verified,
                stopReason: null,
                stoppedBefore: null,
                divergence: null);
        }

        private static async ValueTask<InteractionReplayDivergence?> ReplayEntryAsync(
            InteractionDispatcher dispatcher,
            InteractionReplayLease lease,
            RecordedInteraction entry,
            RecordedOutcome expected,
            IInteractionSecretResolver? secretResolver,
            CancellationToken cancellationToken)
        {
            var reference = InteractionReplayEntryRef.From(entry);

            // §16.1 step 1: the command name and version exist in the catalog.
            if (!dispatcher.Catalog.TryGet(
                entry.CommandName,
                entry.CommandVersion,
                out var catalogEntry))
            {
                return PreDispatch(
                    reference,
                    expected,
                    InteractionReplayDivergenceKind.CommandNotInCatalog);
            }

            // §16.1 step 2: audit the recorded arguments against the current schema
            // and resolve the referenced secrets in memory.
            var argumentDivergence = BuildArguments(
                reference,
                entry,
                expected,
                catalogEntry!,
                secretResolver,
                out var argumentsJson);
            if (argumentDivergence != null)
            {
                return argumentDivergence;
            }

            DecodedInteractionCommand decoded;
            try
            {
                using var document = JsonDocument.Parse(argumentsJson);
                decoded = catalogEntry!.Decode(entry.TargetId, document.RootElement);
            }
            catch (InteractionCommandException)
            {
                return PreDispatch(
                    reference,
                    expected,
                    InteractionReplayDivergenceKind.ArgumentsNotDecodable);
            }

            // §16.1 step 3: the recorded before-state hashes match the current
            // state, checked before dispatch so a mismatch stops execution with
            // zero further side effects (§22 criterion 6). Non-executed outcomes
            // (Rejected, cancelled before start) recorded empty, unobserved state
            // maps; they get a fresh zero-side-effect bracket instead.
            var executed = IsExecuted(expected);
            StateProbeReading? bracket = null;
            if (executed)
            {
                var current = ReadCurrentObservation(dispatcher);
                var differences = CompareObservations(expected.Before, current);
                if (differences.Count > 0)
                {
                    return new InteractionReplayDivergence(
                        reference,
                        InteractionReplayDivergenceKind.BeforeStateMismatch,
                        argumentName: null,
                        secretKey: null,
                        expected,
                        actual: null,
                        differences);
                }
            }
            else
            {
                bracket = dispatcher.Probes?.Read();
            }

            // §16.1 step 4: dispatch through the normal path with Origin.Replay and
            // no correlation or idempotency key — every entry executes exactly once.
            // A recorded pre-start cancellation is reproduced with a synthetic
            // already-cancelled token (never the caller's), which deterministically
            // takes the cancelled-before-start path on an idle dispatcher.
            var dispatchToken = expected.Status == InteractionStatus.Cancelled
                ? new CancellationToken(canceled: true)
                : cancellationToken;
            ValueTask<InteractionResult> pending;
            lease.ArmDispatch();
            try
            {
                pending = decoded.DispatchAsync(
                    dispatcher,
                    new InteractionDispatchOptions(InteractionOrigin.Replay),
                    dispatchToken);
            }
            finally
            {
                lease.DisarmDispatch();
            }

            var result = await pending.ConfigureAwait(false);

            // A stage may ignore cancellation and return any status; a cancelled
            // replay must throw rather than report that as a divergence.
            cancellationToken.ThrowIfCancellationRequested();

            var actual = RecordedOutcome.FromResult(result);

            // §16.1 step 5: the status matches.
            if (actual.Status != expected.Status)
            {
                return PostDispatch(
                    reference,
                    expected,
                    InteractionReplayDivergenceKind.StatusMismatch,
                    actual);
            }

            // §16.1 step 6: per-status detail.
            switch (expected.Status)
            {
                case InteractionStatus.Succeeded:
                    // Stage progress is compared for faulted results only (§16.1);
                    // for successes the state hashes are the behavioral guard, so a
                    // pipeline may restructure its stages without diverging.
                    break;
                case InteractionStatus.Faulted:
                    if (!string.Equals(
                        expected.FaultCode,
                        actual.FaultCode,
                        StringComparison.Ordinal))
                    {
                        return PostDispatch(
                            reference,
                            expected,
                            InteractionReplayDivergenceKind.FaultCodeMismatch,
                            actual);
                    }

                    // Full-array equality is exactly §12.2's "completed stage IDs
                    // plus the failed stage": both validators force the shape
                    // completed…faulted, so comparing the arrays compares those.
                    if (!expected.Stages.Equals(actual.Stages))
                    {
                        return PostDispatch(
                            reference,
                            expected,
                            InteractionReplayDivergenceKind.StageProgressMismatch,
                            actual);
                    }

                    break;
                case InteractionStatus.Rejected:
                    if (expected.RejectionCode != actual.RejectionCode)
                    {
                        return PostDispatch(
                            reference,
                            expected,
                            InteractionReplayDivergenceKind.RejectionCodeMismatch,
                            actual);
                    }

                    break;
                default:
                    // Cancelled before start: the replayed dispatch must not have
                    // reached any stage.
                    if (actual.Stages.Count != 0)
                    {
                        return PostDispatch(
                            reference,
                            expected,
                            InteractionReplayDivergenceKind.StageProgressMismatch,
                            actual);
                    }

                    break;
            }

            if (executed)
            {
                // Defensive re-check of the dispatcher's own capture closes the gap
                // between the replayer's pre-dispatch reading and publish; then
                // §16.1 step 7: the recorded after-state hashes match.
                var beforeDifferences = CompareObservations(expected.Before, actual.Before);
                if (beforeDifferences.Count > 0)
                {
                    return new InteractionReplayDivergence(
                        reference,
                        InteractionReplayDivergenceKind.BeforeStateMismatch,
                        argumentName: null,
                        secretKey: null,
                        expected,
                        actual,
                        beforeDifferences);
                }

                var afterDifferences = CompareObservations(expected.After, actual.After);
                if (afterDifferences.Count > 0)
                {
                    return new InteractionReplayDivergence(
                        reference,
                        InteractionReplayDivergenceKind.AfterStateMismatch,
                        argumentName: null,
                        secretKey: null,
                        expected,
                        actual,
                        afterDifferences);
                }
            }
            else if (bracket != null)
            {
                // Zero probe-observable state change is the strongest claim a
                // recording supports for the §16.1 zero-side-effect guarantee.
                var after = bracket.ReadSame();
                var differences = CompareObservations(
                    bracket.ToObservation(),
                    after.ToObservation());
                if (differences.Count > 0)
                {
                    return new InteractionReplayDivergence(
                        reference,
                        InteractionReplayDivergenceKind.UnexpectedStateChange,
                        argumentName: null,
                        secretKey: null,
                        expected,
                        actual,
                        differences);
                }
            }

            return null;
        }

        // Rebuilds the recorded argument object with secrets substituted in memory.
        // Every property is audited against the current catalog schema before the
        // codec sees it (the codec may be permissive): unknown arguments, missing
        // required arguments, wrong scalar kinds, markers for non-sensitive
        // arguments, and plaintext for now-sensitive arguments are all schema
        // divergences. Plaintext scalars pass through byte-for-byte, so recorded
        // numbers survive without precision loss.
        private static InteractionReplayDivergence? BuildArguments(
            InteractionReplayEntryRef reference,
            RecordedInteraction entry,
            RecordedOutcome expected,
            InteractionCommandCatalogEntry catalogEntry,
            IInteractionSecretResolver? secretResolver,
            out byte[] argumentsJson)
        {
            argumentsJson = Array.Empty<byte>();
            var definitions = new Dictionary<string, InteractionArgumentDefinition>(
                StringComparer.Ordinal);
            foreach (var definition in catalogEntry.Arguments.Arguments)
            {
                definitions.Add(definition.Name, definition);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var buffer = new ArrayBufferWriter<byte>();
            using (var document = JsonDocument.Parse(entry.ArgumentsJson))
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    seen.Add(property.Name);
                    if (!definitions.TryGetValue(property.Name, out var definition))
                    {
                        return ArgumentDivergence(reference, expected, property.Name);
                    }

                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        // A secret marker; its shape and key were validated by the
                        // strict reader.
                        var divergence = ResolveSecret(
                            reference,
                            entry,
                            expected,
                            definition,
                            secretResolver,
                            writer);
                        if (divergence != null)
                        {
                            return divergence;
                        }
                    }
                    else
                    {
                        if (definition.Sensitive)
                        {
                            // Plaintext was recorded for an argument the current
                            // catalog marks sensitive (sensitivity upgraded since
                            // recording); replaying it would republish the value.
                            return ArgumentDivergence(reference, expected, property.Name);
                        }

                        if (!ScalarMatches(property.Value.ValueKind, definition.Type))
                        {
                            return ArgumentDivergence(reference, expected, property.Name);
                        }

                        property.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            foreach (var definition in catalogEntry.Arguments.Arguments)
            {
                if (definition.Required && !seen.Contains(definition.Name))
                {
                    return ArgumentDivergence(reference, expected, definition.Name);
                }
            }

            argumentsJson = buffer.WrittenSpan.ToArray();
            return null;
        }

        private static InteractionReplayDivergence? ResolveSecret(
            InteractionReplayEntryRef reference,
            RecordedInteraction entry,
            RecordedOutcome expected,
            InteractionArgumentDefinition definition,
            IInteractionSecretResolver? secretResolver,
            Utf8JsonWriter writer)
        {
            if (!definition.Sensitive)
            {
                // The recording redacted an argument the current catalog no longer
                // marks sensitive; the schemas have drifted apart.
                return ArgumentDivergence(reference, expected, definition.Name);
            }

            var key = InteractionRecordingSecret.KeyFor(
                entry.CommandName,
                entry.CommandVersion,
                definition.Name);
            if (secretResolver == null)
            {
                throw new InteractionReplayException(
                    InteractionReplayError.SecretResolverMissing,
                    "Entry " + entry.Sequence.ToString(CultureInfo.InvariantCulture)
                    + " references secret '" + key
                    + "' but no secret resolver was supplied.");
            }

            bool resolved;
            InteractionValue? value;
            try
            {
                resolved = secretResolver.TryResolve(entry.RequestId, key, out value);
            }
            catch (Exception exception)
            {
                // A throwing resolver (a failed vault lookup, for example) is a
                // broken resolver contract, not evidence about the recording;
                // surface it through the stable replay failure channel with the
                // original cause attached.
                throw new InteractionReplayException(
                    InteractionReplayError.SecretResolverContract,
                    "The secret resolver threw while resolving '" + key + "'.",
                    exception);
            }

            if (!resolved)
            {
                return new InteractionReplayDivergence(
                    reference,
                    InteractionReplayDivergenceKind.SecretUnavailable,
                    argumentName: null,
                    key,
                    expected,
                    actual: null,
                    Array.Empty<InteractionReplayStateDifference>());
            }

            if (value == null || value.Kind == InteractionValueKind.Null)
            {
                // Schema v1 arguments are always scalars; a resolver claiming
                // success without one has broken its contract.
                throw new InteractionReplayException(
                    InteractionReplayError.SecretResolverContract,
                    "The secret resolver claimed success without a scalar value for '"
                    + key + "'.");
            }

            if (!KindMatches(value.Kind, definition.Type))
            {
                return ArgumentDivergence(reference, expected, definition.Name);
            }

            writer.WritePropertyName(definition.Name);
            switch (value.Kind)
            {
                case InteractionValueKind.String:
                    writer.WriteStringValue(value.GetString());
                    break;
                case InteractionValueKind.Boolean:
                    writer.WriteBooleanValue(value.GetBoolean());
                    break;
                default:
                    writer.WriteNumberValue(value.GetNumber());
                    break;
            }

            return null;
        }

        // Executed outcomes carry genuine before/after observations to verify;
        // Rejected and cancelled-before-start outcomes recorded empty state maps
        // meaning "never observed" (schema v1), so hash comparison against them
        // would be meaningless. The classification is by status, not stage count,
        // because a forged Succeeded entry with zero stages must still face the
        // state checks.
        private static bool IsExecuted(RecordedOutcome outcome)
        {
            switch (outcome.Status)
            {
                case InteractionStatus.Succeeded:
                case InteractionStatus.Faulted:
                    return true;
                case InteractionStatus.Cancelled:
                    return outcome.Stages.Count > 0;
                default:
                    return false;
            }
        }

        private static StateObservation ReadCurrentObservation(
            InteractionDispatcher dispatcher)
        {
            var probes = dispatcher.Probes;
            return probes == null
                ? StateObservation.Empty
                : probes.Read().ToObservation();
        }

        // Merges two probe-ID-sorted observations into per-probe differences:
        // changed hashes, probes only the recording knows, and probes only the
        // runtime has. StateProbeDiff cannot represent the missing sides, hence
        // the replay-specific difference type.
        private static List<InteractionReplayStateDifference> CompareObservations(
            StateObservation expected,
            StateObservation actual)
        {
            var differences = new List<InteractionReplayStateDifference>();
            var expectedProbes = expected.Probes;
            var actualProbes = actual.Probes;
            var expectedIndex = 0;
            var actualIndex = 0;
            while (expectedIndex < expectedProbes.Count || actualIndex < actualProbes.Count)
            {
                int comparison;
                if (expectedIndex >= expectedProbes.Count)
                {
                    comparison = 1;
                }
                else if (actualIndex >= actualProbes.Count)
                {
                    comparison = -1;
                }
                else
                {
                    comparison = string.CompareOrdinal(
                        expectedProbes[expectedIndex].ProbeId,
                        actualProbes[actualIndex].ProbeId);
                }

                if (comparison < 0)
                {
                    var probe = expectedProbes[expectedIndex];
                    differences.Add(new InteractionReplayStateDifference(
                        probe.ProbeId,
                        probe.Hash,
                        actualHash: null));
                    expectedIndex++;
                }
                else if (comparison > 0)
                {
                    var probe = actualProbes[actualIndex];
                    differences.Add(new InteractionReplayStateDifference(
                        probe.ProbeId,
                        expectedHash: null,
                        probe.Hash));
                    actualIndex++;
                }
                else
                {
                    var expectedProbe = expectedProbes[expectedIndex];
                    var actualProbe = actualProbes[actualIndex];
                    if (!string.Equals(
                        expectedProbe.Hash,
                        actualProbe.Hash,
                        StringComparison.Ordinal))
                    {
                        differences.Add(new InteractionReplayStateDifference(
                            expectedProbe.ProbeId,
                            expectedProbe.Hash,
                            actualProbe.Hash));
                    }

                    expectedIndex++;
                    actualIndex++;
                }
            }

            return differences;
        }

        private static bool ScalarMatches(
            JsonValueKind valueKind,
            InteractionArgumentType type)
        {
            switch (type)
            {
                case InteractionArgumentType.String:
                    return valueKind == JsonValueKind.String;
                case InteractionArgumentType.Boolean:
                    return valueKind == JsonValueKind.True
                        || valueKind == JsonValueKind.False;
                default:
                    return valueKind == JsonValueKind.Number;
            }
        }

        private static bool KindMatches(
            InteractionValueKind kind,
            InteractionArgumentType type)
        {
            switch (type)
            {
                case InteractionArgumentType.String:
                    return kind == InteractionValueKind.String;
                case InteractionArgumentType.Boolean:
                    return kind == InteractionValueKind.Boolean;
                default:
                    return kind == InteractionValueKind.Number;
            }
        }

        private static InteractionReplayDivergence PreDispatch(
            InteractionReplayEntryRef reference,
            RecordedOutcome expected,
            InteractionReplayDivergenceKind kind)
        {
            return new InteractionReplayDivergence(
                reference,
                kind,
                argumentName: null,
                secretKey: null,
                expected,
                actual: null,
                Array.Empty<InteractionReplayStateDifference>());
        }

        private static InteractionReplayDivergence PostDispatch(
            InteractionReplayEntryRef reference,
            RecordedOutcome expected,
            InteractionReplayDivergenceKind kind,
            RecordedOutcome actual)
        {
            return new InteractionReplayDivergence(
                reference,
                kind,
                argumentName: null,
                secretKey: null,
                expected,
                actual,
                Array.Empty<InteractionReplayStateDifference>());
        }

        private static InteractionReplayDivergence ArgumentDivergence(
            InteractionReplayEntryRef reference,
            RecordedOutcome expected,
            string argumentName)
        {
            return new InteractionReplayDivergence(
                reference,
                InteractionReplayDivergenceKind.ArgumentSchemaMismatch,
                argumentName,
                secretKey: null,
                expected,
                actual: null,
                Array.Empty<InteractionReplayStateDifference>());
        }

        private static InteractionReplayReport Stopped(
            InteractionReplayStopReason reason,
            RecordedInteraction? stoppedBefore,
            int verified,
            int total)
        {
            return new InteractionReplayReport(
                InteractionReplayOutcome.Stopped,
                total,
                verified,
                reason,
                stoppedBefore == null ? null : InteractionReplayEntryRef.From(stoppedBefore),
                divergence: null);
        }
    }
}
