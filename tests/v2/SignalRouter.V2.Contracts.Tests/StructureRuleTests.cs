using System;
using System.Linq;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// guarantees.md §6.2 — rules R1–R5. R2 and R4 are structural properties of the cut
/// kind set, pinned as declaration tests (their behavioral half — that replay really
/// compares at every cut — is a replay-layer integration concern, out of scope
/// here). Violations are results, never exceptions.
/// </summary>
public sealed class StructureRuleTests
{
    [Test]
    public void R1IncompleteShapeInsideACompletedCloseBarsCompleted()
    {
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Close()
            .Build();

        var violations = EvidenceSemantics.CheckStructure(facts);
        Assert.That(violations.Any(v => v.Rule == ShapeRule.R1), Is.True);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.Not.EqualTo(RecordingOutcome.Completed));
    }

    [Test]
    public void R1UnmatchedPredicateArmedForcesIncompleteOrInterrupted()
    {
        // §6.2: "every PredicateArmed MUST have a matching PredicateResolved".
        var facts = new EvidenceFixture()
            .Open()
            .Arm("op1")
            .Close()
            .Build();

        var violations = EvidenceSemantics.CheckStructure(facts);
        Assert.That(
            violations.Any(v => v.Rule == ShapeRule.R1 && Equals(v.Operation, TestData.Operation("op1"))),
            Is.True);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.Interrupted));
    }

    [Test]
    public void R1FlagsDuplicateOrphanAndOutOfOrderCuts()
    {
        var duplicatePermit = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Permit("r1")
            .Permit("r1")
            .Terminal("r1", InteractionOutcome.Succeeded)
            .Build();
        Assert.That(
            EvidenceSemantics.CheckStructure(duplicatePermit)
                .Any(v => v.Rule == ShapeRule.R1 && v.Description.Contains("Duplicate")),
            Is.True);

        var orphanTerminal = new EvidenceFixture()
            .Open()
            .Terminal("ghost", InteractionOutcome.Rejected)
            .Build();
        Assert.That(
            EvidenceSemantics.CheckStructure(orphanTerminal)
                .Any(v => v.Rule == ShapeRule.R1 && v.Description.Contains("without a preceding E2")),
            Is.True);
        Assert.That(
            EvidenceSemantics.ClassifyInteractions(orphanTerminal),
            Is.Empty,
            "an orphan cut is a violation, never an admitted interaction");

        var outOfOrder = new EvidenceFixture()
            .Open()
            .Append(sequence => new EffectPermit(
                sequence,
                TestData.Request("r1"),
                new LogicalOrder(1),
                new SourceRevision(1),
                TestData.Content("before-r1"),
                reusedCheckpointBlob: false))
            .Admit("r1")
            .Build();
        Assert.That(
            EvidenceSemantics.CheckStructure(outOfOrder)
                .Any(v => v.Rule == ShapeRule.R1 && v.Description.Contains("out of order")),
            Is.True);
    }

    [Test]
    public void R1FlagsATerminalWhosePermitFlagDisagreesWithE3Presence()
    {
        var facts = new EvidenceFixture()
            .Open()
            .Admit("r1")
            .Append(sequence => new TerminalCut(
                sequence,
                TestData.Request("r1"),
                new LogicalOrder(1),
                InteractionOutcome.Succeeded,
                effectPermitted: true,
                TestData.Content("after-r1"),
                completionEvidence: TestData.Completion()))
            .Build();

        Assert.That(
            EvidenceSemantics.CheckStructure(facts)
                .Any(v => v.Rule == ShapeRule.R1 && v.Description.Contains("permit flag")),
            Is.True);
    }

    [Test]
    public void R1FlagsLogicalOrderInversionsAndMismatches()
    {
        // recording-replay.md §2: E2 cuts appear in LogicalOrder (admission order is
        // append order), and E3/E4 carry their interaction's LogicalOrder.
        var invertedAdmissions = new EvidenceFixture()
            .Open()
            .Admit("a", order: 5)
            .Admit("b", order: 3)
            .Build();
        Assert.That(
            EvidenceSemantics.CheckStructure(invertedAdmissions)
                .Any(v => v.Rule == ShapeRule.R1 && v.Description.Contains("E2 cuts out of LogicalOrder")),
            Is.True);

        var mismatchedPermit = new EvidenceFixture()
            .Open()
            .Admit("a", order: 1)
            .Append(sequence => new EffectPermit(
                sequence,
                TestData.Request("a"),
                new LogicalOrder(9),
                new SourceRevision(1),
                TestData.Content("before-a"),
                reusedCheckpointBlob: false))
            .Build();
        Assert.That(
            EvidenceSemantics.CheckStructure(mismatchedPermit)
                .Any(v => v.Rule == ShapeRule.R1 && v.Description.Contains("E3 LogicalOrder disagrees")),
            Is.True);
    }

    [Test]
    public void R1FlagsAResolutionPrecedingItsArming()
    {
        // guarantees.md §5.6: E6 is an armed-then-resolved pair.
        var facts = new EvidenceFixture()
            .Open()
            .Resolve("op1", PredicateResolution.Satisfied)
            .Arm("op1")
            .Build();

        Assert.That(
            EvidenceSemantics.CheckStructure(facts)
                .Any(v => v.Rule == ShapeRule.R1 && v.Description.Contains("precedes its PredicateArmed")),
            Is.True);
    }

    [Test]
    public void R2NoControlLaneCutKindExists()
    {
        // §6.2 R2: control-lane operations (cancel requests, queries) are not
        // ReplayEvidence — the kind set has no member for them; cancellation
        // surfaces only through CancellationEvidence embedded in E4.
        Assert.That(Enum.GetNames<EvidenceCutKind>(), Is.EqualTo(new[]
        {
            "RecordingOpened", "AdmissionCut", "EffectPermit", "TerminalCut",
            "ExternalMutationBarrier", "PredicateArmed", "PredicateResolved",
            "RecordingClosed", "AssertionEvaluated",
        }));
    }

    [Test]
    public void R3ContinuationCommitmentsResolveToChildChains()
    {
        var commitment = new ContinuationCommitment(0, TestData.Fingerprint("child"));
        var link = new ContinuationLink(TestData.Request("parent"), 0, TestData.Fingerprint("child"));

        var resolved = new EvidenceFixture()
            .Open()
            .Admit("parent")
            .Permit("parent")
            .Terminal("parent", InteractionOutcome.Succeeded,
                continuations: ValueList<ContinuationCommitment>.From(new[] { commitment }))
            .Admit("child", Causality.OfContinuation(link))
            .Permit("child")
            .Terminal("child", InteractionOutcome.Succeeded)
            .Close()
            .Build();
        Assert.That(
            EvidenceSemantics.CheckStructure(resolved).Any(v => v.Rule == ShapeRule.R3),
            Is.False);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(resolved).Outcome,
            Is.EqualTo(RecordingOutcome.Completed));
    }

    [Test]
    public void R3UnresolvedCommitmentsBlockCompleted()
    {
        // §5.8: "An artifact with unresolved commitments … MUST NOT be closed as Completed".
        var commitment = new ContinuationCommitment(0, TestData.Fingerprint("child"));
        var facts = new EvidenceFixture()
            .Open()
            .Admit("parent")
            .Permit("parent")
            .Terminal("parent", InteractionOutcome.Succeeded,
                continuations: ValueList<ContinuationCommitment>.From(new[] { commitment }))
            .Close()
            .Build();

        Assert.That(
            EvidenceSemantics.CheckStructure(facts)
                .Any(v => v.Rule == ShapeRule.R3 && v.Description.Contains("Unresolved")),
            Is.True);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.Not.EqualTo(RecordingOutcome.Completed));
    }

    [Test]
    public void R3FlagsAChildAdmittedBeforeItsParentTerminal()
    {
        // guarantees.md §5.8: "A child is admitted only after its parent's E4 is durable".
        var commitment = new ContinuationCommitment(0, TestData.Fingerprint("child"));
        var link = new ContinuationLink(TestData.Request("parent"), 0, TestData.Fingerprint("child"));
        var facts = new EvidenceFixture()
            .Open()
            .Admit("parent")
            .Permit("parent")
            .Admit("child", Causality.OfContinuation(link))
            .Permit("child")
            .Terminal("child", InteractionOutcome.Succeeded)
            .Terminal("parent", InteractionOutcome.Succeeded,
                continuations: ValueList<ContinuationCommitment>.From(new[] { commitment }))
            .Build();

        Assert.That(
            EvidenceSemantics.CheckStructure(facts)
                .Any(v => v.Rule == ShapeRule.R3 && v.Description.Contains("before its parent terminal")),
            Is.True);
    }

    [Test]
    public void R3FlagsFingerprintMismatchAndOrphanChildren()
    {
        var commitment = new ContinuationCommitment(0, TestData.Fingerprint("expected"));
        var wrongLink = new ContinuationLink(TestData.Request("parent"), 0, TestData.Fingerprint("actual"));
        var mismatch = new EvidenceFixture()
            .Open()
            .Admit("parent")
            .Permit("parent")
            .Terminal("parent", InteractionOutcome.Succeeded,
                continuations: ValueList<ContinuationCommitment>.From(new[] { commitment }))
            .Admit("child", Causality.OfContinuation(wrongLink))
            .Permit("child")
            .Terminal("child", InteractionOutcome.Succeeded)
            .Build();
        Assert.That(
            EvidenceSemantics.CheckStructure(mismatch)
                .Any(v => v.Rule == ShapeRule.R3 && v.Description.Contains("fingerprint")),
            Is.True);

        var orphanLink = new ContinuationLink(TestData.Request("nobody"), 3, TestData.Fingerprint("x"));
        var orphan = new EvidenceFixture()
            .Open()
            .Admit("child", Causality.OfContinuation(orphanLink))
            .Permit("child")
            .Terminal("child", InteractionOutcome.Succeeded)
            .Build();
        Assert.That(
            EvidenceSemantics.CheckStructure(orphan)
                .Any(v => v.Rule == ShapeRule.R3 && v.Description.Contains("without a matching commitment")),
            Is.True);
    }

    [Test]
    public void R4EveryCutKindIsComparisonBearing()
    {
        // §6.2 R4: "strict replay compares evidence from all cuts" — the declared
        // comparison surface covers every kind. That replay actually compares at
        // each is a replay-layer integration test (honesty ledger).
        Assert.That(
            EvidenceSemantics.ComparisonBearingCutKinds,
            Is.EquivalentTo(Enum.GetValues<EvidenceCutKind>()));
    }

    [Test]
    public void R5AssertionsAreClosureFree()
    {
        // §6.2 R5: an artifact may close Completed with any number of E8 cuts and
        // any mix of E8 outcomes — including False and Unevaluable.
        var facts = new EvidenceFixture()
            .Open()
            .Assertion(PredicateEvaluationOutcome.Satisfied, "a1")
            .Assertion(PredicateEvaluationOutcome.False, "a2")
            .Assertion(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Redacted), "a3")
            .Close()
            .Build();

        Assert.That(EvidenceSemantics.CheckStructure(facts), Is.Empty);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(facts).Outcome,
            Is.EqualTo(RecordingOutcome.Completed));
    }
}
