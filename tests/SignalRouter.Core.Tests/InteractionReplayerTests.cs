using System.Text.Json;
using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class InteractionReplayerTests
{
    // -------------------------------------------------- §22-5: reproduction

    [Test]
    public async Task ASuccessfulRecordingReplaysToCompletionThroughTheReplayOrigin()
    {
        using var recordSide = new ReplayRuntime(record: true);
        recordSide.RegisterClick("menu.start", onExecute: (_, _, _) =>
        {
            recordSide.Counter.Increment();
            return default;
        });
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime();
        replaySide.RegisterClick("menu.start", onExecute: (_, _, _) =>
        {
            replaySide.Counter.Increment();
            return default;
        });

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(report.Outcome, Is.EqualTo(InteractionReplayOutcome.Completed));
            Assert.That(report.VerifiedInteractions, Is.EqualTo(1));
            Assert.That(report.TotalInteractions, Is.EqualTo(1));
            Assert.That(replaySide.Log, Is.EqualTo(new[] { "menu.start:Replay" }));
            Assert.That(replaySide.Counter.Value, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AStageTwoFaultIsReproducedWithItsPartialState()
    {
        using var recordSide = new ReplayRuntime(record: true);
        recordSide.RegisterStagedClick(
            "menu.start",
            new FakeStage("click.apply", 0, onExecute: () => recordSide.Counter.Increment()),
            new FakeStage("click.transition", 1, fault: AudioFault()),
            new FakeStage("click.sound", 2));
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime();
        replaySide.RegisterStagedClick(
            "menu.start",
            new FakeStage("click.apply", 0, onExecute: () => replaySide.Counter.Increment()),
            new FakeStage("click.transition", 1, fault: AudioFault()),
            new FakeStage("click.sound", 2));

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(report.Outcome, Is.EqualTo(InteractionReplayOutcome.Completed));
            Assert.That(recording.Interactions[0].Outcome!.FaultCode,
                Is.EqualTo("AudioDeviceUnavailable"));
            Assert.That(replaySide.Counter.Value, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AFaultAtADifferentStageDiverges()
    {
        using var recordSide = new ReplayRuntime(record: true);
        recordSide.RegisterStagedClick(
            "menu.start",
            new FakeStage("click.apply", 0, onExecute: () => recordSide.Counter.Increment()),
            new FakeStage("click.transition", 1, fault: AudioFault()),
            new FakeStage("click.sound", 2));
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime();
        replaySide.RegisterStagedClick(
            "menu.start",
            new FakeStage("click.apply", 0, onExecute: () => replaySide.Counter.Increment()),
            new FakeStage("click.transition", 1),
            new FakeStage("click.sound", 2, fault: AudioFault()));

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(report.Outcome, Is.EqualTo(InteractionReplayOutcome.Diverged));
            Assert.That(
                report.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.StageProgressMismatch));
            Assert.That(report.Divergence.Actual, Is.Not.Null);
        });
    }

    [Test]
    public async Task ADifferentFaultCodeDiverges()
    {
        using var recordSide = new ReplayRuntime(record: true);
        recordSide.RegisterStagedClick(
            "menu.start",
            new FakeStage("click.apply", 0),
            new FakeStage("click.transition", 1, fault: AudioFault()));
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime();
        replaySide.RegisterStagedClick(
            "menu.start",
            new FakeStage("click.apply", 0),
            new FakeStage(
                "click.transition",
                1,
                fault: new InteractionFaultException("DisplayLost", "The display was lost.")));

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        Assert.That(
            report.Divergence!.Kind,
            Is.EqualTo(InteractionReplayDivergenceKind.FaultCodeMismatch));
    }

    [Test]
    public async Task ARecordedFaultThatNowSucceedsDiverges()
    {
        using var recordSide = new ReplayRuntime(record: true);
        recordSide.RegisterStagedClick(
            "menu.start",
            new FakeStage("click.apply", 0),
            new FakeStage("click.transition", 1, fault: AudioFault()));
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime();
        replaySide.RegisterStagedClick(
            "menu.start",
            new FakeStage("click.apply", 0),
            new FakeStage("click.transition", 1));

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                report.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.StatusMismatch));
            Assert.That(
                report.Divergence.Actual!.Status,
                Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(
                report.Divergence.Expected.Status,
                Is.EqualTo(InteractionStatus.Faulted));
        });
    }

    // ---------------------------------------------- §22-6: state divergence

    [Test]
    public async Task AChangedBeforeStateStopsBeforeAnythingIsDispatched()
    {
        using var recordSide = new ReplayRuntime(record: true);
        recordSide.RegisterClick("menu.start");
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime(counterStart: 5);
        replaySide.RegisterClick("menu.start");

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(report.Outcome, Is.EqualTo(InteractionReplayOutcome.Diverged));
            Assert.That(
                report.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.BeforeStateMismatch));
            Assert.That(report.Divergence.Actual, Is.Null);
            Assert.That(report.Divergence.StateDifferences, Has.Count.EqualTo(1));
            Assert.That(
                report.Divergence.StateDifferences[0].ProbeId,
                Is.EqualTo("counter"));
            Assert.That(report.Divergence.StateDifferences[0].ExpectedHash, Is.Not.Null);
            Assert.That(report.Divergence.StateDifferences[0].ActualHash, Is.Not.Null);
            Assert.That(replaySide.Log, Is.Empty);
        });
    }

    [Test]
    public async Task AChangedAfterStateStopsTheRemainingEntries()
    {
        using var recordSide = new ReplayRuntime(record: true);
        recordSide.RegisterClick("menu.start", onExecute: (_, _, _) =>
        {
            recordSide.Counter.Increment();
            return default;
        });
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime();
        replaySide.RegisterClick("menu.start", onExecute: (_, _, _) =>
        {
            replaySide.Counter.Increment(2);
            return default;
        });

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(report.Outcome, Is.EqualTo(InteractionReplayOutcome.Diverged));
            Assert.That(
                report.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.AfterStateMismatch));
            Assert.That(report.Divergence.Entry.Sequence, Is.EqualTo(1));
            Assert.That(report.VerifiedInteractions, Is.EqualTo(0));
            Assert.That(replaySide.Log, Has.Count.EqualTo(1));
        });
    }

    // ------------------------------------------- rejected entry re-verification

    [Test]
    public async Task ARecordedRejectionReplaysWithZeroObservableStateChange()
    {
        using var recordSide = new ReplayRuntime(record: true);
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.missing"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime();

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(report.Outcome, Is.EqualTo(InteractionReplayOutcome.Completed));
            Assert.That(
                recording.Interactions[0].Outcome!.RejectionCode,
                Is.EqualTo(InteractionRejectionCode.TargetNotFound));
            Assert.That(replaySide.Log, Is.Empty);
        });
    }

    [Test]
    public async Task ARecordedRejectionThatNowSucceedsDiverges()
    {
        using var recordSide = new ReplayRuntime(record: true);
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.missing"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime();
        replaySide.RegisterClick("menu.missing");

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                report.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.StatusMismatch));
            Assert.That(
                report.Divergence.Actual!.Status,
                Is.EqualTo(InteractionStatus.Succeeded));
        });
    }

    [Test]
    public async Task ARejectionWithADifferentCodeDiverges()
    {
        using var recordSide = new ReplayRuntime(record: true);
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime();
        replaySide.RegisterClick("menu.start", enabled: false);

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                report.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.RejectionCodeMismatch));
            Assert.That(
                report.Divergence.Actual!.RejectionCode,
                Is.EqualTo(InteractionRejectionCode.Disabled));
            Assert.That(
                report.Divergence.Expected.RejectionCode,
                Is.EqualTo(InteractionRejectionCode.TargetNotFound));
        });
    }

    [Test]
    public async Task AValidationThatChangesStateViolatesTheZeroSideEffectGuarantee()
    {
        using var recordSide = new ReplayRuntime(record: true);
        recordSide.RegisterClick("menu.start", validator: _ =>
            InteractionValidation.Reject(
                InteractionRejectionCode.InvalidArguments,
                "Rejected for the test."));
        await recordSide.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        var recording = Load(recordSide.Sink!);

        using var replaySide = new ReplayRuntime();
        replaySide.RegisterClick("menu.start", validator: _ =>
        {
            replaySide.Counter.Increment();
            return InteractionValidation.Reject(
                InteractionRejectionCode.InvalidArguments,
                "Rejected for the test.");
        });

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                report.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.UnexpectedStateChange));
            Assert.That(report.Divergence.StateDifferences, Has.Count.EqualTo(1));
            Assert.That(
                report.Divergence.StateDifferences[0].ProbeId,
                Is.EqualTo("counter"));
        });
    }

    // ------------------------------------------------------------ secrets

    [Test]
    public async Task AResolvedSecretReachesThePipelineAndNeverTheReport()
    {
        var recording = RecordSecretDispatch(new SecretSchema(sensitive: true));

        using var replaySide = new ReplayRuntime(SecretCatalog(new SecretSchema(sensitive: true)));
        replaySide.RegisterSecret("vault.value", new SecretSchema(sensitive: true));
        var resolver = new MapResolver(("set_secret@1/value", InteractionValue.FromString("hunter2")));

        var report = await InteractionReplayer.ReplayAsync(
            recording,
            replaySide.Dispatcher,
            resolver);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(report.Outcome, Is.EqualTo(InteractionReplayOutcome.Completed));
            Assert.That(replaySide.Log, Is.EqualTo(new[] { "vault.value:hunter2" }));
            Assert.That(resolver.Requests, Has.Count.EqualTo(1));
            Assert.That(
                resolver.Requests[0].RequestId,
                Is.EqualTo(recording.Interactions[0].RequestId));
            Assert.That(resolver.Requests[0].Key, Is.EqualTo("set_secret@1/value"));
        });
    }

    [Test]
    public async Task AnUnresolvableSecretDivergesWithTheKeyOnly()
    {
        var recording = RecordSecretDispatch(new SecretSchema(sensitive: true));

        using var replaySide = new ReplayRuntime(SecretCatalog(new SecretSchema(sensitive: true)));
        replaySide.RegisterSecret("vault.value", new SecretSchema(sensitive: true));

        var report = await InteractionReplayer.ReplayAsync(
            recording,
            replaySide.Dispatcher,
            new MapResolver());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                report.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.SecretUnavailable));
            Assert.That(report.Divergence.SecretKey, Is.EqualTo("set_secret@1/value"));
            Assert.That(report.Divergence.Actual, Is.Null);
            Assert.That(replaySide.Log, Is.Empty);
            Assert.That(report.ToString(), Does.Not.Contain("hunter2"));
        });
    }

    [Test]
    public void AMissingSecretResolverFailsFastAtTheReferencingEntry()
    {
        var recording = RecordSecretDispatch(new SecretSchema(sensitive: true));

        using var replaySide = new ReplayRuntime(SecretCatalog(new SecretSchema(sensitive: true)));
        replaySide.RegisterSecret("vault.value", new SecretSchema(sensitive: true));

        var exception = NUnitCompat.ThrowsAsync<InteractionReplayException>(async () =>
            await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher));

        Assert.That(
            exception!.Error,
            Is.EqualTo(InteractionReplayError.SecretResolverMissing));
    }

    [Test]
    public async Task AWrongKindSecretResolutionDiverges()
    {
        var recording = RecordSecretDispatch(new SecretSchema(sensitive: true));

        using var replaySide = new ReplayRuntime(SecretCatalog(new SecretSchema(sensitive: true)));
        replaySide.RegisterSecret("vault.value", new SecretSchema(sensitive: true));
        var resolver = new MapResolver(("set_secret@1/value", InteractionValue.FromNumber(42m)));

        var report = await InteractionReplayer.ReplayAsync(
            recording,
            replaySide.Dispatcher,
            resolver);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                report.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.ArgumentSchemaMismatch));
            Assert.That(report.Divergence.ArgumentName, Is.EqualTo("value"));
        });
    }

    [Test]
    public void ANullKindSecretResolutionBreaksTheResolverContract()
    {
        var recording = RecordSecretDispatch(new SecretSchema(sensitive: true));

        using var replaySide = new ReplayRuntime(SecretCatalog(new SecretSchema(sensitive: true)));
        replaySide.RegisterSecret("vault.value", new SecretSchema(sensitive: true));
        var resolver = new MapResolver(("set_secret@1/value", InteractionValue.Null));

        var exception = NUnitCompat.ThrowsAsync<InteractionReplayException>(async () =>
            await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher, resolver));

        Assert.That(
            exception!.Error,
            Is.EqualTo(InteractionReplayError.SecretResolverContract));
    }

    [Test]
    public async Task SensitivityDriftDivergesInBothDirectionsWithoutLeakingPlaintext()
    {
        // A recorded marker whose argument is no longer sensitive.
        var markerRecording = RecordSecretDispatch(new SecretSchema(sensitive: true));
        using var relaxedSide = new ReplayRuntime(SecretCatalog(new SecretSchema(sensitive: false)));
        relaxedSide.RegisterSecret("vault.value", new SecretSchema(sensitive: false));
        var markerReport = await InteractionReplayer.ReplayAsync(
            markerRecording,
            relaxedSide.Dispatcher,
            new MapResolver(("set_secret@1/value", InteractionValue.FromString("hunter2"))));

        // Recorded plaintext for an argument that is now sensitive.
        var plaintextRecording = RecordSecretDispatch(new SecretSchema(sensitive: false));
        using var upgradedSide = new ReplayRuntime(SecretCatalog(new SecretSchema(sensitive: true)));
        upgradedSide.RegisterSecret("vault.value", new SecretSchema(sensitive: true));
        var plaintextReport = await InteractionReplayer.ReplayAsync(
            plaintextRecording,
            upgradedSide.Dispatcher,
            new MapResolver(("set_secret@1/value", InteractionValue.FromString("hunter2"))));

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                markerReport.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.ArgumentSchemaMismatch));
            Assert.That(markerReport.Divergence.ArgumentName, Is.EqualTo("value"));
            Assert.That(
                plaintextReport.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.ArgumentSchemaMismatch));
            Assert.That(plaintextReport.Divergence.ArgumentName, Is.EqualTo("value"));
            Assert.That(plaintextReport.ToString(), Does.Not.Contain("hunter2"));
            Assert.That(upgradedSide.Log, Is.Empty);
        });
    }

    // ----------------------------------------------- catalog and decode drift

    [Test]
    public async Task ACommandMissingFromTheCatalogDiverges()
    {
        var recording = RecordSecretDispatch(new SecretSchema(sensitive: true));

        using var replaySide = new ReplayRuntime();

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                report.Divergence!.Kind,
                Is.EqualTo(InteractionReplayDivergenceKind.CommandNotInCatalog));
            Assert.That(report.Divergence.Entry.CommandName, Is.EqualTo("set_secret"));
            Assert.That(report.Divergence.Actual, Is.Null);
        });
    }

    [Test]
    public async Task ArgumentsTheCodecNoLongerAcceptsDiverge()
    {
        var recording = RecordSecretDispatch(new SecretSchema(sensitive: false));

        using var replaySide = new ReplayRuntime(
            SecretCatalog(new UndecodableSecretSchema()));
        replaySide.RegisterSecret("vault.value", new UndecodableSecretSchema());

        var report = await InteractionReplayer.ReplayAsync(recording, replaySide.Dispatcher);

        Assert.That(
            report.Divergence!.Kind,
            Is.EqualTo(InteractionReplayDivergenceKind.ArgumentsNotDecodable));
    }

    // ----------------------------------------------------------- report models

    [Test]
    public void EntryRefValidatesItsIdentityFields()
    {
        NUnitCompat.Multiple(() =>
        {
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayEntryRef(0, "request-1", "click", 1, "menu.start"),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayEntryRef(1, "request-1", "click", 0, "menu.start"),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayEntryRef(1, "", "click", 1, "menu.start"),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void EntryRefProjectionCarriesIdentityButNoArguments()
    {
        var entry = new RecordedInteraction(
            7,
            "request-7",
            InteractionOrigin.Test,
            "click",
            1,
            "menu.start",
            "{\"value\":\"plaintext\"}",
            outcome: null);

        var reference = InteractionReplayEntryRef.From(entry);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(reference.Sequence, Is.EqualTo(7));
            Assert.That(reference.RequestId, Is.EqualTo("request-7"));
            Assert.That(reference.CommandName, Is.EqualTo("click"));
            Assert.That(reference.CommandVersion, Is.EqualTo(1));
            Assert.That(reference.TargetId, Is.EqualTo("menu.start"));
        });
    }

    [Test]
    public void AStateDifferenceRequiresDifferingHashesAndAtLeastOneSide()
    {
        NUnitCompat.Multiple(() =>
        {
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayStateDifference("probe", null, null),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayStateDifference("probe", HashA, HashA),
                Throws.ArgumentException);
            Assert.That(
                new InteractionReplayStateDifference("probe", HashA, null).ActualHash,
                Is.Null);
            Assert.That(
                new InteractionReplayStateDifference("probe", null, HashB).ExpectedHash,
                Is.Null);
            Assert.That(
                new InteractionReplayStateDifference("probe", HashA, HashB).ProbeId,
                Is.EqualTo("probe"));
        });
    }

    [Test]
    public void ADivergenceTiesItsDetailFieldsToItsKind()
    {
        NUnitCompat.Multiple(() =>
        {
            // An argument name is carried exactly by ArgumentSchemaMismatch.
            NUnitCompat.ThatThrows(
                () => _ = Divergence(
                    InteractionReplayDivergenceKind.ArgumentSchemaMismatch,
                    argumentName: null),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => _ = Divergence(
                    InteractionReplayDivergenceKind.CommandNotInCatalog,
                    argumentName: "value"),
                Throws.ArgumentException);

            // A secret key is carried exactly by SecretUnavailable.
            NUnitCompat.ThatThrows(
                () => _ = Divergence(
                    InteractionReplayDivergenceKind.SecretUnavailable,
                    secretKey: null),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => _ = Divergence(
                    InteractionReplayDivergenceKind.CommandNotInCatalog,
                    secretKey: "click@1/value"),
                Throws.ArgumentException);

            // State differences are carried exactly by state divergences.
            NUnitCompat.ThatThrows(
                () => _ = Divergence(InteractionReplayDivergenceKind.BeforeStateMismatch),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => _ = Divergence(
                    InteractionReplayDivergenceKind.CommandNotInCatalog,
                    differences: OneDifference()),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void ADivergenceRequiresAnActualOutcomeExactlyAfterDispatch()
    {
        NUnitCompat.Multiple(() =>
        {
            // Pre-dispatch kinds must not carry an actual outcome.
            NUnitCompat.ThatThrows(
                () => _ = Divergence(
                    InteractionReplayDivergenceKind.CommandNotInCatalog,
                    actual: SucceededOutcome()),
                Throws.ArgumentException);

            // Post-dispatch kinds require one.
            NUnitCompat.ThatThrows(
                () => _ = Divergence(InteractionReplayDivergenceKind.StatusMismatch),
                Throws.ArgumentException);

            // BeforeStateMismatch is legal both before dispatch (step 3) and on the
            // defensive post-dispatch re-check.
            Assert.That(
                Divergence(
                    InteractionReplayDivergenceKind.BeforeStateMismatch,
                    differences: OneDifference()).Actual,
                Is.Null);
            Assert.That(
                Divergence(
                    InteractionReplayDivergenceKind.BeforeStateMismatch,
                    actual: SucceededOutcome(),
                    differences: OneDifference()).Actual,
                Is.Not.Null);
        });
    }

    [Test]
    public void ACompletedReportMustHaveVerifiedEverythingAndCarryNoDetails()
    {
        NUnitCompat.Multiple(() =>
        {
            var report = new InteractionReplayReport(
                InteractionReplayOutcome.Completed,
                2,
                2,
                stopReason: null,
                stoppedBefore: null,
                divergence: null);
            Assert.That(report.VerifiedInteractions, Is.EqualTo(2));
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayReport(
                    InteractionReplayOutcome.Completed,
                    2,
                    1,
                    stopReason: null,
                    stoppedBefore: null,
                    divergence: null),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayReport(
                    InteractionReplayOutcome.Completed,
                    1,
                    1,
                    stopReason: null,
                    stoppedBefore: null,
                    divergence: Divergence(InteractionReplayDivergenceKind.CommandNotInCatalog)),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void ADivergedReportRequiresDivergenceInformationShortOfTheEnd()
    {
        NUnitCompat.Multiple(() =>
        {
            var report = new InteractionReplayReport(
                InteractionReplayOutcome.Diverged,
                2,
                1,
                stopReason: null,
                stoppedBefore: null,
                divergence: Divergence(InteractionReplayDivergenceKind.CommandNotInCatalog));
            Assert.That(report.Divergence, Is.Not.Null);
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayReport(
                    InteractionReplayOutcome.Diverged,
                    2,
                    1,
                    stopReason: null,
                    stoppedBefore: null,
                    divergence: null),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayReport(
                    InteractionReplayOutcome.Diverged,
                    2,
                    2,
                    stopReason: null,
                    stoppedBefore: null,
                    divergence: Divergence(InteractionReplayDivergenceKind.CommandNotInCatalog)),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void AStoppedReportNamesTheFirstUnreplayedEntryExceptATrailingContinuation()
    {
        NUnitCompat.Multiple(() =>
        {
            var stopped = new InteractionReplayReport(
                InteractionReplayOutcome.Stopped,
                2,
                1,
                InteractionReplayStopReason.OutcomeUnknown,
                EntryRef(),
                divergence: null);
            Assert.That(stopped.StopReason, Is.EqualTo(InteractionReplayStopReason.OutcomeUnknown));

            // A continuation requested by the final entry leaves nothing to stop before.
            var trailing = new InteractionReplayReport(
                InteractionReplayOutcome.Stopped,
                2,
                2,
                InteractionReplayStopReason.ContinuationRequested,
                stoppedBefore: null,
                divergence: null);
            Assert.That(trailing.StoppedBefore, Is.Null);

            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayReport(
                    InteractionReplayOutcome.Stopped,
                    2,
                    1,
                    stopReason: null,
                    stoppedBefore: EntryRef(),
                    divergence: null),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayReport(
                    InteractionReplayOutcome.Stopped,
                    2,
                    2,
                    InteractionReplayStopReason.OutcomeUnknown,
                    stoppedBefore: null,
                    divergence: null),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayReport(
                    InteractionReplayOutcome.Stopped,
                    2,
                    2,
                    InteractionReplayStopReason.OutcomeUnknown,
                    stoppedBefore: EntryRef(),
                    divergence: null),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => _ = new InteractionReplayReport(
                    InteractionReplayOutcome.Stopped,
                    2,
                    3,
                    InteractionReplayStopReason.OutcomeUnknown,
                    stoppedBefore: EntryRef(),
                    divergence: null),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void TheSanitizedOutcomeProjectionDropsEveryExceptionDetail()
    {
        var fault = new FaultInfo(
            "System.InvalidOperationException",
            "boom with sensitive context",
            "at Game.Audio.Play()",
            "AudioDeviceUnavailable",
            "execute",
            0,
            Array.Empty<string>());
        var result = new InteractionResult(
            1,
            "request-1",
            "menu.start",
            "click",
            1,
            InteractionOrigin.Replay,
            InteractionStatus.Faulted,
            rejection: null,
            fault,
            new StageProgress(new[]
            {
                new InteractionStageProgress("execute", 0, InteractionStageStatus.Faulted),
            }),
            StateObservation.Empty,
            StateObservation.Empty,
            StateDiff.Empty);

        var outcome = RecordedOutcome.FromResult(result);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(InteractionStatus.Faulted));
            Assert.That(outcome.FaultCode, Is.EqualTo("AudioDeviceUnavailable"));
            Assert.That(outcome.Stages, Has.Count.EqualTo(1));
            Assert.That(outcome.RejectionCode, Is.Null);
        });
    }

    [Test]
    public void TheSanitizedOutcomeProjectionCoversEveryTerminalShape()
    {
        var rejected = new InteractionResult(
            1,
            "request-1",
            "menu.start",
            "click",
            1,
            InteractionOrigin.Replay,
            InteractionStatus.Rejected,
            new RejectionInfo(InteractionRejectionCode.TargetNotFound, "Missing."),
            fault: null,
            StageProgress.Empty,
            StateObservation.Empty,
            StateObservation.Empty,
            StateDiff.Empty);
        var cancelled = new InteractionResult(
            2,
            "request-2",
            "menu.start",
            "click",
            1,
            InteractionOrigin.Replay,
            InteractionStatus.Cancelled,
            rejection: null,
            fault: null,
            StageProgress.Empty,
            StateObservation.Empty,
            StateObservation.Empty,
            StateDiff.Empty);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                RecordedOutcome.FromResult(rejected).RejectionCode,
                Is.EqualTo(InteractionRejectionCode.TargetNotFound));
            Assert.That(RecordedOutcome.FromResult(rejected).FaultCode, Is.Null);
            Assert.That(
                RecordedOutcome.FromResult(cancelled).Status,
                Is.EqualTo(InteractionStatus.Cancelled));
            Assert.That(RecordedOutcome.FromResult(cancelled).Stages, Is.Empty);
        });
    }

    // ---------------------------------------------------------------- helpers

    private const string HashA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string HashB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static InteractionReplayEntryRef EntryRef()
    {
        return new InteractionReplayEntryRef(1, "request-1", "click", 1, "menu.start");
    }

    private static RecordedOutcome SucceededOutcome()
    {
        return new RecordedOutcome(
            InteractionStatus.Succeeded,
            new[]
            {
                new InteractionStageProgress("execute", 0, InteractionStageStatus.Completed),
            },
            rejectionCode: null,
            faultCode: null,
            StateObservation.Empty,
            StateObservation.Empty);
    }

    private static InteractionReplayDivergence Divergence(
        InteractionReplayDivergenceKind kind,
        string? argumentName = null,
        string? secretKey = null,
        RecordedOutcome? actual = null,
        IEnumerable<InteractionReplayStateDifference>? differences = null)
    {
        return new InteractionReplayDivergence(
            EntryRef(),
            kind,
            argumentName,
            secretKey,
            SucceededOutcome(),
            actual,
            differences ?? Array.Empty<InteractionReplayStateDifference>());
    }

    private static InteractionReplayStateDifference[] OneDifference()
    {
        return new[] { new InteractionReplayStateDifference("probe", HashA, HashB) };
    }

    private static readonly DateTimeOffset FixedInstant =
        new(2026, 7, 21, 3, 4, 5, 123, TimeSpan.Zero);

    private static InteractionDispatchOptions Options()
    {
        return new InteractionDispatchOptions(InteractionOrigin.Test);
    }

    private static InteractionRecording Load(MemoryStream sink)
    {
        using var stream = new MemoryStream(sink.ToArray());
        return InteractionRecordingReader.Load(stream);
    }

    private static InteractionFaultException AudioFault()
    {
        return new InteractionFaultException(
            "AudioDeviceUnavailable",
            "The audio device is unavailable.");
    }

    private static InteractionCommandCatalog SecretCatalog(
        IInteractionCommandSchema<SecretCommand> schema)
    {
        return new InteractionCommandCatalogBuilder()
            .Register("set_secret", 1, schema, true)
            .Build();
    }

    private static InteractionRecording RecordSecretDispatch(
        IInteractionCommandSchema<SecretCommand> schema)
    {
        using var recordSide = new ReplayRuntime(SecretCatalog(schema), record: true);
        recordSide.RegisterSecret("vault.value", schema);
        recordSide.Dispatcher.DispatchAsync(
            new SecretCommand("vault.value", "hunter2"),
            Options()).AsTask().GetAwaiter().GetResult();
        return Load(recordSide.Sink!);
    }

    // A record-phase / replay-phase pair share this runtime shape: the replay
    // side reconstructs a registry with the recorded session ID and re-registers
    // equivalent targets in the same order so the built-in probe hashes line up
    // (ADR 0005 replay precondition); a mutable counter probe provides
    // deterministic, test-controlled state changes.
    private sealed class ReplayRuntime : IDisposable
    {
        private readonly List<IInteractionTargetRegistration> registrations = new();
        private readonly List<string> log = new();

        public ReplayRuntime(
            InteractionCommandCatalog? catalog = null,
            bool record = false,
            int counterStart = 0,
            string sessionId = "session-1")
        {
            Catalog = catalog ?? InteractionCommandCatalog.CreateMvp();
            Registry = new InteractionRegistry(Catalog, sessionId);
            Counter = new CounterProbe(counterStart);
            Probes = new InteractionStateProbeRegistry();
            Probes.Register(new SemanticUiStateProbe(Registry));
            Probes.Register(new InteractionRuntimeStateProbe(Registry));
            Probes.Register(Counter);
            if (record)
            {
                Sink = new MemoryStream();
                Recorder = new InteractionRecorder(
                    Sink,
                    new InteractionRecorderOptions(
                        sessionId,
                        "build-1",
                        new FixedClock(FixedInstant)),
                    leaveOpen: true);
            }

            Dispatcher = new InteractionDispatcher(Catalog, Registry, Probes, Recorder);
        }

        public InteractionCommandCatalog Catalog { get; }

        public InteractionRegistry Registry { get; }

        public InteractionStateProbeRegistry Probes { get; }

        public CounterProbe Counter { get; }

        public MemoryStream? Sink { get; }

        public InteractionRecorder? Recorder { get; }

        public InteractionDispatcher Dispatcher { get; }

        public IReadOnlyList<string> Log
        {
            get
            {
                lock (log)
                {
                    return log.ToArray();
                }
            }
        }

        public void LogEntry(string entry)
        {
            lock (log)
            {
                log.Add(entry);
            }
        }

        public void RegisterClick(
            string targetId,
            Func<ClickCommand, InteractionValidation>? validator = null,
            Func<ClickCommand, InteractionContext, CancellationToken, ValueTask>? onExecute = null,
            bool enabled = true)
        {
            var pipeline = new FakePipeline<ClickCommand>(validator, async (command, context, token) =>
            {
                LogEntry(command.TargetId + ":" + context.Options.Origin);
                if (onExecute != null)
                {
                    await onExecute(command, context, token);
                }
            });
            Register(targetId, "click", 1, ClickCommandSchema.Instance.Arguments, pipeline, enabled);
        }

        public void RegisterStagedClick(string targetId, params FakeStage[] stages)
        {
            var pipeline = new StagePipeline<ClickCommand>(stages);
            Register(targetId, "click", 1, ClickCommandSchema.Instance.Arguments, pipeline, enabled: true);
        }

        public void RegisterSecret(
            string targetId,
            IInteractionCommandSchema<SecretCommand> schema)
        {
            var pipeline = new FakePipeline<SecretCommand>(null, (command, _, _) =>
            {
                LogEntry(command.TargetId + ":" + command.Value);
                return default;
            });
            Register(targetId, "set_secret", 1, schema.Arguments, pipeline, enabled: true);
        }

        public void Dispose()
        {
            Dispatcher.Dispose();
            Recorder?.Dispose();
        }

        private void Register(
            string targetId,
            string wireName,
            int version,
            InteractionArgumentSchema arguments,
            object pipeline,
            bool enabled)
        {
            registrations.Add(Registry.Register(
                new FakeTarget(targetId, wireName, version, arguments, pipeline, enabled),
                true));
        }
    }

    private sealed class CounterProbe : IInteractionStateProbe
    {
        private int value;

        public CounterProbe(int start)
        {
            value = start;
        }

        public string Id => "counter";

        public int Version => 1;

        public int Value => Volatile.Read(ref value);

        public void Increment(int amount = 1)
        {
            Interlocked.Add(ref value, amount);
        }

        public StateProbeSnapshot Capture()
        {
            return StateProbeSnapshot.FromJson(
                "{\"value\":" + Volatile.Read(ref value) + "}");
        }
    }

    private sealed class FixedClock : IInteractionClock
    {
        public FixedClock(DateTimeOffset instant)
        {
            UtcNow = instant;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class FakeTarget : IInteractionTarget
    {
        private readonly string wireName;
        private readonly int version;
        private readonly InteractionArgumentSchema arguments;
        private readonly object pipeline;
        private readonly bool enabled;

        public FakeTarget(
            string id,
            string wireName,
            int version,
            InteractionArgumentSchema arguments,
            object pipeline,
            bool enabled)
        {
            Id = id;
            this.wireName = wireName;
            this.version = version;
            this.arguments = arguments;
            this.pipeline = pipeline;
            this.enabled = enabled;
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
                visible: true,
                enabled,
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

    private sealed class FakePipeline<TCommand> : IInteractionPipeline<TCommand>
        where TCommand : struct, IInteractionCommand
    {
        private readonly Func<TCommand, InteractionValidation>? validator;
        private readonly Func<TCommand, InteractionContext, CancellationToken, ValueTask>? onExecute;

        public FakePipeline(
            Func<TCommand, InteractionValidation>? validator,
            Func<TCommand, InteractionContext, CancellationToken, ValueTask>? onExecute)
        {
            this.validator = validator;
            this.onExecute = onExecute;
        }

        public InteractionValidation Validate(in TCommand command)
        {
            return validator == null ? InteractionValidation.Valid : validator(command);
        }

        public ValueTask ExecuteAsync(
            TCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            return onExecute == null
                ? default
                : onExecute(command, context, cancellationToken);
        }
    }

    private sealed class FakeStage : IInteractionStage<ClickCommand>
    {
        private readonly Action? onExecute;
        private readonly Exception? fault;

        public FakeStage(string id, int order, Action? onExecute = null, Exception? fault = null)
        {
            Id = id;
            Order = order;
            this.onExecute = onExecute;
            this.fault = fault;
        }

        public string Id { get; }

        public int Order { get; }

        public ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            onExecute?.Invoke();
            if (fault != null)
            {
                throw fault;
            }

            return default;
        }
    }

    private readonly struct SecretCommand : IInteractionCommand
    {
        public SecretCommand(string targetId, string value)
        {
            TargetId = targetId;
            Value = value;
        }

        public string TargetId { get; }

        public string Value { get; }
    }

    private sealed class SecretSchema : IInteractionCommandSchema<SecretCommand>
    {
        public SecretSchema(bool sensitive)
        {
            Arguments = new InteractionArgumentSchema(new[]
            {
                new InteractionArgumentDefinition(
                    "value",
                    InteractionArgumentType.String,
                    required: true,
                    sensitive),
            });
        }

        public InteractionArgumentSchema Arguments { get; }

        public SecretCommand Decode(string targetId, JsonElement arguments)
        {
            return new SecretCommand(
                targetId,
                arguments.GetProperty("value").GetString()!);
        }

        public void WriteArguments(Utf8JsonWriter writer, in SecretCommand command)
        {
            writer.WriteStartObject();
            writer.WriteString("value", command.Value);
            writer.WriteEndObject();
        }
    }

    // Accepts the same argument schema but rejects every decode, standing in for
    // a codec whose parsing rules tightened after the recording was made.
    private sealed class UndecodableSecretSchema : IInteractionCommandSchema<SecretCommand>
    {
        public InteractionArgumentSchema Arguments { get; } = new(new[]
        {
            new InteractionArgumentDefinition(
                "value",
                InteractionArgumentType.String,
                required: true,
                sensitive: false),
        });

        public SecretCommand Decode(string targetId, JsonElement arguments)
        {
            throw new InteractionCommandException(
                InteractionRejectionCode.InvalidArguments,
                "The recorded arguments are no longer accepted.");
        }

        public void WriteArguments(Utf8JsonWriter writer, in SecretCommand command)
        {
            writer.WriteStartObject();
            writer.WriteString("value", command.Value);
            writer.WriteEndObject();
        }
    }

    private sealed class MapResolver : IInteractionSecretResolver
    {
        private readonly Dictionary<string, InteractionValue> values =
            new(StringComparer.Ordinal);

        public MapResolver(params (string Key, InteractionValue Value)[] entries)
        {
            foreach (var (key, value) in entries)
            {
                values.Add(key, value);
            }
        }

        public List<(string RequestId, string Key)> Requests { get; } = new();

        public bool TryResolve(string requestId, string key, out InteractionValue? value)
        {
            Requests.Add((requestId, key));
            if (values.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }

            value = null;
            return false;
        }
    }
}
