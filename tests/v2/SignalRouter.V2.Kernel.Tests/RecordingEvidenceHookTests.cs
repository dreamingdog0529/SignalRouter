using System.Collections.Generic;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// The E5/E6/E8 kernel sites (ADR 0015; guarantees.md §5.5, §5.6, §5.10):
/// external mutations coalesce into one barrier per pump run with the
/// disposition honored, waits commit paired armed/resolved cuts exactly when
/// their arm was recorded, evidence-bearing assertions evaluate against the
/// record projection with the record-exposure refusal, and Pending commits park
/// as fence members.
/// </summary>
public sealed class RecordingEvidenceHookTests
{
    private static readonly PredicateContractRef CountAtLeastOne =
        new(new PredicateContractId("countAtLeastOne"), new ContractVersion(1, 0));

    private static readonly PredicateContractRef AgentOnlyLabelExists =
        new(new PredicateContractId("agentOnlyLabelExists"), new ContractVersion(1, 0));

    private static PredicateDefinition CountAtLeastOneDefinition() => new(
        ValueArray<PredicateClause>.From(new[]
        {
            new PredicateClause(
                new ClauseId("c0"),
                new ComparisonExpression(
                    new FieldPath("sources/inventory/count"),
                    ComparisonOperator.Ge,
                    PredicateOperand.Of(1L))),
        }));

    private static PredicateDefinition LabelExistsDefinition() => new(
        ValueArray<PredicateClause>.From(new[]
        {
            new PredicateClause(
                new ClauseId("c0"),
                new ComparisonExpression(
                    new FieldPath("nodes/save/attributes/label"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of("Saved"))),
        }));

    private sealed class RecordingLifecycleObserver : IRecordingObserver
    {
        internal List<string> Answers { get; } = new();

        internal RecordingCloseReason? ClosedReason { get; private set; }

        public void OnOpened(OperationId recording) => Answers.Add("Opened");

        public void OnOpenRefused(OperationId recording, string reasonCode) =>
            Answers.Add("OpenRefused:" + reasonCode);

        public void OnClosed(OperationId recording, RecordingCloseReason reason)
        {
            ClosedReason = reason;
            Answers.Add("Closed");
        }

        public void OnFailed(OperationId recording, string reasonCode) =>
            Answers.Add("Failed:" + reasonCode);
    }

    private static (
        KernelFixture Fixture,
        RecordingStateMachineTests.ScriptedRecordingCoordinator Coordinator) Build()
    {
        var coordinator = new RecordingStateMachineTests.ScriptedRecordingCoordinator();
        var fixture = new KernelFixture(
            coordinator: coordinator, codec: new TestCanonicalStateCodec(), start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            new ViewContractRef(new ViewContractId("record-standard"), new ContractVersion(1, 0)),
            ViewFamily.Record, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Bootstrap.RegisterPredicateContract(
            CountAtLeastOne, CountAtLeastOneDefinition());
        fixture.Runtime.Bootstrap.RegisterPredicateContract(
            AgentOnlyLabelExists, new PredicateDefinition(
                ValueArray<PredicateClause>.From(new[]
                {
                    new PredicateClause(
                        new ClauseId("c0"),
                        new ExistsExpression(new FieldPath("nodes/agentonly/attributes/label"))),
                })));
        // Agent-visible but record-hidden: the record-exposure refusal's target
        // (verification.md §3.3).
        fixture.Runtime.Bootstrap.RegisterNode(new NodeRegistration(
            new AuthorKey("agentonly"),
            NodeRole.Button,
            parent: null,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("AgentOnly"), Sensitivity.Standard),
            }),
            ValueArray<CapabilityDeclaration>.Empty,
            new ExposurePolicy(ValueArray<SecurityDomainId>.From(new[]
            {
                KernelFixture.AgentDomain,
            }))));
        fixture.Runtime.Start(fixture.Executor);
        return (fixture, coordinator);
    }

    private static RecordingOpenRequest Request() => new(
        new ReplayComparisonProfileRef(
            new ReplayComparisonProfileId("strict"), new ContractVersion(1, 0)),
        new ViewContractRef(new ViewContractId("record-standard"), new ContractVersion(1, 0)),
        "root",
        new RedactionPolicyId("default-redaction"));

    private static RecordingLifecycleObserver Open(KernelFixture fixture)
    {
        var observer = new RecordingLifecycleObserver();
        fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened" }));
        return observer;
    }

    // ── E5 ───────────────────────────────────────────────────────────────────

    [Test]
    public void ExternalMutationsCoalesceIntoOneBarrierPerPumpRun()
    {
        var (fixture, coordinator) = Build();
        Open(fixture);

        fixture.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("first-effect", null, null));
        fixture.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("second-effect", null, null));
        fixture.PumpUntilIdle();

        Assert.That(coordinator.Barriers, Has.Count.EqualTo(1), "coalesced (ADR 0015)");
        Assert.That(coordinator.Barriers[0].SourceHint, Is.EqualTo("first-effect"));
        Assert.That(coordinator.Barriers[0].ContaminatedRequests.Count, Is.EqualTo(0));
    }

    [Test]
    public void AnExternallyCausedPublicationRaisesTheBarrierAndARequestCausedOneDoesNot()
    {
        var (fixture, coordinator) = Build();
        Open(fixture);

        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();
        Assert.That(coordinator.Barriers, Has.Count.EqualTo(1));

        // Request causation is controlled work: attributable, no barrier.
        fixture.PublishInventory(2, EventCausation.OfRequest(new RequestId("r-cause")));
        fixture.PumpUntilIdle();
        Assert.That(coordinator.Barriers, Has.Count.EqualTo(1));
    }

    [Test]
    public void PreOpenExternalMutationsRaiseNoBarrier()
    {
        var (fixture, coordinator) = Build();
        fixture.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("before-open", null, null));
        fixture.PumpUntilIdle();

        var observer = new RecordingLifecycleObserver();
        var recording = fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();
        fixture.Runtime.Recording.CloseRecording(recording, observer);
        fixture.PumpUntilIdle();

        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(coordinator.Barriers, Is.Empty);
    }

    [Test]
    public void TheTerminateDispositionDrivesTheCloseFence()
    {
        var (fixture, coordinator) = Build();
        coordinator.BarrierCloseRequest = IncompleteReason.ExternalMutation;
        var observer = Open(fixture);

        fixture.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("external-effect", null, null));
        fixture.PumpUntilIdle();

        Assert.That(coordinator.Barriers, Has.Count.EqualTo(1), "the barrier is recorded first");
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(observer.ClosedReason!.Value.IsCompleted, Is.False);
        Assert.That(observer.ClosedReason.Value.Reason.Value, Is.EqualTo("ExternalMutation"));
    }

    [Test]
    public void AMidEffectExternalMutationMarksTheContaminatedRequest()
    {
        var (fixture, coordinator) = Build();
        Open(fixture);

        fixture.Submit("r-1");
        fixture.PumpUntilIdle();
        fixture.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("external-effect", null, null));
        fixture.PumpUntilIdle();

        Assert.That(coordinator.Barriers, Has.Count.EqualTo(1));
        Assert.That(
            coordinator.Barriers[0].ContaminatedRequests[0], Is.EqualTo(new RequestId("r-1")),
            "the mid-effect interaction's window overlaps the interval (guarantees.md §5.5)");

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();
    }

    [Test]
    public void APendingBarrierRetriesAndCommitsOnce()
    {
        var (fixture, coordinator) = Build();
        coordinator.BarrierReadiness = EvidenceReadiness.Pending;
        Open(fixture);

        fixture.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("external-effect", null, null));
        fixture.Pump();
        fixture.Pump();
        Assert.That(coordinator.Barriers, Is.Empty, "still in flight");

        coordinator.BarrierReadiness = EvidenceReadiness.Ready;
        fixture.PumpUntilIdle();
        Assert.That(coordinator.Barriers, Has.Count.EqualTo(1));
    }

    // ── E6 ───────────────────────────────────────────────────────────────────

    [Test]
    public void AWaitArmedAndResolvedWhileRecordingCommitsPairedCuts()
    {
        var (fixture, coordinator) = Build();
        Open(fixture);

        var waitObserver = new RecordingWaitObserver();
        var operation = fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.PumpUntilIdle();

        Assert.That(coordinator.ArmedWaits, Has.Count.EqualTo(1));
        var armed = coordinator.ArmedWaits[0];
        Assert.That(armed.Operation, Is.EqualTo(operation));
        var expectedOperands = PredicateCanonicalizer.DigestOf(LabelExistsDefinition());
        Assert.That(
            armed.Operands, Is.EqualTo(expectedOperands),
            "the digest pins the registered definition (ADR 0015)");
        Assert.That(
            armed.Fingerprint,
            Is.EqualTo(PredicateCanonicalizer.FingerprintOf(
                KernelFixture.LabelExists, expectedOperands)));
        Assert.That(armed.Causality.Kind, Is.EqualTo(CausalityKind.Root));
        Assert.That(armed.ArmedSequence, Is.EqualTo(new ViewSequence(0)));

        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Saved"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();

        Assert.That(
            waitObserver.Resolutions[0].Resolution, Is.EqualTo(PredicateResolution.Satisfied));
        Assert.That(coordinator.ResolvedWaits, Has.Count.EqualTo(1));
        var resolved = coordinator.ResolvedWaits[0];
        Assert.That(resolved.Operation, Is.EqualTo(operation));
        Assert.That(resolved.Resolution, Is.EqualTo(PredicateResolution.Satisfied));
        Assert.That(
            resolved.Observation.Snapshot.IsAddressed, Is.True,
            "the witness is the addressed record-view materialization (guarantees.md §5.6)");
        Assert.That(resolved.ResolvedSequence, Is.EqualTo(new ViewSequence(1)));
    }

    [Test]
    public void AnImmediatelySatisfiedWaitCommitsBothCuts()
    {
        var (fixture, coordinator) = Build();
        Open(fixture);

        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Saved"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();

        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.PumpUntilIdle();

        Assert.That(
            waitObserver.Resolutions[0].Resolution, Is.EqualTo(PredicateResolution.Satisfied));
        Assert.That(coordinator.ArmedWaits, Has.Count.EqualTo(1));
        Assert.That(coordinator.ResolvedWaits, Has.Count.EqualTo(1));
        Assert.That(coordinator.ArmedWaits[0].ArmedSequence, Is.EqualTo(new ViewSequence(0)));
        Assert.That(coordinator.ResolvedWaits[0].ResolvedSequence, Is.EqualTo(new ViewSequence(1)));
    }

    [Test]
    public void APreOpenWaitResolvingDuringRecordingProducesNoCuts()
    {
        var (fixture, coordinator) = Build();
        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.PumpUntilIdle();

        Open(fixture);
        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Saved"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();

        Assert.That(
            waitObserver.Resolutions[0].Resolution, Is.EqualTo(PredicateResolution.Satisfied));
        Assert.That(coordinator.ArmedWaits, Is.Empty, "the arm predates E1");
        Assert.That(
            coordinator.ResolvedWaits, Is.Empty,
            "an unrecorded arm must not produce an unpaired resolution cut");
    }

    [Test]
    public void TheCloseFenceCancelsRecordedWaitsWithResolutionCuts()
    {
        var (fixture, coordinator) = Build();
        var observer = new RecordingLifecycleObserver();
        var recording = fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();

        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.PumpUntilIdle();

        fixture.Runtime.Recording.CloseRecording(recording, observer);
        fixture.PumpUntilIdle();

        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(
            waitObserver.Resolutions[0].Resolution, Is.EqualTo(PredicateResolution.Cancelled));
        Assert.That(coordinator.ResolvedWaits, Has.Count.EqualTo(1));
        Assert.That(
            coordinator.ResolvedWaits[0].Resolution, Is.EqualTo(PredicateResolution.Cancelled),
            "the close fence resolves armed waits with their E6b before E7 (guarantees.md §5.9)");
    }

    [Test]
    public void AWaitArmedDuringTheCloseFenceCarriesNoEvidence()
    {
        var (fixture, coordinator) = Build();
        var observer = new RecordingLifecycleObserver();
        var recording = fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();

        // r-1 keeps the close fence draining while the wait arms.
        fixture.Submit("r-1");
        fixture.PumpUntilIdle();
        fixture.Runtime.Recording.CloseRecording(recording, observer);
        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.Pump();

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();

        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(
            waitObserver.Resolutions[0].Resolution, Is.EqualTo(PredicateResolution.Cancelled));
        Assert.That(coordinator.ArmedWaits, Is.Empty, "E6a bears only while Active (ADR 0015)");
        Assert.That(coordinator.ResolvedWaits, Is.Empty);
    }

    // ── E8 ───────────────────────────────────────────────────────────────────

    [Test]
    public void ARecordedAssertionCommitsE8AgainstTheRecordProjection()
    {
        var (fixture, coordinator) = Build();
        fixture.PublishInventory(5);
        fixture.PumpUntilIdle();
        Open(fixture);

        var assertionObserver = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(new[] { CountAtLeastOne }),
            KernelFixture.Agent,
            assertionObserver));
        fixture.PumpUntilIdle();

        Assert.That(
            assertionObserver.Results!.Value[0].Outcome,
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(coordinator.Assertions, Has.Count.EqualTo(1));
        var evidence = coordinator.Assertions[0];
        Assert.That(evidence.Predicate, Is.EqualTo(CountAtLeastOne));
        Assert.That(
            evidence.Operands,
            Is.EqualTo(PredicateCanonicalizer.DigestOf(CountAtLeastOneDefinition())));
        Assert.That(evidence.Domain, Is.EqualTo(KernelFixture.RecordDomain));
        Assert.That(evidence.Outcome, Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(
            evidence.Snapshot.Snapshot.IsAddressed, Is.True,
            "E8 persists the addressed record-domain projection (verification.md §3.3)");
        Assert.That(evidence.Clauses.Count, Is.EqualTo(1));
    }

    [Test]
    public void ADiagnosticOnlyBatchProducesNoEvidence()
    {
        var (fixture, coordinator) = Build();
        fixture.PublishInventory(5);
        fixture.PumpUntilIdle();
        Open(fixture);

        var assertionObserver = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(new[] { CountAtLeastOne }),
            KernelFixture.Agent,
            assertionObserver,
            diagnosticOnly: true));
        fixture.PumpUntilIdle();

        Assert.That(
            assertionObserver.Results!.Value[0].Outcome,
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(coordinator.Assertions, Is.Empty, "diagnostic answers the live caller only");
    }

    [Test]
    public void ARecordHiddenPredicateIsRefusedWithADistinctError()
    {
        var (fixture, coordinator) = Build();
        Open(fixture);

        // Evidence-bearing: refused — the agent-only node is outside the record
        // view's exposure, and E8 must never bypass the record opt-in.
        var refused = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(new[] { AgentOnlyLabelExists }),
            KernelFixture.Agent,
            refused));
        fixture.PumpUntilIdle();
        Assert.That(
            refused.Results!.Value[0].Outcome,
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(
                new UnevaluableReason("RecordExposure"))));
        Assert.That(coordinator.Assertions, Is.Empty);

        // The same predicate evaluates for the live caller as a diagnostic.
        var diagnostic = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(new[] { AgentOnlyLabelExists }),
            KernelFixture.Agent,
            diagnostic,
            diagnosticOnly: true));
        fixture.PumpUntilIdle();
        Assert.That(
            diagnostic.Results!.Value[0].Outcome,
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
    }

    // ── Stream-order discipline (reader rule R1; guarantees.md §5.5) ─────────

    [Test]
    public void AResolutionNeverOutrunsAPendingArm()
    {
        var (fixture, coordinator) = Build();
        Open(fixture);

        coordinator.WaitArmedAnswer = EvidenceReadiness.Pending;
        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.Pump();

        // The wait satisfies while its E6a is still in flight: the resolution
        // queues behind it instead of committing first.
        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Saved"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.Pump();
        fixture.Pump();
        Assert.That(
            waitObserver.Resolutions[0].Resolution, Is.EqualTo(PredicateResolution.Satisfied));
        Assert.That(coordinator.ResolvedWaits, Is.Empty, "held behind the parked arm");

        coordinator.WaitArmedAnswer = EvidenceReadiness.Ready;
        fixture.PumpUntilIdle();
        Assert.That(coordinator.CommitOrder, Is.EqualTo(new[] { "E6a", "E6b" }));
    }

    [Test]
    public void AFaultedQueuedArmSuppressesItsResolutionCut()
    {
        var (fixture, coordinator) = Build();
        Open(fixture);

        coordinator.WaitArmedAnswer = EvidenceReadiness.Pending;
        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.Pump();

        coordinator.WaitArmedAnswer = EvidenceReadiness.Fault;
        fixture.Pump();
        fixture.Pump();

        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Saved"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();

        Assert.That(
            waitObserver.Resolutions[0].Resolution, Is.EqualTo(PredicateResolution.Satisfied));
        Assert.That(coordinator.ArmedWaits, Is.Empty);
        Assert.That(
            coordinator.ResolvedWaits, Is.Empty,
            "an arm that never reached the artifact must not leave an unpaired E6b (R1)");
    }

    [Test]
    public void EvidenceProducedBehindAPendingBarrierStaysBehindIt()
    {
        var (fixture, coordinator) = Build();
        Open(fixture);

        coordinator.BarrierReadiness = EvidenceReadiness.Pending;
        fixture.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("external-effect", null, null));
        fixture.Pump();

        // The arm happens after the mutation: its cut queues behind the still
        // in-flight barrier — committing it first would falsely place the arm
        // on the clean side of the interval (guarantees.md §5.5).
        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.Pump();
        fixture.Pump();
        Assert.That(coordinator.ArmedWaits, Is.Empty, "held behind the pending barrier");

        coordinator.BarrierReadiness = EvidenceReadiness.Ready;
        fixture.PumpUntilIdle();
        Assert.That(coordinator.CommitOrder, Is.EqualTo(new[] { "E5", "E6a" }));
    }

    [Test]
    public void AnAdmissionWaitsBehindAPendingBarrier()
    {
        var (fixture, coordinator) = Build();
        Open(fixture);

        coordinator.BarrierReadiness = EvidenceReadiness.Pending;
        fixture.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("external-effect", null, null));
        fixture.Pump();

        // The submission's E2 would place a post-mutation admission on the
        // clean side of the interval: the mutation lane holds until the
        // barrier is durable.
        fixture.Submit("r-1");
        fixture.Pump();
        fixture.Pump();
        Assert.That(coordinator.CommitOrder, Is.Empty, "held behind the pending barrier");

        coordinator.BarrierReadiness = EvidenceReadiness.Ready;
        fixture.PumpUntilIdle();
        Assert.That(coordinator.CommitOrder.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(coordinator.CommitOrder[0], Is.EqualTo("E5"));
        Assert.That(coordinator.CommitOrder[1], Is.EqualTo("E2"));

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();
    }

    [Test]
    public void ASatisfiedWaitTheRecordViewCannotReproduceDegradesTheRecording()
    {
        var (fixture, coordinator) = Build();
        var observer = new RecordingLifecycleObserver();
        fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened" }));

        // The predicate satisfies against the agent view but references a
        // record-hidden node: the E6b witness cannot reproduce Satisfied, so
        // the artifact degrades instead of recording an unreplayable pair.
        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            AgentOnlyLabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.PumpUntilIdle();

        Assert.That(
            waitObserver.Resolutions[0].Resolution, Is.EqualTo(PredicateResolution.Satisfied));
        Assert.That(coordinator.ArmedWaits, Has.Count.EqualTo(1));
        Assert.That(
            coordinator.ResolvedWaits, Has.Count.EqualTo(1),
            "the pair still records — R1 stays intact; honesty comes from the close reason");
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(observer.ClosedReason!.Value.IsCompleted, Is.False);
        Assert.That(observer.ClosedReason.Value.Reason.Value, Is.EqualTo("RecordExposure"));
    }

    // ── Parked commits and the fence ─────────────────────────────────────────

    [Test]
    public void TheCloseFenceWaitsForItsOwnCancellationEvidence()
    {
        var (fixture, coordinator) = Build();
        var observer = new RecordingLifecycleObserver();
        var recording = fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();

        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.PumpUntilIdle();

        coordinator.WaitResolvedAnswer = EvidenceReadiness.Pending;
        fixture.Runtime.Recording.CloseRecording(recording, observer);
        fixture.Pump();
        fixture.Pump();
        Assert.That(
            waitObserver.Resolutions[0].Resolution, Is.EqualTo(PredicateResolution.Cancelled));
        Assert.That(
            observer.Answers, Is.EqualTo(new[] { "Opened" }),
            "E7 must not commit over the cancellation's parked E6b (reader rule R1)");

        coordinator.WaitResolvedAnswer = EvidenceReadiness.Ready;
        fixture.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(coordinator.ResolvedWaits, Has.Count.EqualTo(1));
    }

    [Test]
    public void APendingE8ParksAndTheCloseFenceWaitsForIt()
    {
        var (fixture, coordinator) = Build();
        fixture.PublishInventory(5);
        fixture.PumpUntilIdle();
        var observer = new RecordingLifecycleObserver();
        var recording = fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();

        coordinator.AssertionAnswer = EvidenceReadiness.Pending;
        var assertionObserver = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(new[] { CountAtLeastOne }),
            KernelFixture.Agent,
            assertionObserver));
        fixture.Pump();
        Assert.That(
            assertionObserver.Results, Is.Not.Null,
            "the live answer never waits on evidence durability (E8 is atomic)");

        fixture.Runtime.Recording.CloseRecording(recording, observer);
        fixture.Pump();
        fixture.Pump();
        Assert.That(
            observer.Answers, Is.EqualTo(new[] { "Opened" }),
            "the fence waits for the parked E8 (ADR 0015)");

        coordinator.AssertionAnswer = EvidenceReadiness.Ready;
        fixture.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(coordinator.Assertions, Has.Count.EqualTo(1));
    }
}
