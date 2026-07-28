using System.Linq;
using NUnit.Framework;
using SignalRouter.AdapterSdk;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel.Tests;

/// <summary>
/// Regression fixtures for the review findings: fence entailment, terminal
/// evidence retry, publication contract validation and redaction, contamination
/// causation, exposure-safe counts, expired waits, teardown fencing, and trace
/// bounds.
/// </summary>
public sealed class ReviewRegressionTests
{
    private static readonly CompletionProfileRef Acknowledged =
        new(new CompletionProfileId("AdapterAcknowledged"), new ContractVersion(1, 0));

    private static CompletionEvidence Ack() =>
        new(Acknowledged, CompletionEvidenceKind.AdapterAcknowledged, default);

    private static CompletionEvidence Applied() =>
        new(KernelFixture.Applied, CompletionEvidenceKind.Applied, default);

    [Test]
    public void AnAcknowledgedCompletionNeverImpliesTheFence()
    {
        // ADR 0010: AdapterAcknowledged exists for effects the engine cannot
        // fence; the after basis is taken only once a genuine fence arrives.
        var fixture = new KernelFixture(invokeProfile: Acknowledged);
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Ack()));
        fixture.PumpUntilIdle();
        Assert.That(
            fixture.Query("r1").Kind, Is.EqualTo(QueryAnswerKind.Pending),
            "no fence, no observation, no terminal");

        fixture.Executor.FenceLast();
        fixture.PumpUntilIdle();
        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));
    }

    [Test]
    public void PendingTerminalEvidenceIsRetriedAndGatesContinuations()
    {
        // kernel-execution.md §9: children are admitted only after the parent's
        // terminal evidence is durable.
        var coordinator = new ScriptedCoordinator { TerminalAnswer = EvidenceReadiness.Pending };
        var fixture = new KernelFixture(coordinator: coordinator);
        fixture.Submit("r1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(
            EffectResolution.Succeeded(Applied()),
            ValueArray<ContinuationRequest>.From(new[]
            {
                new ContinuationRequest(
                    KernelFixture.Invoke,
                    TargetReference.ForKey(new AuthorKey("save")),
                    InvocationPayload.Empty),
            }));
        var report = fixture.Pump();

        Assert.That(
            fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)),
            "the true terminal is committed and queryable while the recording lags");
        Assert.That(
            fixture.Query("continuation-1-0").Kind, Is.EqualTo(QueryAnswerKind.OutcomeUnknown),
            "the child is not admitted before the parent's evidence is durable");
        Assert.That(report.WorkRemaining, Is.True);

        coordinator.TerminalAnswer = EvidenceReadiness.Ready;
        fixture.PumpUntilIdle();
        Assert.That(fixture.Query("continuation-1-0").Kind, Is.EqualTo(QueryAnswerKind.Pending));
        Assert.That(coordinator.Terminals.Single().Continuations.Count, Is.EqualTo(1));
    }

    [Test]
    public void TheCoordinatorReceivesFullAdmissionMaterial()
    {
        var coordinator = new ScriptedCoordinator();
        var fixture = new KernelFixture(coordinator: coordinator);
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        var evidence = coordinator.Admissions.Single();
        Assert.That(evidence.Request, Is.EqualTo(new RequestId("r1")));
        Assert.That(evidence.Order, Is.EqualTo(new LogicalOrder(1)));
        Assert.That(evidence.ResolvedTarget.AuthorKey, Is.EqualTo(new AuthorKey("save")));
        Assert.That(evidence.Envelope.Principal, Is.EqualTo(KernelFixture.Agent));
    }

    [Test]
    public void ACompletionWithMismatchedEvidenceIsRejectedNotApplied()
    {
        // adapter-conformance.md §3: a successful completion must carry the bound
        // profile's evidence; a mismatch is a protocol violation and the correct
        // completion can still follow.
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Ack()));
        fixture.PumpUntilIdle();
        Assert.That(fixture.TraceKinds(), Has.Some.Contains("CompletionRejected"));
        Assert.That(fixture.Query("r1").Kind, Is.EqualTo(QueryAnswerKind.Pending));

        // Wrong evidence kind under the right (standard) profile: same rejection.
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(
                KernelFixture.Applied, CompletionEvidenceKind.AdapterAcknowledged, default)));
        fixture.PumpUntilIdle();
        Assert.That(
            fixture.TraceKinds().Count(k => k.Contains("CompletionRejected")), Is.EqualTo(2));

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        fixture.PumpUntilIdle();
        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));
    }

    [Test]
    public void PublicationsAreValidatedAgainstTheirContract()
    {
        var fixture = new KernelFixture();

        // Undeclared field: rejected before any swap or revision advance.
        var undeclared = fixture.Runtime.Ingress.PublishSourceDocument(new SourcePublication(
            new StateSourceKey("inventory"),
            new SourceDocument(ValueArray<NamedField>.From(new[]
            {
                new NamedField("bogus", FieldValue.Of(1L)),
            })),
            EventCausation.None));
        Assert.That(undeclared, Is.EqualTo(PublicationAnswer.Accepted), "refusal is contract-level, at adoption");
        fixture.PumpUntilIdle();
        Assert.That(fixture.TraceKinds(), Has.Some.Contains("ContractViolationOrUnknownSource"));

        // Wrong runtime type: rejected the same way.
        fixture.Runtime.Ingress.PublishSourceDocument(new SourcePublication(
            new StateSourceKey("inventory"),
            new SourceDocument(ValueArray<NamedField>.From(new[]
            {
                new NamedField("count", FieldValue.Of("not-a-number")),
            })),
            EventCausation.None));
        fixture.PumpUntilIdle();
        Assert.That(
            fixture.TraceKinds().Count(k => k.Contains("ContractViolationOrUnknownSource")),
            Is.EqualTo(2));
        Assert.That(
            fixture.TraceKinds(), Has.None.StartsWith("SourcePublicationAdopted"),
            "an invalid publication never partially swaps");
    }

    [Test]
    public void OtherRequestsPublicationsStillContaminateTheActiveEffect()
    {
        // observation-state.md §7.2: only the active request's own causation is
        // internal to the controlled work.
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        fixture.PublishInventory(5, EventCausation.OfRequest(new RequestId("someone-else")));
        fixture.PumpUntilIdle();
        Assert.That(fixture.TraceKinds(), Has.Some.StartsWith("ContaminationObserved"));

        var own = new KernelFixture();
        own.Submit("r1");
        own.PumpUntilIdle();
        own.PublishInventory(5, EventCausation.OfRequest(new RequestId("r1")));
        own.PumpUntilIdle();
        Assert.That(own.TraceKinds(), Has.None.StartsWith("ContaminationObserved"));
    }

    [Test]
    public void HiddenChildrenNeverContributeToVisibleCounts()
    {
        var fixture = new KernelFixture(start: false);
        var childCount = new PredicateContractRef(
            new PredicateContractId("childCount"), new ContractVersion(1, 0));
        fixture.Runtime.Bootstrap.RegisterPredicateContract(childCount, new PredicateDefinition(
            ValueArray<PredicateClause>.From(new[]
            {
                new PredicateClause(new ClauseId("c0"), new CountExpression(
                    new FieldPath("nodes/save/children"), ComparisonOperator.Ge, 1)),
            })));

        // One hidden child, one visible child under the visible parent.
        fixture.Runtime.Bootstrap.RegisterNode(new NodeRegistration(
            new AuthorKey("hidden-child"), NodeRole.Container, new AuthorKey("save"),
            ValueArray<NodeAttribute>.Empty, ValueArray<CapabilityDeclaration>.Empty,
            ExposurePolicy.Hidden));
        fixture.Runtime.Start(fixture.Executor);

        var observer = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(new[] { childCount }),
            KernelFixture.Agent, observer));
        fixture.PumpUntilIdle();
        Assert.That(
            observer.Results!.Value.Single().Outcome, Is.EqualTo(PredicateEvaluationOutcome.False),
            "the hidden child is invisible to the count");
    }

    [Test]
    public void AWaitArmedPastItsDeadlineResolvesImmediately()
    {
        var fixture = new KernelFixture();
        var observer = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: fixture.LogicalNow - 1, observer);
        fixture.PumpUntilIdle();
        Assert.That(observer.Resolutions.Single().Resolution, Is.EqualTo(PredicateResolution.TimedOut));
    }

    [Test]
    public void LateControlOperationsAfterTeardownGetExplicitAnswers()
    {
        var fixture = new KernelFixture();
        fixture.Runtime.Control.TearDownIncarnation();
        fixture.PumpUntilIdle();

        var registration = new RecordingRegistrationObserver();
        fixture.Runtime.Registry.Register(new NodeRegistration(
            new AuthorKey("late"), NodeRole.Button, null,
            ValueArray<NodeAttribute>.Empty, ValueArray<CapabilityDeclaration>.Empty,
            ExposurePolicy.Hidden), registration);
        var wait = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent, timeoutAtLogicalTime: 1000, wait);
        fixture.PumpUntilIdle();

        Assert.That(registration.Receipt!.Succeeded, Is.False);
        Assert.That(registration.Receipt!.FailureCode, Is.EqualTo("TornDown"));
        Assert.That(wait.Resolutions.Single().Resolution, Is.EqualTo(PredicateResolution.Faulted));
    }

    [Test]
    public void TheTraceRingNeverExceedsItsConfiguredCapacity()
    {
        var fixture = new KernelFixture(traceCapacity: 4);
        for (var i = 0; i < 12; i++)
        {
            fixture.PublishInventory(i);
            fixture.Pump();
        }

        Assert.That(fixture.Runtime.Trace.Snapshot().Count, Is.LessThanOrEqualTo(4));
        Assert.That(fixture.Runtime.Trace.TotalDropped, Is.GreaterThan(0));
    }
}
