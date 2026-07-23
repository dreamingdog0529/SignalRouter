using NUnit.Framework;
using SignalRouter;

namespace SignalRouter.Protocol.Tests;

public sealed class ProtocolInteractionOutcomeTests
{
    [Test]
    public void ProjectsASucceededResultOntoTheWireSubset()
    {
        var result = CreateResult(InteractionStatus.Succeeded);
        var outcome = ProtocolInteractionOutcome.FromResult(result);

        Assert.That(outcome.Sequence, Is.EqualTo(result.Sequence));
        Assert.That(outcome.RequestId, Is.EqualTo(result.RequestId));
        Assert.That(outcome.TargetId, Is.EqualTo(result.TargetId));
        Assert.That(outcome.CommandName, Is.EqualTo(result.CommandName));
        Assert.That(outcome.CommandVersion, Is.EqualTo(result.CommandVersion));
        Assert.That(outcome.Origin, Is.EqualTo(InteractionOrigin.Agent));
        Assert.That(outcome.Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(outcome.Stages, Is.EqualTo(result.Stages.Stages));
        Assert.That(outcome.RejectionCode, Is.Null);
        Assert.That(outcome.FaultCode, Is.Null);
        Assert.That(outcome.Before, Is.EqualTo(result.Before));
        Assert.That(outcome.After, Is.EqualTo(result.After));
    }

    [Test]
    public void ProjectsARejectionOntoItsCodeAndDropsTheMessage()
    {
        var result = CreateResult(InteractionStatus.Rejected);
        var outcome = ProtocolInteractionOutcome.FromResult(result);

        Assert.That(outcome.RejectionCode, Is.EqualTo(InteractionRejectionCode.Disabled));
        Assert.That(outcome.Stages, Is.Empty);
        Assert.That(outcome.FaultCode, Is.Null);
    }

    [Test]
    public void ProjectsAFaultOntoItsApplicationCodeOnly()
    {
        var result = CreateResult(InteractionStatus.Faulted);
        var outcome = ProtocolInteractionOutcome.FromResult(result);

        Assert.That(outcome.FaultCode, Is.EqualTo("app-code"));
        Assert.That(outcome.Status, Is.EqualTo(InteractionStatus.Faulted));
        Assert.That(outcome.RejectionCode, Is.Null);
    }

    [Test]
    public void ProjectsACancellationBeforeExecution()
    {
        var result = CreateResult(InteractionStatus.Cancelled);
        var outcome = ProtocolInteractionOutcome.FromResult(result);

        Assert.That(outcome.Status, Is.EqualTo(InteractionStatus.Cancelled));
        Assert.That(outcome.Stages, Is.Empty);
    }

    [Test]
    public void RejectedOutcomesRequireACodeAndNothingElse()
    {
        NUnitCompat.Throws<ArgumentException>(() => CreateOutcome(
            InteractionStatus.Rejected,
            rejectionCode: null));
        NUnitCompat.Throws<ArgumentException>(() => CreateOutcome(
            InteractionStatus.Rejected,
            rejectionCode: InteractionRejectionCode.Disabled,
            faultCode: "app-code"));
        NUnitCompat.Throws<ArgumentException>(() => CreateOutcome(
            InteractionStatus.Rejected,
            rejectionCode: InteractionRejectionCode.Disabled,
            stages: new[]
            {
                new InteractionStageProgress("apply", 0, InteractionStageStatus.Completed),
            }));
    }

    [Test]
    public void SucceededOutcomesMustNotCarryCodesOrFailedStages()
    {
        NUnitCompat.Throws<ArgumentException>(() => CreateOutcome(
            InteractionStatus.Succeeded,
            rejectionCode: InteractionRejectionCode.Disabled));
        NUnitCompat.Throws<ArgumentException>(() => CreateOutcome(
            InteractionStatus.Succeeded,
            stages: new[]
            {
                new InteractionStageProgress("apply", 0, InteractionStageStatus.Faulted),
            }));
    }

    [Test]
    public void FaultedOutcomesMustEndWithAFaultedStage()
    {
        NUnitCompat.Throws<ArgumentException>(() => CreateOutcome(
            InteractionStatus.Faulted,
            faultCode: "app-code",
            stages: Array.Empty<InteractionStageProgress>()));
    }

    [Test]
    public void BeforeAndAfterMustCoverTheSameProbes()
    {
        var before = new StateObservation(new[] { new StateProbeObservation("probe-a", "hash-1") });
        var after = new StateObservation(new[] { new StateProbeObservation("probe-b", "hash-1") });

        NUnitCompat.Throws<ArgumentException>(() => CreateOutcome(
            InteractionStatus.Succeeded,
            stages: new[]
            {
                new InteractionStageProgress("apply", 0, InteractionStageStatus.Completed),
            },
            before: before,
            after: after));
    }

    [Test]
    public void EqualityComparesEveryWireField()
    {
        var left = CreateSucceededOutcome("r-1");
        var same = CreateSucceededOutcome("r-1");
        var differentRequest = CreateSucceededOutcome("r-2");

        Assert.That(left, Is.EqualTo(same));
        Assert.That(left.GetHashCode(), Is.EqualTo(same.GetHashCode()));
        Assert.That(left, Is.Not.EqualTo(differentRequest));
    }

    internal static ProtocolInteractionOutcome CreateSucceededOutcome(string requestId)
    {
        return new ProtocolInteractionOutcome(
            1,
            requestId,
            "target-1",
            "click",
            1,
            InteractionOrigin.Agent,
            InteractionStatus.Succeeded,
            new[] { new InteractionStageProgress("apply", 0, InteractionStageStatus.Completed) },
            null,
            null,
            new StateObservation(new[] { new StateProbeObservation("probe-a", "hash-1") }),
            new StateObservation(new[] { new StateProbeObservation("probe-a", "hash-1") }));
    }

    private static ProtocolInteractionOutcome CreateOutcome(
        InteractionStatus status,
        InteractionRejectionCode? rejectionCode = null,
        string? faultCode = null,
        IEnumerable<InteractionStageProgress>? stages = null,
        StateObservation? before = null,
        StateObservation? after = null)
    {
        return new ProtocolInteractionOutcome(
            1,
            "r-1",
            "target-1",
            "click",
            1,
            InteractionOrigin.Agent,
            status,
            stages ?? Array.Empty<InteractionStageProgress>(),
            rejectionCode,
            faultCode,
            before ?? StateObservation.Empty,
            after ?? StateObservation.Empty);
    }

    private static InteractionResult CreateResult(InteractionStatus status)
    {
        var observation = new StateObservation(
            new[] { new StateProbeObservation("probe-a", "hash-1") });
        switch (status)
        {
            case InteractionStatus.Succeeded:
                return new InteractionResult(
                    1,
                    "r-1",
                    "target-1",
                    "click",
                    1,
                    InteractionOrigin.Agent,
                    InteractionStatus.Succeeded,
                    null,
                    null,
                    new StageProgress(new[]
                    {
                        new InteractionStageProgress("apply", 0, InteractionStageStatus.Completed),
                    }),
                    observation,
                    observation,
                    StateDiff.Empty);
            case InteractionStatus.Rejected:
                return new InteractionResult(
                    1,
                    "r-1",
                    "target-1",
                    "click",
                    1,
                    InteractionOrigin.Agent,
                    InteractionStatus.Rejected,
                    new RejectionInfo(
                        InteractionRejectionCode.Disabled,
                        "The target is disabled right now."),
                    null,
                    StageProgress.Empty,
                    StateObservation.Empty,
                    StateObservation.Empty,
                    StateDiff.Empty);
            case InteractionStatus.Faulted:
                return new InteractionResult(
                    1,
                    "r-1",
                    "target-1",
                    "click",
                    1,
                    InteractionOrigin.Agent,
                    InteractionStatus.Faulted,
                    null,
                    new FaultInfo(
                        "System.InvalidOperationException",
                        "Secret failure detail.",
                        "at Secret.Frame()",
                        "app-code",
                        "apply",
                        0,
                        Array.Empty<string>()),
                    new StageProgress(new[]
                    {
                        new InteractionStageProgress("apply", 0, InteractionStageStatus.Faulted),
                    }),
                    observation,
                    observation,
                    StateDiff.Empty);
            default:
                return new InteractionResult(
                    1,
                    "r-1",
                    "target-1",
                    "click",
                    1,
                    InteractionOrigin.Agent,
                    InteractionStatus.Cancelled,
                    null,
                    null,
                    StageProgress.Empty,
                    StateObservation.Empty,
                    StateObservation.Empty,
                    StateDiff.Empty);
        }
    }
}
