using NUnit.Framework;

namespace SignalRouter.Contracts.Tests;

/// <summary>
/// guarantees.md §6.1 — the four per-interaction terminal shapes, one fixture per
/// table row, expected values transcribed literally from the spec.
/// </summary>
public sealed class EvidenceShapeTests
{
    [Test]
    public void E2PlusUnpermittedE4IsTerminalWithoutEffect()
    {
        // Row 1: "E2 + E4 with effectPermitted = false | Rejection, pre-effect
        // cancellation, or pre-effect evidence failure; zero effects".
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Terminal("r1", InteractionOutcome.Rejected)
            .Build();

        var classification = EvidenceSemantics.ClassifyInteractions(facts)[0];
        Assert.That(classification.Shape, Is.EqualTo(InteractionShape.TerminalWithoutEffect));
        Assert.That(classification.ReaderOutcome, Is.EqualTo(InteractionOutcome.Rejected));
        Assert.That(classification.EvidenceIncomplete, Is.False);
        Assert.That(classification.StrictReplayStopsBeforeEffect, Is.False);
    }

    [Test]
    public void E2E3E4IsTheNormalReplayableShape()
    {
        // Row 2: "E2 + E3 + E4 | Effect permitted, terminal known".
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Succeeded)
            .Build();

        var classification = EvidenceSemantics.ClassifyInteractions(facts)[0];
        Assert.That(classification.Shape, Is.EqualTo(InteractionShape.TerminalWithEffect));
        Assert.That(classification.ReaderOutcome, Is.EqualTo(InteractionOutcome.Succeeded));
        Assert.That(classification.EvidenceIncomplete, Is.False);
        Assert.That(classification.StrictReplayStopsBeforeEffect, Is.False);
    }

    [Test]
    public void E2OnlyIsAdmittedOnlyAndEvidenceIncomplete()
    {
        // Row 3: "E2 only | No effect was permitted (E3 is the permit) … the reader
        // treats the interaction as evidence-incomplete".
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Build();

        var classification = EvidenceSemantics.ClassifyInteractions(facts)[0];
        Assert.That(classification.Shape, Is.EqualTo(InteractionShape.AdmittedOnly));
        Assert.That(classification.ReaderOutcome, Is.EqualTo(InteractionOutcome.OutcomeUnknown));
        Assert.That(classification.EvidenceIncomplete, Is.True);
    }

    [Test]
    public void E2E3WithoutE4IsOutcomeUnknownAndStopsStrictReplayBeforeTheEffect()
    {
        // Row 4: "E2 + E3, no E4 | Effect may or may not have occurred:
        // OutcomeUnknown. Strict replay stops before permitting this interaction's effect".
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Build();

        var classification = EvidenceSemantics.ClassifyInteractions(facts)[0];
        Assert.That(classification.Shape, Is.EqualTo(InteractionShape.PermittedWithoutTerminal));
        Assert.That(classification.ReaderOutcome, Is.EqualTo(InteractionOutcome.OutcomeUnknown));
        Assert.That(classification.EvidenceIncomplete, Is.True);
        Assert.That(classification.StrictReplayStopsBeforeEffect, Is.True);
    }
}
