using System.Linq;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// guarantees.md §5.5–§5.7 — the strict-replay stop candidates derivable from
/// evidence alone. Actual stop decisions and Incomparable reporting belong to the
/// replay layer; temporal-predicate hazards need contract knowledge and are out of
/// scope here (honesty ledger).
/// </summary>
public sealed class StaticReplayHazardTests
{
    [Test]
    public void ContaminatedEffectStopsBeforeTheEffectNotAtE5Position()
    {
        // §5.5: "stop before permitting the effect of the first contaminated
        // interaction — not upon reaching E5's position in the stream".
        var fixture = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Succeeded);
        var e5Position = fixture.LastSequence.Value + 1;
        var facts = fixture
            .Barrier(2, e5Position, "r1")
            .Build();

        var hazard = EvidenceSemantics.ScanStaticReplayHazards(facts)
            .Single(h => h.Kind == StaticReplayHazardKind.ContaminatedEffect);
        Assert.That(hazard.Position, Is.EqualTo(fixture.SequenceOfPermit("r1")));
        Assert.That(hazard.Position.Value, Is.LessThan(e5Position));
        Assert.That(hazard.Reason, Is.EqualTo(IncomparableReason.Contamination));

        var classification = EvidenceSemantics.ClassifyInteractions(facts)[0];
        Assert.That(classification.Contaminated, Is.True);
        Assert.That(classification.StrictReplayStopsBeforeEffect, Is.True);
    }

    [Test]
    public void NonSatisfiedPredicateResolutionsStopBeforeTheWait()
    {
        // §5.6: TimedOut/Cancelled/Faulted/Unknown resolutions stop strict replay
        // before execution of the wait; Faulted reports Incomparable(PredicateFault).
        var timedOut = new EvidenceFixture()
            .Open().Arm("op1").Resolve("op1", PredicateResolution.TimedOut)
            .Build();
        var timedOutHazard = EvidenceSemantics.ScanStaticReplayHazards(timedOut).Single();
        Assert.That(timedOutHazard.Kind, Is.EqualTo(StaticReplayHazardKind.PredicateResolutionNotSatisfied));
        Assert.That(timedOutHazard.Reason, Is.Null);

        var faultedFixture = new EvidenceFixture()
            .Open().Arm("op1");
        var faulted = faultedFixture
            .Resolve("op1", PredicateResolution.Faulted)
            .Build();
        var faultedHazard = EvidenceSemantics.ScanStaticReplayHazards(faulted).Single();
        Assert.That(faultedHazard.Reason, Is.EqualTo(IncomparableReason.PredicateFault));
        Assert.That(faultedHazard.Position, Is.EqualTo(faultedFixture.SequenceOfArmed("op1")));

        var satisfied = new EvidenceFixture()
            .Open().Arm("op1").Resolve("op1", PredicateResolution.Satisfied)
            .Build();
        Assert.That(EvidenceSemantics.ScanStaticReplayHazards(satisfied), Is.Empty);
    }

    [Test]
    public void DuringEffectCancellationIsACancellationTimingHazard()
    {
        // §5.7: "DuringEffect cancellations stop strict replay as Incomparable(CancellationTiming)".
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Cancelled, cancellationPhase: CancellationPhase.DuringEffect)
            .Build();

        var hazard = EvidenceSemantics.ScanStaticReplayHazards(facts).Single();
        Assert.That(hazard.Kind, Is.EqualTo(StaticReplayHazardKind.DuringEffectCancellation));
        Assert.That(hazard.Reason, Is.EqualTo(IncomparableReason.CancellationTiming));
    }

    [Test]
    public void BeforeEffectCancellationIsReplayableWithoutAHazard()
    {
        // §5.7: BeforeEffect cancellations replay deterministically with a synthetic
        // pre-cancelled token — no stop candidate.
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Terminal("r1", InteractionOutcome.Cancelled, cancellationPhase: CancellationPhase.BeforeEffect)
            .Build();

        Assert.That(EvidenceSemantics.ScanStaticReplayHazards(facts), Is.Empty);
    }

    [Test]
    public void AfterEffectSplitsOnItsTerminal()
    {
        // §5.7: AfterEffect evidence on a non-Cancelled terminal replays as a normal
        // terminal (no hazard); a Cancelled terminal with phase AfterEffect stops as
        // Incomparable(CancellationTiming).
        var lateCancelIgnored = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Succeeded, cancellationPhase: CancellationPhase.AfterEffect)
            .Build();
        Assert.That(EvidenceSemantics.ScanStaticReplayHazards(lateCancelIgnored), Is.Empty);

        var cancelledAfterEffect = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Cancelled, cancellationPhase: CancellationPhase.AfterEffect)
            .Build();
        var hazard = EvidenceSemantics.ScanStaticReplayHazards(cancelledAfterEffect).Single();
        Assert.That(hazard.Kind, Is.EqualTo(StaticReplayHazardKind.CancelledAfterEffectTerminal));
        Assert.That(hazard.Reason, Is.EqualTo(IncomparableReason.CancellationTiming));
    }

    [Test]
    public void HazardsAreOrderedByStreamPosition()
    {
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Arm("op1")
            .Resolve("op1", PredicateResolution.TimedOut)
            .Build();

        var hazards = EvidenceSemantics.ScanStaticReplayHazards(facts);
        Assert.That(hazards.Count, Is.EqualTo(2));
        Assert.That(hazards[0].Position.Value, Is.LessThan(hazards[1].Position.Value));
        Assert.That(hazards[0].Kind, Is.EqualTo(StaticReplayHazardKind.OutcomeUnknownShape));
    }
}
