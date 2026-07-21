using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class InteractionReplayerTests
{
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
}
