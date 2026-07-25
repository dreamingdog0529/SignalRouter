using System;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// guarantees.md §2/§3: each taxonomy is a distinct axis with the exact spec
/// vocabulary; honest uncertainty is unconvertible; constructor-enforced invariants
/// hold (Rejected ⇒ no permitted effect, Incomplete ⇔ reason, no fabricated
/// terminals).
/// </summary>
public sealed class OutcomeTaxonomyTests
{
    [Test]
    public void InteractionOutcomesMatchTheSpecTable()
    {
        Assert.That(Enum.GetNames<InteractionOutcome>(), Is.EqualTo(new[]
        {
            "Succeeded", "Rejected", "Faulted", "Cancelled", "OutcomeUnknown",
        }));
    }

    [Test]
    public void RecordingOutcomesMatchTheSpecTable()
    {
        Assert.That(Enum.GetNames<RecordingOutcomeKind>(), Is.EqualTo(new[]
        {
            "Completed", "Incomplete", "Interrupted", "OpenFailed",
        }));
    }

    [Test]
    public void ReplayComparisonOutcomesMatchTheSpecTable()
    {
        Assert.That(Enum.GetNames<ReplayComparisonKind>(), Is.EqualTo(new[]
        {
            "Equal", "Diverged", "Incomparable",
        }));
    }

    [Test]
    public void QueryAnswersMatchTheSpecTable()
    {
        Assert.That(Enum.GetNames<QueryAnswerKind>(), Is.EqualTo(new[]
        {
            "Pending", "Terminal", "RuntimeUnavailable", "OutcomeUnknown",
        }));
    }

    [Test]
    public void IncompleteRequiresAReasonAndOthersRefuseOne()
    {
        AssertEx.Throws<ArgumentException>(() => RecordingOutcome.Incomplete(default));
        AssertEx.Throws<InvalidOperationException>(() => _ = RecordingOutcome.Completed.Reason);
        AssertEx.Throws<InvalidOperationException>(() => _ = RecordingOutcome.Interrupted.Reason);
        Assert.That(
            RecordingOutcome.Incomplete(IncompleteReason.SizeLimit).Reason,
            Is.EqualTo(IncompleteReason.SizeLimit));
    }

    [Test]
    public void IncomparableRequiresAReasonAndIsDistinctFromDiverged()
    {
        AssertEx.Throws<ArgumentException>(() => ReplayComparisonOutcome.Incomparable(default));
        var incomparable = ReplayComparisonOutcome.Incomparable(IncomparableReason.CancellationTiming);
        Assert.That(incomparable, Is.Not.EqualTo(ReplayComparisonOutcome.Diverged));
        Assert.That(incomparable.Kind, Is.Not.EqualTo(ReplayComparisonKind.Diverged));
    }

    [Test]
    public void QueryAnswersCannotFabricateATerminal()
    {
        AssertEx.Throws<InvalidOperationException>(() => _ = QueryAnswer.RuntimeUnavailable.TerminalOutcome);
        AssertEx.Throws<InvalidOperationException>(() => _ = QueryAnswer.OutcomeUnknown.TerminalOutcome);
        AssertEx.Throws<ArgumentException>(() => QueryAnswer.Terminal(InteractionOutcome.OutcomeUnknown));
    }

    [Test]
    public void PredicateVocabulariesMatchTheSpec()
    {
        Assert.That(Enum.GetNames<PredicateResolution>(), Is.EqualTo(new[]
        {
            "Satisfied", "TimedOut", "Cancelled", "Faulted", "Unknown",
        }));
        Assert.That(Enum.GetNames<PredicateEvaluationKind>(), Is.EqualTo(new[]
        {
            "Satisfied", "False", "Unevaluable",
        }));
        Assert.That(Enum.GetNames<PostconditionResult>(), Is.EqualTo(new[]
        {
            "Satisfied", "False", "TimedOut", "Unknown",
        }));
        Assert.That(Enum.GetNames<CancellationPhase>(), Is.EqualTo(new[]
        {
            "BeforeEffect", "DuringEffect", "AfterEffect",
        }));
    }

    [Test]
    public void RecordingControlOutcomeIsSeparateFromArtifactStates()
    {
        Assert.That(Enum.GetNames<RecordingControlOutcome>(), Is.EqualTo(new[]
        {
            "Succeeded", "Failed",
        }));
        Assert.That(typeof(RecordingControlOutcome), Is.Not.EqualTo(typeof(RecordingOutcomeKind)));
    }
}
