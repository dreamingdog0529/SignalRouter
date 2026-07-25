using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// kernel-execution.md §6, §7, §10 and guarantees.md §8 — the pump contract, the
/// two-clock model, gating with visible refusal, retention expiry, trace-gap
/// marking, and the incarnation lifecycle.
/// </summary>
public sealed class PumpGatingAndLifecycleTests
{
    private static CompletionEvidence Applied() =>
        new(KernelFixture.Applied, CompletionEvidenceKind.Applied, default);

    [Test]
    public void MaxTurnsIsHonoredAndTheReportIsTruthful()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.Submit("r2");

        var report = fixture.Pump(maxTurns: 1);
        Assert.That(report.TurnsExecuted, Is.EqualTo(1));
        Assert.That(report.WorkRemaining, Is.True);

        fixture.PumpUntilIdle();
        var idle = fixture.Pump();
        Assert.That(idle.TurnsExecuted, Is.Zero);
        Assert.That(idle.WorkRemaining, Is.False);
        Assert.That(idle.AwaitingAdapterCompletion, Is.True, "the adopted effect has not completed");
    }

    [Test]
    public void TheDeadlineIsEnforcedWithTheInjectedMonotonicClock()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");

        fixture.Clock.Value = 500;
        var report = fixture.Runtime.Pump(new PumpBudget(
            64, deadline: 500, new LogicalTime(fixture.LogicalNow), FramePhase.Update));
        Assert.That(report.TurnsExecuted, Is.Zero, "the deadline had already passed");
        Assert.That(report.WorkRemaining, Is.True);
    }

    [Test]
    public void ANonMonotonicClockFailsFast()
    {
        var fixture = new KernelFixture();
        fixture.Clock.Value = 100;
        fixture.Pump();
        fixture.Clock.Value = 50;
        AssertEx.Throws<KernelFaultException>(() => fixture.Pump());
    }

    [Test]
    public void ConcurrentPumpingIsAKernelFault()
    {
        var fixture = new KernelFixture();
        fixture.Executor.OnExecute = _ => AssertEx.Throws<KernelFaultException>(() => fixture.Pump());
        fixture.Submit("r1");
        fixture.PumpUntilIdle();
        Assert.That(fixture.Executor.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public void GatingRefusesForeignMutationsVisiblyAndNeverBlocksQueries()
    {
        var fixture = new KernelFixture();
        fixture.Runtime.Control.AcquireExclusiveControl(KernelFixture.AgentDomain);
        fixture.Pump();

        var human = fixture.Submit(
            "h1", principal: KernelFixture.Human, provenance: Provenance.HumanDirected);
        fixture.PumpUntilIdle();
        Assert.That(human.Rejected.Single().Reason, Is.EqualTo(new RejectionReason("AdmissionGated")));
        Assert.That(fixture.TraceKinds(), Has.Some.StartsWith("HumanIntentBlocked"));

        // The holder's own admissions proceed; read-only queries are never blocked.
        var owner = fixture.Submit("a1");
        fixture.PumpUntilIdle();
        Assert.That(owner.Accepted, Has.Count.EqualTo(1));
        Assert.That(fixture.Query("a1").Kind, Is.EqualTo(QueryAnswerKind.Pending));

        fixture.Runtime.Control.ReleaseExclusiveControl();
        fixture.Pump();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        fixture.PumpUntilIdle();
        var humanRetry = fixture.Submit(
            "h2", principal: KernelFixture.Human, provenance: Provenance.HumanDirected);
        fixture.PumpUntilIdle();
        Assert.That(humanRetry.Accepted, Has.Count.EqualTo(1));
    }

    [Test]
    public void TerminalsExpireOnlyByRetentionAtPumpBoundaries()
    {
        var fixture = new KernelFixture(terminalRetention: 50);
        fixture.Submit("r1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        fixture.PumpUntilIdle();
        Assert.That(fixture.Query("r1").Kind, Is.EqualTo(QueryAnswerKind.Terminal));

        fixture.LogicalNow += 100;
        fixture.Pump();
        Assert.That(fixture.Query("r1").Kind, Is.EqualTo(QueryAnswerKind.OutcomeUnknown));
    }

    [Test]
    public void QueriesArePrincipalBound()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.Pump(maxTurns: 1);

        Assert.That(fixture.Query("r1", KernelFixture.Agent).Kind, Is.EqualTo(QueryAnswerKind.Pending));
        Assert.That(
            fixture.Query("r1", KernelFixture.Human).Kind,
            Is.EqualTo(QueryAnswerKind.OutcomeUnknown),
            "an unauthorized RequestId answers exactly as an unknown id");
    }

    [Test]
    public void TraceLossIsCountedAndGapMarked()
    {
        var fixture = new KernelFixture(traceCapacity: 4);
        for (var i = 0; i < 8; i++)
        {
            fixture.PublishInventory(i);
            fixture.Pump();
        }

        Assert.That(fixture.Runtime.Trace.TotalDropped, Is.GreaterThan(0));
        Assert.That(fixture.TraceKinds(), Has.Some.StartsWith("TraceGap"));
    }

    [Test]
    public void TeardownStrandsPendingWorkAndFencesNewSubmissions()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent, timeoutAtLogicalTime: 1000, waitObserver);
        fixture.PumpUntilIdle();
        var oldPermit = fixture.Executor.Requests.Single().Permit;

        fixture.Runtime.Control.TearDownIncarnation();
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("r1").Kind, Is.EqualTo(QueryAnswerKind.OutcomeUnknown));
        Assert.That(
            waitObserver.Resolutions.Single().Resolution,
            Is.EqualTo(PredicateResolution.Cancelled));

        var late = fixture.Submit("r2");
        fixture.Pump();
        Assert.That(late.Rejected.Single().Reason, Is.EqualTo(RejectionReason.IncarnationMismatch));

        // A completion carrying the old permit is rejected and traced, never applied.
        fixture.Executor.Sink.ReportCompletion(new EffectCompletion(
            oldPermit, EffectResolution.Succeeded(Applied())));
        fixture.Pump();
        Assert.That(fixture.Query("r1").Kind, Is.EqualTo(QueryAnswerKind.OutcomeUnknown));
    }
}
