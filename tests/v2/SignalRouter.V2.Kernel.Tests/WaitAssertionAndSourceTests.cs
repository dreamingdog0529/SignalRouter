using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// verification.md §2–§3 (as amended) and observation-state.md §7 — waits
/// re-evaluate at turn boundaries on revision advance, timeouts resolve per pump
/// against logical time, assertion batches pin one read, and revision-bound
/// publications adopt atomically with causation-aware contamination.
/// </summary>
public sealed class WaitAssertionAndSourceTests
{
    [Test]
    public void AWaitResolvesWhenARevisionAdvanceSatisfiesIt()
    {
        var fixture = new KernelFixture();
        var observer = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent, timeoutAtLogicalTime: 1000, observer);
        fixture.PumpUntilIdle();
        Assert.That(observer.Resolutions, Is.Empty, "label is still 'Save'");

        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueList<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Saved"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();

        Assert.That(observer.Resolutions.Single().Resolution, Is.EqualTo(PredicateResolution.Satisfied));
    }

    [Test]
    public void AWaitTimesOutAgainstTheLogicalClock()
    {
        var fixture = new KernelFixture();
        var observer = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent, timeoutAtLogicalTime: 150, observer);
        fixture.PumpUntilIdle();
        Assert.That(observer.Resolutions, Is.Empty);

        fixture.LogicalNow = 200;
        fixture.Pump();
        Assert.That(observer.Resolutions.Single().Resolution, Is.EqualTo(PredicateResolution.TimedOut));
    }

    [Test]
    public void CancellingAWaitResolvesCancelled()
    {
        var fixture = new KernelFixture();
        var observer = new RecordingWaitObserver();
        var operation = fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent, timeoutAtLogicalTime: 1000, observer);
        fixture.PumpUntilIdle();

        fixture.Runtime.Control.CancelWait(operation);
        fixture.PumpUntilIdle();
        Assert.That(observer.Resolutions.Single().Resolution, Is.EqualTo(PredicateResolution.Cancelled));
    }

    [Test]
    public void AnUnregisteredPredicateFaultsTheWait()
    {
        var fixture = new KernelFixture();
        var observer = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            new PredicateContractRef(new PredicateContractId("nope"), new ContractVersion(1, 0)),
            KernelFixture.Agent,
            timeoutAtLogicalTime: 1000,
            observer);
        fixture.PumpUntilIdle();
        Assert.That(observer.Resolutions.Single().Resolution, Is.EqualTo(PredicateResolution.Faulted));
    }

    [Test]
    public void AnAssertionBatchAnswersPerPredicateAgainstOnePinnedRead()
    {
        var fixture = new KernelFixture();
        var observer = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueList<PredicateContractRef>.From(new[]
            {
                KernelFixture.LabelExists,
                new PredicateContractRef(new PredicateContractId("nope"), new ContractVersion(1, 0)),
            }),
            KernelFixture.Agent,
            observer));
        fixture.PumpUntilIdle();

        Assert.That(observer.Results, Has.Count.EqualTo(2));
        Assert.That(
            observer.Results![0].Outcome, Is.EqualTo(PredicateEvaluationOutcome.False),
            "label is 'Save', the predicate expects 'Saved'");
        Assert.That(
            observer.Results![1].Outcome,
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.UnsupportedContract)));
    }

    [Test]
    public void HiddenSourceFieldsNeverEvaluateFalseEndToEnd()
    {
        // The no-boolean-oracle rule through the whole stack: a predicate over the
        // sensitive source field answers Unevaluable(Redacted), never False.
        var fixture = new KernelFixture(start: false);
        var secretProbe = new PredicateContractRef(
            new PredicateContractId("secretProbe"), new ContractVersion(1, 0));
        fixture.Runtime.Bootstrap.RegisterPredicateContract(secretProbe, new PredicateDefinition(
            ValueList<PredicateClause>.From(new[]
            {
                new PredicateClause(new ClauseId("c0"), new ComparisonExpression(
                    new FieldPath("sources/inventory/secret"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of("hunter2"))),
            })));
        fixture.Runtime.Start(fixture.Executor);
        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();

        var observer = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueList<PredicateContractRef>.From(new[] { secretProbe }),
            KernelFixture.Agent,
            observer));
        fixture.PumpUntilIdle();

        Assert.That(
            observer.Results!.Single().Outcome,
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Redacted)));
    }

    [Test]
    public void PublicationOverflowAnswersThePublisherExplicitly()
    {
        var fixture = new KernelFixture(perSourcePending: 1);
        fixture.PublishInventory(1);
        var second = fixture.Runtime.Ingress.PublishSourceDocument(new SourcePublication(
            new StateSourceKey("inventory"),
            new SourceDocument(ValueList<NamedField>.From(new[]
            {
                new NamedField("count", FieldValue.Of(2L)),
            })),
            EventCausation.None));
        Assert.That(second, Is.EqualTo(PublicationAnswer.Refused));
    }

    [Test]
    public void AnExternallyCausedPublicationContaminatesTheActiveEffect()
    {
        // observation-state.md §7.2: a publication caused outside the controlled
        // work that lands during the effect window participates in contamination.
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle(); // effect adopted, awaiting completion

        fixture.PublishInventory(9, EventCausation.OfExternal("scene-loader"));
        fixture.PumpUntilIdle();

        Assert.That(fixture.TraceKinds(), Has.Some.StartsWith("ContaminationObserved"));
    }

    [Test]
    public void AnObservedExternalReportDuringTheEffectWindowContaminates()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        fixture.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("native-toggle", null, null));
        fixture.PumpUntilIdle();
        Assert.That(fixture.TraceKinds(), Has.Some.StartsWith("ContaminationObserved"));

        // Outside any effect window the report is plain diagnostics.
        var idle = new KernelFixture();
        idle.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("native-toggle", null, null));
        idle.PumpUntilIdle();
        Assert.That(idle.TraceKinds(), Has.None.StartsWith("ContaminationObserved"));
        Assert.That(idle.TraceKinds(), Has.Some.StartsWith("ObservedExternal"));
    }
}
