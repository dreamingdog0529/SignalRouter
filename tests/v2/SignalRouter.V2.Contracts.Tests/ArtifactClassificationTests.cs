using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// guarantees.md §6.3 — the artifact-level decision table, one fixture per row,
/// plus the classification precedence: reader verification overrides writer
/// self-declaration.
/// </summary>
public sealed class ArtifactClassificationTests
{
    [Test]
    public void BaseBlobOrE1NotDurableIsOpenFailed()
    {
        // Row 1: "Base blob or E1 not durable | OpenFailed".
        var baseNotDurable = new EvidenceFixture().Open().Build(baseSnapshotDurable: false);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(baseNotDurable).Outcome,
            Is.EqualTo(RecordingOutcome.OpenFailed));

        var noOpen = new EvidenceFixture().Build();
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(noOpen).Outcome,
            Is.EqualTo(RecordingOutcome.OpenFailed));
    }

    [Test]
    public void CompletedRequiresE7ClosureAndRules()
    {
        // Row 2: "E1 present, E7 with Completed, closure verifies, R1/R3 hold | Completed".
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Succeeded)
            .Close()
            .Build();

        var classification = EvidenceSemantics.ClassifyArtifact(facts);
        Assert.That(classification.Outcome, Is.EqualTo(RecordingOutcome.Completed));
        Assert.That(classification.Closure, Is.EqualTo(ClosureCheckResult.Verified));
        Assert.That(classification.Violations, Is.Empty);
    }

    [Test]
    public void DeclaredIncompleteIsHonoredWhenClosureVerifies()
    {
        // Row 3: "E1 present, E7 with Incomplete(reason) | Incomplete(reason)".
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Close(RecordingCloseReason.Incomplete(IncompleteReason.SizeLimit))
            .Build();

        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.Incomplete(IncompleteReason.SizeLimit)));
    }

    [Test]
    public void MissingE7IsInterrupted()
    {
        // Row 4: "E1 present, no E7 | Interrupted". Absence of E7 is meaningful (§5.9).
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
    public void ClosureVerificationFailureIsInterruptedNeverCompleted()
    {
        // Row 5: "E7 present but closure verification fails | Interrupted (tampered
        // or torn; never Completed)" — both recomputable variants.
        var wrongCount = new EvidenceFixture()
            .Open()
            .Close(declaredEventCountOverride: 99)
            .Build();
        var wrongCountClassification = EvidenceSemantics.ClassifyArtifact(wrongCount);
        Assert.That(wrongCountClassification.Outcome, Is.EqualTo(RecordingOutcome.Interrupted));
        Assert.That(wrongCountClassification.Closure, Is.EqualTo(ClosureCheckResult.EventCountMismatch));

        var unreachable = new EvidenceFixture()
            .Open()
            .Close(omitReachableContentId: true)
            .Build();
        var unreachableClassification = EvidenceSemantics.ClassifyArtifact(unreachable);
        Assert.That(unreachableClassification.Outcome, Is.EqualTo(RecordingOutcome.Interrupted));
        Assert.That(unreachableClassification.Closure, Is.EqualTo(ClosureCheckResult.UnreachableContentId));
    }

    [Test]
    public void ExternalIntegrityFailureIsInterrupted()
    {
        // Codec-level integrity (blob existence, digests) is a supplied fact; when
        // it fails the artifact is never Completed (§5.9, §6.3).
        var facts = new EvidenceFixture()
            .Open()
            .Close()
            .Build(externalIntegrityFailure: true);

        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));
    }

    [Test]
    public void SelfDeclaredCompletedOverViolatedRulesReadsInterrupted()
    {
        // §6.3 precedence: "An E7 declaring Completed over evidence that violates
        // R1 or R3 reads Interrupted".
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Close()
            .Build();

        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));
    }

    [Test]
    public void SelfDeclaredIncompleteWithFailingClosureReadsInterrupted()
    {
        // §6.3 precedence: "Incomplete(reason) is honored only when E7 is durable
        // and its closure material verifies".
        var facts = new EvidenceFixture()
            .Open()
            .Close(
                RecordingCloseReason.Incomplete(IncompleteReason.SinkFault),
                declaredEventCountOverride: 42)
            .Build();

        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));
    }

    [Test]
    public void CutsAfterTheCloseFenceReadInterrupted()
    {
        // E7 is the close fence (§5.9); evidence appended past it is torn.
        var facts = new EvidenceFixture()
            .Open()
            .Close()
            .Admit("late")
            .Build();

        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));
    }
}
