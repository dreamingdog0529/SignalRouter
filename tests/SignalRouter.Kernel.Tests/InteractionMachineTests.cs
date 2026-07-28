using System.Linq;
using NUnit.Framework;
using SignalRouter.AdapterSdk;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel.Tests;

/// <summary>
/// kernel-execution.md §5 — the interaction state machine: the happy path,
/// Validating-time rejections, adoption refusal/throw semantics, reentrancy, the
/// postcondition evaluation, and the single-mutation invariant.
/// </summary>
public sealed class InteractionMachineTests
{
    private static CompletionEvidence Applied() =>
        new(KernelFixture.Applied, CompletionEvidenceKind.Applied, default);

    [Test]
    public void AHappyPathTerminatesSucceeded()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle();
        Assert.That(fixture.Executor.Requests, Has.Count.EqualTo(1));
        Assert.That(fixture.Query("r1").Kind, Is.EqualTo(QueryAnswerKind.Pending));

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        fixture.PumpUntilIdle();
        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));
    }

    [Test]
    public void ValidatingRejectsAnUnavailableCapability()
    {
        var fixture = new KernelFixture(start: false);
        fixture.Runtime.Start(fixture.Executor);
        fixture.Submit("r1");
        fixture.Pump(maxTurns: 1); // admission only

        var receipt = new RecordingRegistrationObserver();
        fixture.Runtime.Registry.SetCapabilityAvailability(
            fixture.SaveNode, KernelFixture.Invoke, available: false, receipt);
        fixture.PumpUntilIdle();

        Assert.That(receipt.Receipt!.Succeeded, Is.True);
        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Rejected)));
        Assert.That(fixture.Executor.Requests, Is.Empty, "no permit, zero effects");
    }

    [Test]
    public void ValidatingRejectsAFailedPrecondition()
    {
        // Precondition: the label must equal "Ready" — it is "Save".
        var precondition = new PredicateDefinition(ValueArray<PredicateClause>.From(new[]
        {
            new PredicateClause(new ClauseId("c0"), new ComparisonExpression(
                new FieldPath("nodes/save/attributes/label"),
                ComparisonOperator.Eq,
                PredicateOperand.Of("Ready"))),
        }));
        var fixture = new KernelFixture(invokePrecondition: precondition);
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Rejected)));
        Assert.That(fixture.Executor.Requests, Is.Empty);
    }

    [Test]
    public void AdoptionRefusalIsAFaultedTerminalNotARejection()
    {
        // ADR 0010: the permit was already granted, so a refusal is Faulted with
        // effectStarted = false — never Rejected.
        var fixture = new KernelFixture();
        fixture.Executor.Behavior = ScriptedExecutor.Mode.Refuse;
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Faulted)));
    }

    [Test]
    public void AnExecutorThrowIsPossiblyEffectedFaulted()
    {
        var fixture = new KernelFixture();
        fixture.Executor.Behavior = ScriptedExecutor.Mode.Throw;
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Faulted)));
    }

    [Test]
    public void NestedSubmissionDuringTheExecutorCallIsReentrantDispatch()
    {
        var fixture = new KernelFixture();
        RecordingObserver? nested = null;
        fixture.Executor.OnExecute = _ => nested = fixture.Submit("nested");
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        Assert.That(
            nested!.Rejected.Single().Reason,
            Is.EqualTo(RejectionReason.ReentrantDispatch));
    }

    [Test]
    public void APostconditionDeterminesTheTerminal()
    {
        // Postcondition: label == "Saved". The effect does not change it → the
        // terminal is Faulted(CompletionPostconditionNotSatisfied)
        // (verification.md §3.4).
        var postcondition = new PredicateDefinition(ValueArray<PredicateClause>.From(new[]
        {
            new PredicateClause(new ClauseId("c0"), new ComparisonExpression(
                new FieldPath("nodes/save/attributes/label"),
                ComparisonOperator.Eq,
                PredicateOperand.Of("Saved"))),
        }));
        var failing = new KernelFixture(invokePostcondition: postcondition);
        failing.Submit("r1");
        failing.PumpUntilIdle();
        failing.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        failing.PumpUntilIdle();
        Assert.That(failing.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Faulted)));

        // When the effect updates the attribute before completing, it succeeds.
        var satisfied = new KernelFixture(invokePostcondition: postcondition);
        satisfied.Executor.OnExecute = _ =>
            satisfied.Runtime.Registry.UpdateAttributes(
                satisfied.SaveNode,
                ValueArray<NodeAttribute>.From(new[]
                {
                    new NodeAttribute("label", FieldValue.Of("Saved"), Sensitivity.Standard),
                }),
                observer: null);
        satisfied.Submit("r1");
        satisfied.PumpUntilIdle();
        satisfied.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        satisfied.PumpUntilIdle();
        Assert.That(satisfied.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));
    }

    [Test]
    public void AtMostOneMutationIsActiveAndOrderIsPreserved()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.Submit("r2");
        fixture.PumpUntilIdle();

        Assert.That(fixture.Executor.Requests, Has.Count.EqualTo(1), "the lane is held");
        Assert.That(fixture.Executor.Requests[0].Permit.Request, Is.EqualTo(new RequestId("r1")));

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        fixture.PumpUntilIdle();
        Assert.That(fixture.Executor.Requests, Has.Count.EqualTo(2));
        Assert.That(fixture.Executor.Requests[1].Permit.Request, Is.EqualTo(new RequestId("r2")));
    }

    [Test]
    public void EvidenceCoordinationGatesThePermit()
    {
        // ADR 0010: the permit is minted only after PrepareEffectPermit answers
        // Ready; Pending stalls the interaction; Fault is the pre-effect evidence
        // failure.
        var coordinator = new ScriptedCoordinator { PermitAnswer = EvidenceReadiness.Pending };
        var fixture = new KernelFixture(coordinator: coordinator);
        fixture.Submit("r1");
        var report = fixture.Pump();
        Assert.That(fixture.Executor.Requests, Is.Empty, "no permit while evidence is pending");
        Assert.That(report.WorkRemaining, Is.True, "the kernel honestly awaits evidence readiness");

        coordinator.PermitAnswer = EvidenceReadiness.Ready;
        fixture.PumpUntilIdle();
        Assert.That(fixture.Executor.Requests, Has.Count.EqualTo(1));

        var faulting = new ScriptedCoordinator { PermitAnswer = EvidenceReadiness.Fault };
        var faulted = new KernelFixture(coordinator: faulting);
        faulted.Submit("r1");
        faulted.PumpUntilIdle();
        Assert.That(faulted.Executor.Requests, Is.Empty);
        Assert.That(faulted.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Faulted)));
    }
}

/// <summary>A scripted evidence coordinator for gate tests.</summary>
internal sealed class ScriptedCoordinator : IEvidenceCoordinator
{
    internal EvidenceReadiness AdmissionAnswer { get; set; } = EvidenceReadiness.Ready;

    internal EvidenceReadiness PermitAnswer { get; set; } = EvidenceReadiness.Ready;

    internal EvidenceReadiness TerminalAnswer { get; set; } = EvidenceReadiness.Ready;

    internal System.Collections.Generic.List<AdmissionEvidence> Admissions { get; } = new();

    internal System.Collections.Generic.List<TerminalEvidence> Terminals { get; } = new();

    public EvidenceReadiness PrepareAdmissionEvidence(AdmissionEvidence evidence)
    {
        if (AdmissionAnswer == EvidenceReadiness.Ready)
        {
            Admissions.Add(evidence);
        }

        return AdmissionAnswer;
    }

    public EvidenceReadiness PrepareEffectPermit(PermitEvidence evidence) => PermitAnswer;

    public EvidenceReadiness CommitTerminalEvidence(TerminalEvidence evidence)
    {
        if (TerminalAnswer == EvidenceReadiness.Ready)
        {
            Terminals.Add(evidence);
        }

        return TerminalAnswer;
    }
}

/// <summary>Records registration receipts.</summary>
internal sealed class RecordingRegistrationObserver : IRegistrationObserver
{
    internal RegistrationReceipt? Receipt { get; private set; }

    public void OnCompleted(RegistrationReceipt receipt) => Receipt = receipt;
}
