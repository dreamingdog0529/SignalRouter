using System.Linq;
using NUnit.Framework;

namespace SignalRouter.Contracts.Tests;

/// <summary>
/// guarantees.md §7 — the failure matrix, transcribed cell by cell. Interaction
/// outcome and recording outcome are separate columns; a recording failure never
/// rewrites an interaction's real result.
///
/// Honesty ledger — what pure evidence semantics can and cannot verify, per row
/// (rows quoted verbatim from §7):
///
/// | Row | Verified here | Deferred to |
/// |---|---|---|
/// | "Before base blob / E1 durable"                    | recording column (OpenFailed)                          | blob durability facts: Recording/StateStore |
/// | "Before E2 (admission refused or crash pre-admission)" | nothing — absence of evidence cannot distinguish an attempt from no attempt | Kernel admission tests |
/// | "After E2, before E3"                              | shape + Completed barred                               | Kernel zero-effect/dedup behavior |
/// | "After E3, before E4"                              | OutcomeUnknown shape + replay stop candidate           | actual stop: Replay layer |
/// | "Effect done, E4 append fails (sink fault)"        | recording column (Incomplete(SinkFault)/Interrupted)   | RecoveryIndex preservation: Kernel |
/// | "After E4, before E7"                              | terminal known from E4 + Interrupted                   | queryability: Kernel |
/// | "E7 write fault"                                   | reader infers Interrupted                              | torn-write detection: Codec.Recording |
/// | "Pre-effect cancel"                                | shape/phase consistency, no stop candidate             | synthetic-token execution: Replay layer |
/// | "Mid-effect cancel"                                | stop candidate Incomparable(CancellationTiming)        | actual Incomparable answer: Replay layer |
/// | "External mutation during active interaction"      | contaminated marking + pre-effect stop candidate       | detection/append: Adapter + Recording |
/// | "Runtime crash"                                    | recording column (Interrupted unless E7 durable)       | retention/incarnation lifecycle: Kernel |
/// | "Gateway crash / disconnect"                       | nothing                                                | ProtocolSession/Gateway |
/// | "Incarnation change"                               | recording column (Incomplete(IncarnationChanged))      | lifecycle/query: Kernel |
/// | "Revision-bound source publish refused (mailbox overflow)" | nothing — no cut exists for a refused publish  | Kernel/Observation |
/// | "Assertion evaluated, crash before E8 append"      | nothing — E8 absence cannot distinguish a crashed append from no assertion (E8 is atomic, R5) | Recording/Kernel fail-point tests |
/// | "Case seal fails (conditions unmet)"               | nothing                                                | Verification module |
/// | "Capacity exhaustion"                              | reason codes exist on both columns                     | refusal behavior: Kernel; rollover policy: Recording |
/// </summary>
public sealed class FailureMatrixTests
{
    [Test]
    public void BeforeBaseBlobOrE1Durable_RecordingAnswersOpenFailed()
    {
        // Row: "Before base blob / E1 durable" — "OpenFailed; orphan blobs GC-eligible".
        var facts = new EvidenceFixture().Open().Build(baseSnapshotDurable: false);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.OpenFailed));
    }

    [Test]
    public void AfterE2BeforeE3_NoEffectOccurredAndArtifactIsIncompleteOrInterrupted()
    {
        // Row: "After E2, before E3" — "no effect occurred (E3 is the permit)" /
        // "artifact Incomplete/Interrupted".
        var interrupted = new EvidenceFixture().Open().Admit("r1").Build();
        var classification = EvidenceSemantics.ClassifyInteractions(interrupted)[0];
        Assert.That(classification.Shape, Is.EqualTo(InteractionShape.AdmittedOnly));
        Assert.That(classification.ReaderOutcome, Is.EqualTo(InteractionOutcome.OutcomeUnknown));
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(interrupted).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));

        var selfDeclaredComplete = new EvidenceFixture().Open().Admit("r1").Close().Build();
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(selfDeclaredComplete).Outcome,
            Is.Not.EqualTo(RecordingOutcome.Completed));
    }

    [Test]
    public void AfterE3BeforeE4_OutcomeUnknownAndStrictReplayStopsBeforeThisEffect()
    {
        // Row: "After E3, before E4" — "OutcomeUnknown" / "strict replay stops
        // before this effect".
        var facts = new EvidenceFixture().Open().Admit("r1").Permit("r1").Build();

        var classification = EvidenceSemantics.ClassifyInteractions(facts)[0];
        Assert.That(classification.ReaderOutcome, Is.EqualTo(InteractionOutcome.OutcomeUnknown));

        var hazard = EvidenceSemantics.ScanStaticReplayHazards(facts).Single();
        Assert.That(hazard.Kind, Is.EqualTo(StaticReplayHazardKind.OutcomeUnknownShape));
        Assert.That(hazard.Position, Is.EqualTo(new EvidenceFixture().Open().Admit("r1").Permit("r1").SequenceOfPermit("r1")));
    }

    [Test]
    public void EffectDoneE4AppendFails_RecordingAloneFails()
    {
        // Row: "Effect done, E4 append fails (sink fault)" — recording column:
        // "artifact Incomplete(SinkFault) if writable, else Interrupted". The
        // interaction column (true terminal preserved via RecoveryIndex) is a
        // Kernel-layer behavior — see ledger.
        var writable = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Close(RecordingCloseReason.Incomplete(IncompleteReason.SinkFault))
            .Build();
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(writable).Outcome,
            Is.EqualTo(RecordingOutcome.Incomplete(IncompleteReason.SinkFault)));

        var unwritable = new EvidenceFixture().Open().Admit("r1").Permit("r1").Build();
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(unwritable).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));
    }

    [Test]
    public void AfterE4BeforeE7_TerminalKnownButArtifactInterrupted()
    {
        // Row: "After E4, before E7" — "terminal known and queryable" / "artifact
        // Interrupted (unclosed)". The recording failure never rewrites the
        // interaction's real result.
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Succeeded)
            .Build();

        var classification = EvidenceSemantics.ClassifyInteractions(facts)[0];
        Assert.That(classification.ReaderOutcome, Is.EqualTo(InteractionOutcome.Succeeded));
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));
    }

    [Test]
    public void E7WriteFault_ReaderInfersInterrupted()
    {
        // Row: "E7 write fault" — "not Completed; reader infers Interrupted".
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Succeeded)
            .Build();

        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));
    }

    [Test]
    public void PreEffectCancel_IsReplayableWithPhaseBeforeEffect()
    {
        // Row: "Pre-effect cancel" — "Cancelled with phase = BeforeEffect" /
        // "replayable with synthetic cancelled token" (no stop candidate).
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Terminal("r1", InteractionOutcome.Cancelled, cancellationPhase: CancellationPhase.BeforeEffect)
            .Close()
            .Build();

        var classification = EvidenceSemantics.ClassifyInteractions(facts)[0];
        Assert.That(classification.Shape, Is.EqualTo(InteractionShape.TerminalWithoutEffect));
        Assert.That(classification.ReaderOutcome, Is.EqualTo(InteractionOutcome.Cancelled));
        Assert.That(EvidenceSemantics.ScanStaticReplayHazards(facts), Is.Empty);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.Completed));
    }

    [Test]
    public void MidEffectCancel_IsIncomparableCancellationTimingAtThatEntry()
    {
        // Row: "Mid-effect cancel" — "terminal may be known (Cancelled, phase =
        // DuringEffect)" / "Incomparable(CancellationTiming) at that entry".
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Cancelled, cancellationPhase: CancellationPhase.DuringEffect)
            .Build();

        var classification = EvidenceSemantics.ClassifyInteractions(facts)[0];
        Assert.That(classification.ReaderOutcome, Is.EqualTo(InteractionOutcome.Cancelled));

        var hazard = EvidenceSemantics.ScanStaticReplayHazards(facts).Single();
        Assert.That(hazard.Reason, Is.EqualTo(IncomparableReason.CancellationTiming));
    }

    [Test]
    public void ExternalMutationDuringActiveInteraction_MarksContaminationAndStopsBeforeTheEffect()
    {
        // Row: "External mutation during active interaction" — "interaction marked
        // contaminated; outcome per its own evidence" / "E5 interval; strict replay
        // stops before the contaminated effect".
        var fixture = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Succeeded);
        var facts = fixture.Barrier(2, fixture.LastSequence.Value, "r1").Build();

        var classification = EvidenceSemantics.ClassifyInteractions(facts)[0];
        Assert.That(classification.Contaminated, Is.True);
        Assert.That(
            classification.ReaderOutcome,
            Is.EqualTo(InteractionOutcome.Succeeded),
            "outcome stays per the interaction's own evidence");
        Assert.That(classification.StrictReplayStopsBeforeEffect, Is.True);

        // Policy variant: the barrier terminates the artifact Incomplete(ExternalMutation).
        var terminatedFixture = new EvidenceFixture()
            .Open()
            .Admit("r2")
            .Permit("r2")
            .Terminal("r2", InteractionOutcome.Succeeded);
        var terminated = terminatedFixture
            .Barrier(2, terminatedFixture.LastSequence.Value, "r2")
            .Close(RecordingCloseReason.Incomplete(IncompleteReason.ExternalMutation))
            .Build();
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(terminated).Outcome,
            Is.EqualTo(RecordingOutcome.Incomplete(IncompleteReason.ExternalMutation)));
    }

    [Test]
    public void RuntimeCrash_ArtifactInterruptedUnlessE7WasDurable()
    {
        // Row: "Runtime crash" — recording column: "artifact Interrupted unless E7
        // was durable". Interaction retention/queryability is Kernel behavior.
        var withoutClose = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Succeeded)
            .Build();
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(withoutClose).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));

        var withClose = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Succeeded)
            .Close()
            .Build();
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(withClose).Outcome,
            Is.EqualTo(RecordingOutcome.Completed));
    }

    [Test]
    public void IncarnationChange_ClosesIncompleteIncarnationChangedIfWritableElseInterrupted()
    {
        // Row: "Incarnation change" — recording column: "recording bound to old
        // incarnation closes Incomplete(IncarnationChanged) if writable, else
        // Interrupted".
        var writable = new EvidenceFixture()
            .Open()
            .Close(RecordingCloseReason.Incomplete(IncompleteReason.IncarnationChanged))
            .Build();
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(writable).Outcome,
            Is.EqualTo(RecordingOutcome.Incomplete(IncompleteReason.IncarnationChanged)));

        var unwritable = new EvidenceFixture().Open().Build();
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(unwritable).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));
    }

    [Test]
    public void CapacityExhaustion_BothColumnsCarryTheirReservedCodes()
    {
        // Row: "Capacity exhaustion" — "new admissions refused
        // (Rejected(CapacityExhausted))" / "per recording policy:
        // Incomplete(SizeLimit), chunk rollover, or normal close". The refusal
        // behavior itself is Kernel-layer; the codes and the artifact answer are
        // contract-level.
        Assert.That(RejectionReason.CapacityExhausted.IsCanonical, Is.True);

        var sizeLimited = new EvidenceFixture()
            .Open()
            .Close(RecordingCloseReason.Incomplete(IncompleteReason.SizeLimit))
            .Build();
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(sizeLimited).Outcome,
            Is.EqualTo(RecordingOutcome.Incomplete(IncompleteReason.SizeLimit)));
    }
}
