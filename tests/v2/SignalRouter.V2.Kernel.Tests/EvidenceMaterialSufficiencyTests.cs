using System;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// ADR 0015 / guarantees.md §6.1 — the evidence materials the kernel hands the
/// coordinator are sufficient to construct valid cuts. The Contracts cut
/// constructors are the oracle: for every reachable terminal shape, a
/// TerminalCut (and for admissions, an AdmissionCut) must be constructible from
/// the captured evidence without a throw.
/// </summary>
public sealed class EvidenceMaterialSufficiencyTests
{
    private static readonly ContentId AfterView =
        new("fnv1a64", 1, DigestValue.From(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

    private static TerminalCut CutOf(TerminalEvidence evidence) => new(
        new EvidenceSequence(1),
        evidence.Request,
        evidence.Order,
        evidence.Outcome,
        evidence.EffectPermitted,
        AfterView,
        evidence.RejectionReason,
        evidence.FaultCode,
        evidence.Completion,
        evidence.Postcondition,
        evidence.Cancellation,
        evidence.Commitments);

    private static (KernelFixture Fixture, ScriptedCoordinator Coordinator) Build(
        PredicateDefinition? precondition = null)
    {
        var coordinator = new ScriptedCoordinator();
        var fixture = new KernelFixture(
            coordinator: coordinator,
            codec: new TestCanonicalStateCodec(),
            invokePrecondition: precondition);
        return (fixture, coordinator);
    }

    [Test]
    public void TheAdmissionEvidenceCarriesTheRecordedProjection()
    {
        var (fixture, coordinator) = Build();
        fixture.Submit("r-1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();

        var admission = coordinator.Admissions[0];
        Assert.That(
            InvocationCanonicalizer.DigestOf(admission.Arguments),
            Is.EqualTo(admission.Invocation.Arguments),
            "the recorded form re-digests to the invocation's redacted digest");
        _ = new AdmissionCut(
            new EvidenceSequence(0),
            admission.Request,
            admission.Order,
            admission.Fingerprint,
            admission.Invocation,
            admission.Arguments,
            admission.ResolvedTarget,
            admission.Envelope); // ctor invariants are the oracle; a throw fails the test
    }

    [Test]
    public void ASucceededTerminalIsE4Sufficient()
    {
        var (fixture, coordinator) = Build();
        fixture.Submit("r-1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();

        var evidence = coordinator.Terminals[0];
        Assert.That(evidence.Outcome, Is.EqualTo(InteractionOutcome.Succeeded));
        Assert.That(evidence.Completion, Is.Not.Null, "the adapter's completion evidence is kept");
        _ = CutOf(evidence); // the ctor invariants are the oracle; a throw fails the test
    }

    [Test]
    public void APreconditionRejectionIsE4Sufficient()
    {
        var (fixture, coordinator) = Build(precondition: new PredicateDefinition(
            ValueArray<PredicateClause>.From(new[]
            {
                new PredicateClause(
                    new ClauseId("c0"),
                    new ComparisonExpression(
                        new FieldPath("nodes/save/attributes/label"),
                        ComparisonOperator.Eq,
                        PredicateOperand.Of("Never"))),
            })));
        fixture.Submit("r-1");
        fixture.PumpUntilIdle();

        var evidence = coordinator.Terminals[0];
        Assert.That(evidence.Outcome, Is.EqualTo(InteractionOutcome.Rejected));
        Assert.That(evidence.EffectPermitted, Is.False);
        _ = CutOf(evidence); // the ctor invariants are the oracle; a throw fails the test
    }

    [Test]
    public void AnEvidenceUnavailableFaultIsE4Sufficient()
    {
        var (fixture, coordinator) = Build();
        coordinator.PermitAnswer = EvidenceReadiness.Fault;
        fixture.Submit("r-1");
        fixture.PumpUntilIdle();

        var evidence = coordinator.Terminals[0];
        Assert.That(evidence.Outcome, Is.EqualTo(InteractionOutcome.Faulted));
        Assert.That(evidence.FaultCode, Is.EqualTo(FaultCode.EvidenceUnavailable));
        Assert.That(evidence.EffectPermitted, Is.False);
        _ = CutOf(evidence); // the ctor invariants are the oracle; a throw fails the test
    }

    [Test]
    public void AQueueTimeCancellationCarriesFullEvidence()
    {
        var (fixture, coordinator) = Build();
        fixture.Submit("r-1");
        fixture.PumpUntilIdle();
        fixture.Submit("r-2");
        fixture.Pump();
        fixture.Runtime.Control.RequestCancel(new RequestId("r-2"));
        fixture.Pump();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();

        var evidence = coordinator.Terminals[1];
        Assert.That(evidence.Request, Is.EqualTo(new RequestId("r-2")));
        Assert.That(evidence.Outcome, Is.EqualTo(InteractionOutcome.Cancelled));
        Assert.That(evidence.Cancellation, Is.Not.Null);
        Assert.That(evidence.Cancellation!.Phase, Is.EqualTo(CancellationPhase.BeforeEffect));
        Assert.That(evidence.Cancellation.Disposition, Is.EqualTo("PreEffect"));
        Assert.That(
            evidence.Cancellation.ObservedOrder,
            Is.GreaterThanOrEqualTo(evidence.Cancellation.RequestedOrder));
        _ = CutOf(evidence); // the ctor invariants are the oracle; a throw fails the test
    }

    [Test]
    public void AnAdapterCancellationDispositionRidesIntoE4()
    {
        var (fixture, coordinator) = Build();
        fixture.Submit("r-1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(
            EffectResolution.Cancelled(CancellationPhase.DuringEffect, "Cooperative"));
        fixture.PumpUntilIdle();

        var evidence = coordinator.Terminals[0];
        Assert.That(evidence.Cancellation!.Phase, Is.EqualTo(CancellationPhase.DuringEffect));
        Assert.That(evidence.Cancellation.Disposition, Is.EqualTo("Cooperative"));
        Assert.That(evidence.Cancellation.EffectStarted, Is.True);
        _ = CutOf(evidence); // the ctor invariants are the oracle; a throw fails the test
    }

    [Test]
    public void AMalformedDispositionNeverReachesTheKernel()
    {
        // The SDK boundary already enforces the code grammar — CancellationEvidence
        // construction at the terminal can therefore never throw on it.
        AssertEx.Throws<ArgumentException>(() => _ = EffectResolution.Cancelled(
            CancellationPhase.DuringEffect, "co-op!"));
    }

    [Test]
    public void CommitmentsMatchContinuationsAndLaterAdmissions()
    {
        var (fixture, coordinator) = Build();
        fixture.Submit("r-1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(
            EffectResolution.Succeeded(new CompletionEvidence(
                KernelFixture.Applied, CompletionEvidenceKind.Applied, default)),
            ValueArray<ContinuationRequest>.From(new[]
            {
                new ContinuationRequest(
                    KernelFixture.Invoke,
                    TargetReference.ForKey(new AuthorKey("save")),
                    InvocationPayload.Empty),
            }));
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();

        var parent = coordinator.Terminals[0];
        Assert.That(parent.Commitments.Count, Is.EqualTo(1));
        Assert.That(parent.Commitments[0].Ordinal, Is.Zero);

        var child = coordinator.Admissions[1];
        Assert.That(
            child.Fingerprint, Is.EqualTo(parent.Commitments[0].Fingerprint),
            "the committed fingerprint is exactly the admitted child's");
        _ = CutOf(parent); // the ctor invariants are the oracle; a throw fails the test
    }

    [Test]
    public void AnUnresolvableContinuationDropsTheWholeCommitmentList()
    {
        var (fixture, coordinator) = Build();
        fixture.Submit("r-1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(
            EffectResolution.Succeeded(new CompletionEvidence(
                KernelFixture.Applied, CompletionEvidenceKind.Applied, default)),
            ValueArray<ContinuationRequest>.From(new[]
            {
                new ContinuationRequest(
                    KernelFixture.Invoke,
                    TargetReference.ForKey(new AuthorKey("save")),
                    InvocationPayload.Empty),
                new ContinuationRequest(
                    KernelFixture.Invoke,
                    TargetReference.ForKey(new AuthorKey("missing")),
                    InvocationPayload.Empty),
            }));
        fixture.PumpUntilIdle();

        var evidence = coordinator.Terminals[0];
        Assert.That(evidence.Commitments.Count, Is.Zero, "never partially honored");
        Assert.That(evidence.Continuations.Count, Is.Zero, "admissions agree with commitments");
        Assert.That(fixture.TraceKinds(), Has.Some.Contains("ContinuationCommitmentFailed"));
        Assert.That(coordinator.Admissions, Has.Count.EqualTo(1), "no child was admitted");
    }
}
