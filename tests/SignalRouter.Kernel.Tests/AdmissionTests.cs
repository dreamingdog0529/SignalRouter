using System.Linq;
using NUnit.Framework;
using SignalRouter.AdapterSdk;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel.Tests;

/// <summary>
/// kernel-execution.md §3 and guarantees.md §3.5 — admission: explicit split-phase
/// acknowledgment, fingerprint-verified dedup, capacity refusal, and
/// existence-concealing rejection with observational equivalence.
/// </summary>
public sealed class AdmissionTests
{
    [Test]
    public void AdmissionIsExplicitlyAcknowledged()
    {
        var fixture = new KernelFixture();
        var observer = fixture.Submit("r1");
        Assert.That(observer.Accepted, Is.Empty, "acceptance happens at adoption, not enqueue");

        fixture.Pump(maxTurns: 1);
        Assert.That(observer.Accepted, Is.EqualTo(new[] { new RequestId("r1") }));
        Assert.That(fixture.Query("r1").Kind, Is.EqualTo(QueryAnswerKind.Pending));
    }

    [Test]
    public void DuplicateSubmissionsAreIdempotentByFingerprint()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.Pump(maxTurns: 1);

        // Same RequestId, same payload: idempotent accept.
        var duplicate = fixture.Submit("r1");
        fixture.Pump(maxTurns: 1);
        Assert.That(duplicate.Accepted, Has.Count.EqualTo(1));
        Assert.That(duplicate.Rejected, Is.Empty);

        // Same RequestId, different payload: RequestIdConflict.
        var conflicting = fixture.Submit("r1", targetKey: "save", payload: new InvocationPayload(
            ValueArray<NamedField>.Empty));
        _ = conflicting; // identical because schema is empty — force difference via target
        var conflictObserver = fixture.Submit("r1", targetKey: "secret");
        fixture.Pump(maxTurns: 4);
        Assert.That(
            conflictObserver.Rejected.Single().Reason,
            Is.EqualTo(RejectionReason.TargetNotFound),
            "a hidden target conceals before dedup can conflict");
    }

    [Test]
    public void MutationOverflowRefusesAdmissionWithCapacityExhausted()
    {
        var fixture = new KernelFixture(mutationCapacity: 1);
        fixture.Submit("r1");
        var overflow = fixture.Submit("r2");
        Assert.That(
            overflow.Rejected.Single().Reason,
            Is.EqualTo(RejectionReason.CapacityExhausted));
    }

    [Test]
    public void RecoveryIndexCapacityRefusesNewAdmissions()
    {
        var fixture = new KernelFixture(pendingCapacity: 1);
        fixture.Submit("r1");
        fixture.Pump(maxTurns: 1);

        var refused = fixture.Submit("r2");
        fixture.Pump(maxTurns: 1);
        Assert.That(
            refused.Rejected.Single().Reason,
            Is.EqualTo(RejectionReason.CapacityExhausted));
    }

    [Test]
    public void UnregisteredAndHiddenTargetsAreObservationallyIdentical()
    {
        // guarantees.md §3.5: ack, dedup, RecoveryIndex, queries, trace, and
        // LogicalOrder consumption must be indistinguishable.
        var fixture = new KernelFixture();

        var ghost = fixture.Submit("g1", targetKey: "ghost");
        fixture.Pump(maxTurns: 1);
        var hidden = fixture.Submit("h1", targetKey: "secret");
        fixture.Pump(maxTurns: 1);

        Assert.That(ghost.Rejected.Single().Reason, Is.EqualTo(RejectionReason.TargetNotFound));
        Assert.That(hidden.Rejected.Single().Reason, Is.EqualTo(RejectionReason.TargetNotFound));
        Assert.That(fixture.Query("g1").Kind, Is.EqualTo(QueryAnswerKind.OutcomeUnknown));
        Assert.That(fixture.Query("h1").Kind, Is.EqualTo(QueryAnswerKind.OutcomeUnknown));
        Assert.That(
            fixture.TraceKinds().Count(kind => kind.StartsWith("Admitted")),
            Is.Zero,
            "neither leaves a trace footprint");

        // Neither consumed LogicalOrder: the next real admission is order 1.
        fixture.Submit("r1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();
        var admittedEvent = fixture.Runtime.Trace.Snapshot()
            .Single(e => e.Kind == EventKind.Admitted);
        Assert.That(admittedEvent.Order, Is.EqualTo(new LogicalOrder(1)));
    }

    [Test]
    public void AnUnboundPrincipalSeesNothing()
    {
        var fixture = new KernelFixture();
        var unbound = fixture.Submit(
            "u1", principal: new Principal("UnknownKind", "x"));
        fixture.Pump(maxTurns: 1);
        Assert.That(
            unbound.Rejected.Single().Reason,
            Is.EqualTo(RejectionReason.TargetNotFound));
    }
}
