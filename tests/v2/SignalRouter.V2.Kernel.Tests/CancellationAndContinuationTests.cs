using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// kernel-execution.md §8–§9 — cancellation phases and continuation admission.
/// </summary>
public sealed class CancellationAndContinuationTests
{
    private static CompletionEvidence Applied() =>
        new(KernelFixture.Applied, CompletionEvidenceKind.Applied, default);

    [Test]
    public void CancellingAQueuedInteractionIsAlwaysBeforeEffect()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.Submit("r2");
        fixture.PumpUntilIdle(); // r1 active, r2 queued

        fixture.Runtime.Control.RequestCancel(new RequestId("r2"));
        fixture.Pump();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("r2"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Cancelled)));
        Assert.That(
            fixture.Executor.Requests.Select(r => r.Permit.Request),
            Is.EqualTo(new[] { new RequestId("r1") }),
            "the cancelled queued interaction never reached the executor");
    }

    [Test]
    public void CancellingBeforeThePermitTerminatesWithoutTheExecutor()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.Pump(maxTurns: 1); // admitted, not yet active

        fixture.Runtime.Control.RequestCancel(new RequestId("r1"));
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Cancelled)));
        Assert.That(fixture.Executor.Requests, Is.Empty);
    }

    [Test]
    public void CancellingAfterThePermitIsCooperative()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle(); // adopted, awaiting completion

        fixture.Runtime.Control.RequestCancel(new RequestId("r1"));
        fixture.PumpUntilIdle();
        Assert.That(fixture.Executor.CancelRequests, Has.Count.EqualTo(1));
        Assert.That(
            fixture.Query("r1").Kind, Is.EqualTo(QueryAnswerKind.Pending),
            "the effect still completes exactly once");

        fixture.Executor.CompleteLast(
            EffectResolution.Cancelled(CancellationPhase.DuringEffect, "Honored"));
        fixture.PumpUntilIdle();
        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Cancelled)));
    }

    [Test]
    public void ContinuationsAreAdmittedOnlyAfterTheParentTerminal()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        fixture.Executor.CompleteLast(
            EffectResolution.Succeeded(Applied()),
            ValueList<ContinuationRequest>.From(new[]
            {
                new ContinuationRequest(
                    KernelFixture.Invoke,
                    TargetReference.ForKey(new AuthorKey("save")),
                    InvocationPayload.Empty),
            }));
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));
        Assert.That(fixture.Query("continuation-1-0").Kind, Is.EqualTo(QueryAnswerKind.Pending));

        // The child is an ordinary admission with its own place in LogicalOrder.
        var childAdmission = fixture.Runtime.Trace.Snapshot()
            .Where(e => e.Kind == EventKind.Admitted)
            .Single(e => e.Request!.Value.Equals(new RequestId("continuation-1-0")));
        Assert.That(childAdmission.Order!.Value, Is.EqualTo(new LogicalOrder(2)));
        Assert.That(
            childAdmission.Causation,
            Is.EqualTo(EventCausation.OfRequest(new RequestId("r1"))));

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        fixture.PumpUntilIdle();
        Assert.That(
            fixture.Query("continuation-1-0"),
            Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));
    }

    [Test]
    public void AContinuationLimitViolationHonorsNothing()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        var tooMany = new ContinuationRequest[33];
        for (var i = 0; i < tooMany.Length; i++)
        {
            tooMany[i] = new ContinuationRequest(
                KernelFixture.Invoke,
                TargetReference.ForKey(new AuthorKey("save")),
                InvocationPayload.Empty);
        }

        fixture.Executor.CompleteLast(
            EffectResolution.Succeeded(Applied()),
            ValueList<ContinuationRequest>.From(tooMany));
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("continuation-1-0").Kind, Is.EqualTo(QueryAnswerKind.OutcomeUnknown));
        Assert.That(fixture.TraceKinds(), Has.Some.Contains("ContinuationLimitExceeded"));
    }
}
