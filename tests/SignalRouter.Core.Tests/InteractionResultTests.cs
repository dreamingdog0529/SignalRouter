using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class InteractionResultTests
{
    [Test]
    public void AllTerminalStatusesAcceptTheirValidShapes()
    {
        var unchanged = Observation("hash-a");
        var changed = Observation("hash-b");
        var diff = Diff("hash-a", "hash-b");
        var succeededStages = Progress(
            Stage("apply", 0, InteractionStageStatus.Completed),
            Stage("sound", 1, InteractionStageStatus.Completed));
        var faultedStages = Progress(
            Stage("apply", 0, InteractionStageStatus.Completed),
            Stage("sound", 1, InteractionStageStatus.Faulted));
        var cancelledStages = Progress(
            Stage("apply", 0, InteractionStageStatus.Completed),
            Stage("transition", 1, InteractionStageStatus.Cancelled));

        var succeeded = Result(
            InteractionStatus.Succeeded,
            succeededStages,
            unchanged,
            changed,
            diff: diff);
        var rejected = Result(
            InteractionStatus.Rejected,
            StageProgress.Empty,
            unchanged,
            unchanged,
            rejection: new RejectionInfo(
                InteractionRejectionCode.Disabled,
                "The target is disabled."));
        var faulted = Result(
            InteractionStatus.Faulted,
            faultedStages,
            unchanged,
            changed,
            diff: diff,
            fault: new FaultInfo(
                "System.InvalidOperationException",
                "Audio failed.",
                "stack",
                "AudioDeviceUnavailable",
                "sound",
                1,
                new[] { "apply" }));
        var cancelledBeforeStart = Result(
            InteractionStatus.Cancelled,
            StageProgress.Empty,
            unchanged,
            unchanged);
        var cancelledDuringExecution = Result(
            InteractionStatus.Cancelled,
            cancelledStages,
            unchanged,
            changed,
            diff: diff);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(succeeded.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(rejected.Status, Is.EqualTo(InteractionStatus.Rejected));
            Assert.That(faulted.Status, Is.EqualTo(InteractionStatus.Faulted));
            Assert.That(cancelledBeforeStart.Status, Is.EqualTo(InteractionStatus.Cancelled));
            Assert.That(cancelledDuringExecution.Stages.Stages[^1].Status,
                Is.EqualTo(InteractionStageStatus.Cancelled));
        });
    }

    [Test]
    public void StatusSpecificInvariantViolationsFailFast()
    {
        var unchanged = Observation("hash-a");
        var changed = Observation("hash-b");
        var diff = Diff("hash-a", "hash-b");
        var completed = Progress(Stage("apply", 0, InteractionStageStatus.Completed));
        var faulted = Progress(Stage("apply", 0, InteractionStageStatus.Faulted));
        var rejection = new RejectionInfo(
            InteractionRejectionCode.InvalidArguments,
            "Arguments are invalid.");
        var fault = new FaultInfo(
            "System.Exception",
            "failed",
            null,
            null,
            "apply",
            0,
            Array.Empty<string>());

        NUnitCompat.Multiple(() =>
        {
            NUnitCompat.ThatThrows(
                () => Result(
                    InteractionStatus.Succeeded,
                    completed,
                    unchanged,
                    unchanged,
                    rejection: rejection),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => Result(
                    InteractionStatus.Rejected,
                    completed,
                    unchanged,
                    unchanged,
                    rejection: rejection),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => Result(
                    InteractionStatus.Rejected,
                    StageProgress.Empty,
                    unchanged,
                    changed,
                    diff: diff,
                    rejection: rejection),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => Result(
                    InteractionStatus.Faulted,
                    completed,
                    unchanged,
                    unchanged,
                    fault: fault),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => Result(
                    InteractionStatus.Cancelled,
                    completed,
                    unchanged,
                    unchanged),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => Result(
                    InteractionStatus.Faulted,
                    faulted,
                    unchanged,
                    unchanged,
                    fault: new FaultInfo(
                        "System.Exception",
                        "failed",
                        null,
                        null,
                        "different",
                        0,
                        Array.Empty<string>())),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void StateAndStageCollectionsAreDefensivelyCopiedAndStructurallyEqual()
    {
        var stageArray = new[]
        {
            Stage("apply", 0, InteractionStageStatus.Completed),
        };
        var observationArray = new[]
        {
            new StateProbeObservation("semantic-ui", "hash-a"),
        };
        var completedIds = new[] { "apply" };

        var progress = new StageProgress(stageArray);
        var observation = new StateObservation(observationArray);
        var fault = new FaultInfo(
            "System.Exception",
            "failed",
            null,
            null,
            "sound",
            1,
            completedIds);

        stageArray[0] = Stage("changed", 0, InteractionStageStatus.Completed);
        observationArray[0] = new StateProbeObservation("changed", "changed");
        completedIds[0] = "changed";

        NUnitCompat.Multiple(() =>
        {
            Assert.That(progress.Stages[0].Id, Is.EqualTo("apply"));
            Assert.That(observation.Probes[0].ProbeId, Is.EqualTo("semantic-ui"));
            Assert.That(fault.CompletedStageIds[0], Is.EqualTo("apply"));
            Assert.That(progress, Is.EqualTo(Progress(
                Stage("apply", 0, InteractionStageStatus.Completed))));
            Assert.That(observation, Is.EqualTo(Observation("hash-a")));
        });
    }

    [Test]
    public void StateDiffMustExactlyMatchObservationHashes()
    {
        var before = Observation("hash-a");
        var after = Observation("hash-b");

        NUnitCompat.Multiple(() =>
        {
            NUnitCompat.ThatThrows(
                () => Result(
                    InteractionStatus.Succeeded,
                    StageProgress.Empty,
                    before,
                    after),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => Result(
                    InteractionStatus.Succeeded,
                    StageProgress.Empty,
                    before,
                    after,
                    diff: Diff("wrong", "hash-b")),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void AnInteractionFaultExceptionRequiresACodeAndMessage()
    {
        var inner = new InvalidOperationException("boom");
        var fault = new InteractionFaultException(
            "AudioDeviceUnavailable",
            "The audio device is unavailable.",
            inner);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(fault.ApplicationCode, Is.EqualTo("AudioDeviceUnavailable"));
            Assert.That(fault.Message, Is.EqualTo("The audio device is unavailable."));
            Assert.That(fault.InnerException, Is.SameAs(inner));
            NUnitCompat.ThatThrows(
                () => new InteractionFaultException(" padded ", "message"),
                Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => new InteractionFaultException("code", ""),
                Throws.ArgumentException);
        });
    }

    private static InteractionResult Result(
        InteractionStatus status,
        StageProgress stages,
        StateObservation before,
        StateObservation after,
        RejectionInfo? rejection = null,
        FaultInfo? fault = null,
        StateDiff? diff = null)
    {
        return new InteractionResult(
            1,
            "request-1",
            "menu.start",
            "click",
            1,
            InteractionOrigin.Test,
            status,
            rejection,
            fault,
            stages,
            before,
            after,
            diff ?? StateDiff.Empty);
    }

    private static InteractionStageProgress Stage(
        string id,
        int index,
        InteractionStageStatus status)
    {
        return new InteractionStageProgress(id, index, status);
    }

    private static StageProgress Progress(params InteractionStageProgress[] stages)
    {
        return new StageProgress(stages);
    }

    private static StateObservation Observation(string hash)
    {
        return new StateObservation(
            new[] { new StateProbeObservation("semantic-ui", hash) });
    }

    private static StateDiff Diff(string beforeHash, string afterHash)
    {
        return new StateDiff(
            new[]
            {
                new StateProbeDiff(
                    "semantic-ui",
                    beforeHash,
                    afterHash,
                    new[]
                    {
                        new StatePropertyChange(
                            "menu.start.enabled",
                            InteractionValue.FromBoolean(false),
                            InteractionValue.FromBoolean(true)),
                    }),
            });
    }
}
