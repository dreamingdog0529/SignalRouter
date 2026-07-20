using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class InteractionRecordingTests
{
    private static readonly DateTimeOffset FixedInstant =
        new(2026, 7, 21, 3, 4, 5, 123, TimeSpan.Zero);

    // ---------------------------------------------------------------- writer

    [Test]
    public void HeaderIsWrittenEagerlyWithSchemaVersionSessionAndClockTimestamp()
    {
        using var sink = new MemoryStream();
        using (new InteractionRecorder(sink, RecorderOptions(), leaveOpen: true))
        {
        }

        var lines = Lines(sink);
        var header = JsonDocument.Parse(lines.Single()).RootElement;
        NUnitCompat.Multiple(() =>
        {
            Assert.That(header.GetProperty("kind").GetString(), Is.EqualTo("session"));
            Assert.That(header.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
            Assert.That(header.GetProperty("sessionId").GetString(), Is.EqualTo("session-1"));
            Assert.That(header.GetProperty("appBuild").GetString(), Is.EqualTo("build-1"));
            Assert.That(
                header.GetProperty("startedAt").GetString(),
                Is.EqualTo("2026-07-21T03:04:05.1230000Z"));
        });
    }

    [Test]
    public async Task RequestEventIsDurableBeforeTheFirstStageRuns()
    {
        using var harness = new RecordingHarness();
        var blocker = harness.RegisterClick("menu.start", gate: true);

        var dispatch = harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options()).AsTask();
        await blocker.Started.Task;

        var midFlight = Lines(harness.Sink);
        blocker.Release();
        await dispatch;
        var finished = Lines(harness.Sink);

        var request = JsonDocument.Parse(midFlight[1]).RootElement;
        NUnitCompat.Multiple(() =>
        {
            Assert.That(midFlight, Has.Length.EqualTo(2));
            Assert.That(request.GetProperty("kind").GetString(), Is.EqualTo("interaction_requested"));
            Assert.That(request.GetProperty("sequence").GetInt64(), Is.EqualTo(1));
            Assert.That(request.GetProperty("origin").GetString(), Is.EqualTo("Test"));
            Assert.That(
                request.GetProperty("command").GetProperty("name").GetString(),
                Is.EqualTo("click"));
            Assert.That(
                request.GetProperty("command").GetProperty("targetId").GetString(),
                Is.EqualTo("menu.start"));
            Assert.That(
                request.GetProperty("command").GetProperty("arguments").GetRawText(),
                Is.EqualTo("{}"));
            Assert.That(finished, Has.Length.EqualTo(3));
            Assert.That(
                JsonDocument.Parse(finished[2]).RootElement.GetProperty("kind").GetString(),
                Is.EqualTo("interaction_completed"));
        });
    }

    [Test]
    public async Task ConcurrentDispatchesRecordRequestEventsInSequenceOrder()
    {
        using var harness = new RecordingHarness();
        for (var index = 0; index < 12; index++)
        {
            harness.RegisterClick("target." + index);
        }

        var tasks = new List<Task<InteractionResult>>();
        for (var index = 0; index < 12; index++)
        {
            var command = new ClickCommand("target." + index);
            tasks.Add(Task.Run(async () =>
                await harness.Dispatcher.DispatchAsync(command, Options()).AsTask()));
        }

        await Task.WhenAll(tasks);
        var recording = Load(harness.Sink);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(recording.Interactions, Has.Count.EqualTo(12));
            Assert.That(
                recording.Interactions.Select(interaction => interaction.Sequence),
                Is.Ordered.Ascending);
            Assert.That(
                recording.Interactions.All(interaction => interaction.HasKnownOutcome),
                Is.True);
        });
    }

    [Test]
    public async Task ASucceededInteractionRecordsCompletedStagesAndNoCodes()
    {
        using var harness = new RecordingHarness();
        harness.RegisterClick("menu.start");

        await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());

        var result = CompletedResultElement(harness.Sink);
        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("Succeeded"));
            Assert.That(result.GetProperty("stages").GetArrayLength(), Is.EqualTo(1));
            Assert.That(
                result.GetProperty("stages")[0].GetProperty("id").GetString(),
                Is.EqualTo("execute"));
            Assert.That(result.TryGetProperty("rejectionCode", out _), Is.False);
            Assert.That(result.TryGetProperty("faultCode", out _), Is.False);
        });
    }

    [Test]
    public async Task ARejectedInteractionRecordsItsRejectionCodeAndEmptyState()
    {
        using var harness = new RecordingHarness();

        await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.missing"), Options());

        var line = JsonDocument.Parse(Lines(harness.Sink)[2]).RootElement;
        var result = line.GetProperty("result");
        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("Rejected"));
            Assert.That(
                result.GetProperty("rejectionCode").GetString(),
                Is.EqualTo("TargetNotFound"));
            Assert.That(result.GetProperty("stages").GetArrayLength(), Is.EqualTo(0));
            Assert.That(
                line.GetProperty("state").GetProperty("before").GetRawText(),
                Is.EqualTo("{}"));
            Assert.That(
                line.GetProperty("state").GetProperty("after").GetRawText(),
                Is.EqualTo("{}"));
        });
    }

    [Test]
    public async Task AFaultedInteractionRecordsTheStableApplicationCode()
    {
        using var harness = new RecordingHarness();
        harness.RegisterClick(
            "menu.start",
            fault: new InteractionFaultException(
                "AudioDeviceUnavailable",
                "The audio device is unavailable."));

        await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());

        var result = CompletedResultElement(harness.Sink);
        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("Faulted"));
            Assert.That(
                result.GetProperty("faultCode").GetString(),
                Is.EqualTo("AudioDeviceUnavailable"));
            Assert.That(
                result.GetProperty("stages")[0].GetProperty("status").GetString(),
                Is.EqualTo("Faulted"));
        });
    }

    [Test]
    public async Task AFaultWithoutAnApplicationCodeRecordsANullFaultCode()
    {
        using var harness = new RecordingHarness();
        harness.RegisterClick("menu.start", fault: new InvalidOperationException("boom"));

        await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());

        var result = CompletedResultElement(harness.Sink);
        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("Faulted"));
            Assert.That(
                result.GetProperty("faultCode").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task CancellationsBeforeAndDuringExecutionAreRecordedAsCancelled()
    {
        using var harness = new RecordingHarness();
        using var duringCancellation = new CancellationTokenSource();
        harness.RegisterClick("menu.before");
        harness.RegisterClick("menu.during", observeCancellation: duringCancellation);

        await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.before"),
            Options(),
            new CancellationToken(canceled: true));
        await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.during"),
            Options(),
            duringCancellation.Token);

        var recording = Load(harness.Sink);
        var beforeStart = recording.Interactions[0].Outcome!;
        var duringExecution = recording.Interactions[1].Outcome!;
        NUnitCompat.Multiple(() =>
        {
            Assert.That(beforeStart.Status, Is.EqualTo(InteractionStatus.Cancelled));
            Assert.That(beforeStart.Stages, Is.Empty);
            Assert.That(duringExecution.Status, Is.EqualTo(InteractionStatus.Cancelled));
            Assert.That(duringExecution.Stages, Has.Count.EqualTo(1));
            Assert.That(
                duringExecution.Stages[0].Status,
                Is.EqualTo(InteractionStageStatus.Cancelled));
        });
    }

    [Test]
    public async Task PerProbeHashesMatchTheResultObservations()
    {
        using var harness = new RecordingHarness(withProbes: true);
        harness.RegisterClick("menu.start");

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());

        var recording = Load(harness.Sink);
        var outcome = recording.Interactions.Single().Outcome!;
        NUnitCompat.Multiple(() =>
        {
            Assert.That(outcome.Before, Is.EqualTo(result.Before));
            Assert.That(outcome.After, Is.EqualTo(result.After));
            Assert.That(
                outcome.Before.Probes.Select(probe => probe.ProbeId),
                Is.EqualTo(new[]
                {
                    InteractionRuntimeStateProbe.ProbeId,
                    SemanticUiStateProbe.ProbeId,
                }));
        });
    }

    [Test]
    public async Task SensitiveArgumentsAreReplacedWithSecretKeysAndPlaintextNeverPersists()
    {
        using var harness = new RecordingHarness(catalog: SecretCatalog());
        harness.RegisterSecret("form.password");

        await harness.Dispatcher.DispatchAsync(
            new SecretValueCommand("form.password", "hunter2-plaintext"),
            Options());

        var text = Encoding.UTF8.GetString(harness.Sink.ToArray());
        var recording = Load(harness.Sink);
        var arguments = JsonDocument.Parse(
            recording.Interactions.Single().ArgumentsJson).RootElement;
        NUnitCompat.Multiple(() =>
        {
            Assert.That(text, Does.Not.Contain("hunter2-plaintext"));
            Assert.That(
                arguments.GetProperty("value").GetProperty("$secret").GetString(),
                Is.EqualTo("set_secret@1/value"));
            Assert.That(
                recording.RequiredSecretKeys,
                Is.EqualTo(new[] { "set_secret@1/value" }));
        });
    }

    [Test]
    public async Task FaultRecordingsContainNoExceptionTypeMessageOrStackTrace()
    {
        using var harness = new RecordingHarness();
        Exception fault;
        try
        {
            throw new InvalidOperationException("distinctive-fault-message-4d1f");
        }
        catch (InvalidOperationException thrown)
        {
            fault = thrown;
        }

        harness.RegisterClick("menu.start", fault: fault);
        await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());

        var text = Encoding.UTF8.GetString(harness.Sink.ToArray());
        NUnitCompat.Multiple(() =>
        {
            Assert.That(text, Does.Not.Contain("InvalidOperationException"));
            Assert.That(text, Does.Not.Contain("distinctive-fault-message-4d1f"));
            Assert.That(text, Does.Not.Contain("   at "));
        });
    }

    [Test]
    public void ASinkFailurePoisonsTheRecorderAndFailsLaterDispatches()
    {
        using var sink = new FlakySink { FailAfterLines = 1 };
        using var harness = new RecordingHarness(sink);
        harness.RegisterClick("menu.start");

        NUnitCompat.ThatThrows(
            () => harness.Dispatcher.DispatchAsync(
                new ClickCommand("menu.start"),
                Options()).AsTask().GetAwaiter().GetResult(),
            Throws.TypeOf<IOException>());
        var second = NUnitCompat.ThrowsAsync<InteractionRecordingException>(async () =>
            await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options()));

        NUnitCompat.Multiple(() =>
        {
            Assert.That(second!.Error, Is.EqualTo(InteractionRecordingError.RecorderFailed));
            Assert.That(harness.ExecutionLog, Is.Empty);
        });
    }

    [Test]
    public void AnAppendBeyondTheSizeBoundFailsAndPoisonsTheRecorder()
    {
        using var sink = new MemoryStream();
        using var harness = new RecordingHarness(
            sink,
            options: RecorderOptions(maxRecordingBytes: 200));
        harness.RegisterClick("menu.start");

        var first = NUnitCompat.ThrowsAsync<InteractionRecordingException>(async () =>
            await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options()));
        var second = NUnitCompat.ThrowsAsync<InteractionRecordingException>(async () =>
            await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options()));

        NUnitCompat.Multiple(() =>
        {
            Assert.That(first!.Error, Is.EqualTo(InteractionRecordingError.SizeLimitExceeded));
            Assert.That(second!.Error, Is.EqualTo(InteractionRecordingError.RecorderFailed));
            Assert.That(harness.ExecutionLog, Is.Empty);
        });
    }

    [Test]
    public async Task ATerminalAppendFailureKeepsTheExecutedResultCachedForItsIdempotencyKey()
    {
        using var sink = new FlakySink { FailAfterLines = 2 };
        using var harness = new RecordingHarness(sink);
        harness.RegisterClick("menu.start");
        var options = new InteractionDispatchOptions(
            InteractionOrigin.Test,
            idempotencyKey: "retry-key");

        var failure = NUnitCompat.ThrowsAsync<InteractionRecordingException>(async () =>
            await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), options));
        var retried = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            options);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(failure!.Error, Is.EqualTo(InteractionRecordingError.RecorderFailed));
            Assert.That(failure.CompletedResult, Is.Not.Null);
            Assert.That(failure.CompletedResult!.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(retried, Is.SameAs(failure.CompletedResult));
            Assert.That(harness.ExecutionLog, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task AlreadyEnqueuedWorkFailsBeforeItsStagesOnceTheRecorderIsPoisoned()
    {
        using var sink = new FlakySink();
        using var harness = new RecordingHarness(sink);
        var blocker = harness.RegisterClick("menu.first", gate: true);
        harness.RegisterClick("menu.queued");
        harness.RegisterClick("menu.poisoning");

        var first = harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.first"),
            Options()).AsTask();
        await blocker.Started.Task;
        var queued = harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.queued"),
            Options()).AsTask();

        sink.FailNext = true;
        NUnitCompat.ThatThrows(
            () => harness.Dispatcher.DispatchAsync(
                new ClickCommand("menu.poisoning"),
                Options()).AsTask().GetAwaiter().GetResult(),
            Throws.TypeOf<IOException>());

        blocker.Release();
        var firstFailure = NUnitCompat.ThrowsAsync<InteractionRecordingException>(async () => await first);
        var queuedFailure = NUnitCompat.ThrowsAsync<InteractionRecordingException>(async () => await queued);

        NUnitCompat.Multiple(() =>
        {
            // The first interaction executed but its terminal event could not be
            // appended; the queued one was stopped before any stage ran.
            Assert.That(firstFailure!.CompletedResult, Is.Not.Null);
            Assert.That(queuedFailure!.Error, Is.EqualTo(InteractionRecordingError.RecorderFailed));
            Assert.That(queuedFailure.CompletedResult, Is.Null);
            Assert.That(harness.ExecutionLog, Is.EqualTo(new[] { "menu.first" }));
        });
    }

    [Test]
    public void ARecorderSessionMismatchIsRejectedAtDispatcherConstruction()
    {
        using var sink = new MemoryStream();
        var catalog = InteractionCommandCatalog.CreateMvp();
        var registry = new InteractionRegistry(catalog, "session-1");
        using var recorder = new InteractionRecorder(
            sink,
            new InteractionRecorderOptions("other-session", "build-1", new FixedClock(FixedInstant)),
            leaveOpen: true);

        NUnitCompat.ThatThrows(
            () => new InteractionDispatcher(catalog, registry, null, recorder),
            Throws.ArgumentException);
    }

    [Test]
    public void ANonObjectCodecOutputFailsBeforeAnythingIsRecorded()
    {
        using var harness = new RecordingHarness(catalog: BrokenCatalog());
        harness.RegisterBroken("menu.start");

        NUnitCompat.ThatThrows(
            () => harness.Dispatcher.DispatchAsync(
                new BrokenCommand("menu.start"),
                Options()).AsTask().GetAwaiter().GetResult(),
            Throws.TypeOf<InteractionInvariantViolationException>());
        Assert.That(Lines(harness.Sink), Has.Length.EqualTo(1));
    }

    [Test]
    public void OutOfContractAppendsAreInvariantViolations()
    {
        using var sink = new MemoryStream();
        using var recorder = new InteractionRecorder(sink, RecorderOptions(), leaveOpen: true);
        var arguments = Encoding.UTF8.GetBytes("{}");
        recorder.AppendRequested(2, RequestId(2), InteractionOrigin.Test, "click", 1, "menu.start", arguments);
        recorder.AppendCompleted(SucceededResult(2));

        NUnitCompat.Multiple(() =>
        {
            NUnitCompat.ThatThrows(
                () => recorder.AppendRequested(
                    2, RequestId(3), InteractionOrigin.Test, "click", 1, "menu.start", arguments),
                Throws.TypeOf<InteractionInvariantViolationException>());
            NUnitCompat.ThatThrows(
                () => recorder.AppendCompleted(SucceededResult(2)),
                Throws.TypeOf<InteractionInvariantViolationException>());
            NUnitCompat.ThatThrows(
                () => recorder.AppendCompleted(SucceededResult(9)),
                Throws.TypeOf<InteractionInvariantViolationException>());
        });
    }

    // ---------------------------------------------------------------- reader

    [Test]
    public async Task RoundTripReproducesEveryRecordedInteraction()
    {
        using var harness = new RecordingHarness();
        harness.RegisterClick("menu.start");
        harness.RegisterClick(
            "menu.faulty",
            fault: new InteractionFaultException("StageBroke", "The stage broke."));

        await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.faulty"), Options());
        await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.missing"), Options());

        var recording = Load(harness.Sink);
        NUnitCompat.Multiple(() =>
        {
            Assert.That(recording.Session.SessionId, Is.EqualTo("session-1"));
            Assert.That(recording.Session.AppBuild, Is.EqualTo("build-1"));
            Assert.That(recording.Session.StartedAt, Is.EqualTo(FixedInstant));
            Assert.That(recording.TruncatedTailDiscarded, Is.False);
            Assert.That(recording.Interactions, Has.Count.EqualTo(3));
            Assert.That(
                recording.Interactions.Select(interaction => interaction.Outcome!.Status),
                Is.EqualTo(new[]
                {
                    InteractionStatus.Succeeded,
                    InteractionStatus.Faulted,
                    InteractionStatus.Rejected,
                }));
            Assert.That(recording.Interactions[1].Outcome!.FaultCode, Is.EqualTo("StageBroke"));
            Assert.That(
                recording.Interactions[2].Outcome!.RejectionCode,
                Is.EqualTo(InteractionRejectionCode.TargetNotFound));
            Assert.That(
                recording.Interactions.Select(interaction => interaction.ArgumentsJson),
                Is.All.EqualTo("{}"));
        });
    }

    [Test]
    public async Task ATruncatedFinalLineIsDiscardedAndReported()
    {
        using var harness = new RecordingHarness();
        harness.RegisterClick("menu.start");
        await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());

        var complete = harness.Sink.ToArray();
        var truncated = complete.Take(complete.Length - 10).ToArray();
        var recording = Load(truncated);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(recording.TruncatedTailDiscarded, Is.True);
            Assert.That(recording.DiscardedTailBytes, Is.GreaterThan(0));
            Assert.That(recording.Interactions, Has.Count.EqualTo(1));
            Assert.That(recording.Interactions[0].HasKnownOutcome, Is.False);
        });
    }

    [Test]
    public async Task AnUnpairedRequestEventIsReportedAsOutcomeUnknownNotFaulted()
    {
        using var harness = new RecordingHarness();
        var blocker = harness.RegisterClick("menu.start", gate: true);

        var dispatch = harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options()).AsTask();
        await blocker.Started.Task;
        var midFlight = harness.Sink.ToArray();
        blocker.Release();
        await dispatch;

        var recording = Load(midFlight);
        NUnitCompat.Multiple(() =>
        {
            Assert.That(recording.TruncatedTailDiscarded, Is.False);
            Assert.That(recording.Interactions.Single().Outcome, Is.Null);
            Assert.That(recording.Interactions.Single().HasKnownOutcome, Is.False);
        });
    }

    [Test]
    public void AnUnsupportedSchemaVersionIsRejectedBeforeFieldValidation()
    {
        var content = Header(schemaVersion: 2, extraField: true) + "\n";
        var failure = LoadFailure(content);
        Assert.That(failure.Error, Is.EqualTo(InteractionRecordingError.UnsupportedSchemaVersion));
    }

    [Test]
    public void AMissingOrNonSessionFirstLineIsRejected()
    {
        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                LoadFailure(string.Empty).Error,
                Is.EqualTo(InteractionRecordingError.MissingHeader));
            Assert.That(
                LoadFailure(Requested(1) + "\n").Error,
                Is.EqualTo(InteractionRecordingError.MissingHeader));
            Assert.That(
                LoadFailure(Header()).Error,
                Is.EqualTo(InteractionRecordingError.MissingHeader));
        });
    }

    [Test]
    public void AMalformedNewlineTerminatedLineIsCorruptionNotTruncation()
    {
        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                LoadFailure(Header() + "\nnot-json\n" + Requested(1) + "\n").Error,
                Is.EqualTo(InteractionRecordingError.CorruptEntry));
            Assert.That(
                LoadFailure(Header() + "\nnot-json\n").Error,
                Is.EqualTo(InteractionRecordingError.CorruptEntry));
        });
    }

    [Test]
    public void OrderingAndPairingViolationsAreRejected()
    {
        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                LoadFailure(Header() + "\n" + Completed(1, RequestId(1)) + "\n").Error,
                Is.EqualTo(InteractionRecordingError.UnmatchedTerminalEvent));
            Assert.That(
                LoadFailure(
                    Header() + "\n" + Requested(1) + "\n"
                    + Completed(1, RequestId(1)) + "\n"
                    + Completed(1, RequestId(1)) + "\n").Error,
                Is.EqualTo(InteractionRecordingError.DuplicateTerminalEvent));
            Assert.That(
                LoadFailure(
                    Header() + "\n" + Requested(2) + "\n" + Requested(1) + "\n").Error,
                Is.EqualTo(InteractionRecordingError.NonMonotonicSequence));
            Assert.That(
                LoadFailure(
                    Header() + "\n" + Requested(1) + "\n"
                    + Completed(1, RequestId(9)) + "\n").Error,
                Is.EqualTo(InteractionRecordingError.CorruptEntry));
        });
    }

    [Test]
    public void TerminalEventsMayInterleaveAcrossInteractions()
    {
        var content = Header() + "\n"
            + Requested(1) + "\n"
            + Requested(2) + "\n"
            + Completed(2, RequestId(2)) + "\n"
            + Completed(1, RequestId(1)) + "\n";

        var recording = Load(content);
        NUnitCompat.Multiple(() =>
        {
            Assert.That(recording.Interactions, Has.Count.EqualTo(2));
            Assert.That(
                recording.Interactions.All(interaction => interaction.HasKnownOutcome),
                Is.True);
        });
    }

    [Test]
    public void UnknownKindsFieldsAndDuplicatePropertiesAreRejected()
    {
        var unknownKind = Header() + "\n"
            + "{\"kind\":\"security_event\",\"sequence\":1}\n";
        var unknownField = Header() + "\n"
            + Requested(1).Replace("\"origin\"", "\"extra\":1,\"origin\"") + "\n";
        var duplicateProperty = Header() + "\n"
            + Requested(1).Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":1") + "\n";

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                LoadFailure(unknownKind).Error,
                Is.EqualTo(InteractionRecordingError.CorruptEntry));
            Assert.That(
                LoadFailure(unknownField).Error,
                Is.EqualTo(InteractionRecordingError.CorruptEntry));
            Assert.That(
                LoadFailure(duplicateProperty).Error,
                Is.EqualTo(InteractionRecordingError.CorruptEntry));
        });
    }

    [Test]
    public void RequiredSecretKeysListEachDistinctKeyOnce()
    {
        var secretArguments =
            "{\"value\":{\"$secret\":\"click@1/value\"}}";
        var content = Header() + "\n"
            + Requested(1, arguments: secretArguments) + "\n"
            + Requested(2, arguments: secretArguments) + "\n";

        var recording = Load(content);
        Assert.That(recording.RequiredSecretKeys, Is.EqualTo(new[] { "click@1/value" }));
    }

    [Test]
    public void ASecretReferenceThatContradictsItsRequestMetadataIsCorruption()
    {
        var content = Header() + "\n"
            + Requested(1, arguments: "{\"value\":{\"$secret\":\"other@1/value\"}}") + "\n";

        Assert.That(
            LoadFailure(content).Error,
            Is.EqualTo(InteractionRecordingError.CorruptEntry));
    }

    [Test]
    public void ANonExecutedOutcomeWithNonEmptyStateMapsIsCorruption()
    {
        var hash = new string('a', 64);
        var rejected = "{\"kind\":\"interaction_completed\",\"sequence\":1"
            + ",\"requestId\":\"" + RequestId(1) + "\""
            + ",\"result\":{\"status\":\"Rejected\",\"stages\":[]"
            + ",\"rejectionCode\":\"TargetNotFound\"}"
            + ",\"state\":{\"before\":{\"p\":\"" + hash + "\"}"
            + ",\"after\":{\"p\":\"" + hash + "\"}}}";
        var content = Header() + "\n" + Requested(1) + "\n" + rejected + "\n";

        Assert.That(
            LoadFailure(content).Error,
            Is.EqualTo(InteractionRecordingError.CorruptEntry));
    }

    // ------------------------------------------------------------- file I/O

    [Test]
    public void FilePathsThatEscapeTheArtifactRootAreRefused()
    {
        var root = CreateArtifactRoot();
        try
        {
            NUnitCompat.Multiple(() =>
            {
                NUnitCompat.ThatThrows(
                    () => InteractionRecorder.CreateFile(root, "..\\outside.jsonl", RecorderOptions()),
                    Throws.ArgumentException);
                NUnitCompat.ThatThrows(
                    () => InteractionRecorder.CreateFile(
                        root,
                        Path.Combine(Path.GetTempPath(), "evil.jsonl"),
                        RecorderOptions()),
                    Throws.ArgumentException);
                NUnitCompat.ThatThrows(
                    () => InteractionRecordingReader.LoadFile(root, "../outside.jsonl"),
                    Throws.ArgumentException);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CreateFileRefusesToOverwriteAnExistingRecording()
    {
        var root = CreateArtifactRoot();
        try
        {
            using (InteractionRecorder.CreateFile(root, "session.jsonl", RecorderOptions()))
            {
            }

            NUnitCompat.ThatThrows(
                () => InteractionRecorder.CreateFile(root, "session.jsonl", RecorderOptions()),
                Throws.TypeOf<IOException>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task EachAppendIsFlushedAndVisibleToAConcurrentReader()
    {
        var root = CreateArtifactRoot();
        try
        {
            using var recorder = InteractionRecorder.CreateFile(
                root,
                Path.Combine("sub", "session.jsonl"),
                RecorderOptions());
            using var harness = new RecordingHarness(recorder: recorder);
            var blocker = harness.RegisterClick("menu.start", gate: true);

            var dispatch = harness.Dispatcher.DispatchAsync(
                new ClickCommand("menu.start"),
                Options()).AsTask();
            await blocker.Started.Task;

            // The writer still holds the file; the request line must already be
            // durable and readable through an independent handle.
            var midFlight = InteractionRecordingReader.LoadFile(
                root,
                Path.Combine("sub", "session.jsonl"));
            blocker.Release();
            await dispatch;
            var finished = InteractionRecordingReader.LoadFile(
                root,
                Path.Combine("sub", "session.jsonl"));

            NUnitCompat.Multiple(() =>
            {
                Assert.That(midFlight.Interactions.Single().HasKnownOutcome, Is.False);
                Assert.That(finished.Interactions.Single().HasKnownOutcome, Is.True);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // -------------------------------------------------------------- helpers

    private static InteractionDispatchOptions Options()
    {
        return new InteractionDispatchOptions(InteractionOrigin.Test);
    }

    private static InteractionRecorderOptions RecorderOptions(
        long maxRecordingBytes = InteractionRecorderOptions.DefaultMaxRecordingBytes)
    {
        return new InteractionRecorderOptions(
            "session-1",
            "build-1",
            new FixedClock(FixedInstant),
            maxRecordingBytes);
    }

    private static string[] Lines(MemoryStream sink)
    {
        var text = Encoding.UTF8.GetString(sink.ToArray());
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static JsonElement CompletedResultElement(MemoryStream sink)
    {
        var lines = Lines(sink);
        using var document = JsonDocument.Parse(lines[^1]);
        return document.RootElement.GetProperty("result").Clone();
    }

    private static InteractionRecording Load(MemoryStream sink)
    {
        return Load(sink.ToArray());
    }

    private static InteractionRecording Load(string content)
    {
        return Load(Encoding.UTF8.GetBytes(content));
    }

    private static InteractionRecording Load(byte[] content)
    {
        using var stream = new MemoryStream(content);
        return InteractionRecordingReader.Load(stream);
    }

    private static InteractionRecordingException LoadFailure(string content)
    {
        try
        {
            Load(content);
        }
        catch (InteractionRecordingException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The recording loaded unexpectedly.");
    }

    private static string Header(int schemaVersion = 1, bool extraField = false)
    {
        var extra = extraField ? ",\"extra\":true" : string.Empty;
        return "{\"kind\":\"session\",\"schemaVersion\":" + schemaVersion
            + ",\"sessionId\":\"session-1\",\"appBuild\":\"build-1\""
            + ",\"startedAt\":\"2026-07-21T03:04:05.1230000Z\"" + extra + "}";
    }

    private static string Requested(long sequence, string arguments = "{}")
    {
        return "{\"kind\":\"interaction_requested\",\"sequence\":" + sequence
            + ",\"requestId\":\"" + RequestId(sequence) + "\",\"origin\":\"Test\""
            + ",\"command\":{\"name\":\"click\",\"version\":1"
            + ",\"targetId\":\"menu.start\",\"arguments\":" + arguments + "}}";
    }

    private static string Completed(long sequence, string requestId)
    {
        return "{\"kind\":\"interaction_completed\",\"sequence\":" + sequence
            + ",\"requestId\":\"" + requestId + "\""
            + ",\"result\":{\"status\":\"Succeeded\",\"stages\":"
            + "[{\"id\":\"execute\",\"status\":\"Completed\"}]}"
            + ",\"state\":{\"before\":{},\"after\":{}}}";
    }

    private static string RequestId(long sequence)
    {
        return "request-" + sequence;
    }

    private static InteractionResult SucceededResult(long sequence)
    {
        return new InteractionResult(
            sequence,
            RequestId(sequence),
            "menu.start",
            "click",
            1,
            InteractionOrigin.Test,
            InteractionStatus.Succeeded,
            null,
            null,
            new StageProgress(new[]
            {
                new InteractionStageProgress("execute", 0, InteractionStageStatus.Completed),
            }),
            StateObservation.Empty,
            StateObservation.Empty,
            StateDiff.Empty);
    }

    private static InteractionCommandCatalog SecretCatalog()
    {
        return new InteractionCommandCatalogBuilder()
            .Register("click", 1, ClickCommandSchema.Instance, true)
            .Register("set_secret", 1, SecretValueCommandSchema.Instance, true)
            .Build();
    }

    private static InteractionCommandCatalog BrokenCatalog()
    {
        return new InteractionCommandCatalogBuilder()
            .Register("click", 1, ClickCommandSchema.Instance, true)
            .Register("broken", 1, BrokenCommandSchema.Instance, true)
            .Build();
    }

    private static string CreateArtifactRoot()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "recordings",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FixedClock : IInteractionClock
    {
        public FixedClock(DateTimeOffset instant)
        {
            UtcNow = instant;
        }

        public DateTimeOffset UtcNow { get; }
    }

    // A MemoryStream that fails on demand: after a fixed number of successful
    // line writes, or when FailNext is set. Each recorder line is one Write call.
    private sealed class FlakySink : MemoryStream
    {
        private int lines;

        public int FailAfterLines { get; set; } = int.MaxValue;

        public bool FailNext { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailNext || lines >= FailAfterLines)
            {
                throw new IOException("The recording sink failed.");
            }

            base.Write(buffer, offset, count);
            lines++;
        }
    }

    private readonly struct SecretValueCommand : IInteractionCommand
    {
        public SecretValueCommand(string targetId, string value)
        {
            TargetId = targetId;
            Value = value;
        }

        public string TargetId { get; }

        public string Value { get; }
    }

    private sealed class SecretValueCommandSchema : IInteractionCommandSchema<SecretValueCommand>
    {
        private SecretValueCommandSchema()
        {
        }

        public static SecretValueCommandSchema Instance { get; } = new();

        public InteractionArgumentSchema Arguments { get; } = new(new[]
        {
            new InteractionArgumentDefinition(
                "value",
                InteractionArgumentType.String,
                required: true,
                sensitive: true),
        });

        public SecretValueCommand Decode(string targetId, JsonElement arguments)
        {
            return new SecretValueCommand(
                targetId,
                arguments.GetProperty("value").GetString()!);
        }

        public void WriteArguments(Utf8JsonWriter writer, in SecretValueCommand command)
        {
            writer.WriteStartObject();
            writer.WriteString("value", command.Value);
            writer.WriteEndObject();
        }
    }

    private readonly struct BrokenCommand : IInteractionCommand
    {
        public BrokenCommand(string targetId)
        {
            TargetId = targetId;
        }

        public string TargetId { get; }
    }

    // A codec that violates the arguments-are-an-object contract, to prove the
    // recorder fails fast instead of writing a line its own reader rejects.
    private sealed class BrokenCommandSchema : IInteractionCommandSchema<BrokenCommand>
    {
        private BrokenCommandSchema()
        {
        }

        public static BrokenCommandSchema Instance { get; } = new();

        public InteractionArgumentSchema Arguments
        {
            get { return InteractionArgumentSchema.Empty; }
        }

        public BrokenCommand Decode(string targetId, JsonElement arguments)
        {
            return new BrokenCommand(targetId);
        }

        public void WriteArguments(Utf8JsonWriter writer, in BrokenCommand command)
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
        }
    }

    private sealed class RecordingHarness : IDisposable
    {
        private readonly InteractionRegistry registry;
        private readonly InteractionRecorder recorder;
        private readonly bool ownsRecorder;
        private readonly List<IInteractionTargetRegistration> registrations = new();
        private readonly List<string> executionLog = new();

        public RecordingHarness(
            MemoryStream? sink = null,
            InteractionCommandCatalog? catalog = null,
            bool withProbes = false,
            InteractionRecorderOptions? options = null,
            InteractionRecorder? recorder = null)
        {
            var resolvedCatalog = catalog ?? InteractionCommandCatalog.CreateMvp();
            registry = new InteractionRegistry(resolvedCatalog, "session-1");
            Sink = sink ?? new MemoryStream();
            if (recorder != null)
            {
                this.recorder = recorder;
                ownsRecorder = false;
            }
            else
            {
                this.recorder = new InteractionRecorder(
                    Sink,
                    options ?? RecorderOptions(),
                    leaveOpen: true);
                ownsRecorder = true;
            }

            InteractionStateProbeRegistry? probes = null;
            if (withProbes)
            {
                probes = new InteractionStateProbeRegistry();
                probes.Register(new SemanticUiStateProbe(registry));
                probes.Register(new InteractionRuntimeStateProbe(registry));
            }

            Dispatcher = new InteractionDispatcher(
                resolvedCatalog,
                registry,
                probes,
                this.recorder);
        }

        public InteractionDispatcher Dispatcher { get; }

        public MemoryStream Sink { get; }

        public IReadOnlyList<string> ExecutionLog
        {
            get
            {
                lock (executionLog)
                {
                    return executionLog.ToArray();
                }
            }
        }

        public Blocker RegisterClick(
            string targetId,
            bool gate = false,
            Exception? fault = null,
            CancellationTokenSource? observeCancellation = null)
        {
            var blocker = new Blocker(gate);
            var pipeline = new RecordingPipeline<ClickCommand>(
                targetId,
                this,
                blocker,
                fault,
                observeCancellation);
            var target = new RecordingTarget(
                targetId,
                "click",
                1,
                ClickCommandSchema.Instance.Arguments,
                pipeline);
            registrations.Add(registry.Register(target, true));
            return blocker;
        }

        public void RegisterBroken(string targetId)
        {
            var pipeline = new RecordingPipeline<BrokenCommand>(
                targetId,
                this,
                new Blocker(gated: false),
                null,
                null);
            var target = new RecordingTarget(
                targetId,
                "broken",
                1,
                BrokenCommandSchema.Instance.Arguments,
                pipeline);
            registrations.Add(registry.Register(target, true));
        }

        public void RegisterSecret(string targetId)
        {
            var pipeline = new RecordingPipeline<SecretValueCommand>(
                targetId,
                this,
                new Blocker(gated: false),
                null,
                null);
            var target = new RecordingTarget(
                targetId,
                "set_secret",
                1,
                SecretValueCommandSchema.Instance.Arguments,
                pipeline);
            registrations.Add(registry.Register(target, true));
        }

        public void LogExecution(string targetId)
        {
            lock (executionLog)
            {
                executionLog.Add(targetId);
            }
        }

        public void Dispose()
        {
            Dispatcher.Dispose();
            if (ownsRecorder)
            {
                recorder.Dispose();
            }
        }
    }

    private sealed class Blocker
    {
        private readonly TaskCompletionSource<bool>? gate;

        public Blocker(bool gated)
        {
            if (gated)
            {
                gate = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Wait
        {
            get { return gate?.Task ?? Task.CompletedTask; }
        }

        public void Release()
        {
            gate?.TrySetResult(true);
        }
    }

    private sealed class RecordingTarget : IInteractionTarget
    {
        private readonly string wireName;
        private readonly int version;
        private readonly InteractionArgumentSchema arguments;
        private readonly object pipeline;

        public RecordingTarget(
            string id,
            string wireName,
            int version,
            InteractionArgumentSchema arguments,
            object pipeline)
        {
            Id = id;
            this.wireName = wireName;
            this.version = version;
            this.arguments = arguments;
            this.pipeline = pipeline;
        }

        public string Id { get; }

        public InteractionDescriptor Describe()
        {
            return new InteractionDescriptor(
                Id,
                null,
                "button",
                "Label",
                null,
                true,
                true,
                new[] { new AvailableInteraction(wireName, version, arguments) });
        }

        public bool TryGetPipeline<TCommand>(
            out IInteractionPipeline<TCommand>? resolved)
            where TCommand : struct, IInteractionCommand
        {
            resolved = pipeline as IInteractionPipeline<TCommand>;
            return resolved != null;
        }
    }

    private sealed class RecordingPipeline<TCommand> : IInteractionPipeline<TCommand>
        where TCommand : struct, IInteractionCommand
    {
        private readonly string targetId;
        private readonly RecordingHarness harness;
        private readonly Blocker blocker;
        private readonly Exception? fault;
        private readonly CancellationTokenSource? observeCancellation;

        public RecordingPipeline(
            string targetId,
            RecordingHarness harness,
            Blocker blocker,
            Exception? fault,
            CancellationTokenSource? observeCancellation)
        {
            this.targetId = targetId;
            this.harness = harness;
            this.blocker = blocker;
            this.fault = fault;
            this.observeCancellation = observeCancellation;
        }

        public InteractionValidation Validate(in TCommand command)
        {
            return InteractionValidation.Valid;
        }

        public async ValueTask ExecuteAsync(
            TCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            blocker.Started.TrySetResult(true);
            await blocker.Wait;

            if (observeCancellation != null)
            {
                observeCancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (fault != null)
            {
                throw fault;
            }

            harness.LogExecution(targetId);
        }
    }
}
