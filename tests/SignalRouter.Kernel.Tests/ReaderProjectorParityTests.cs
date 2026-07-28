using System.Linq;
using NUnit.Framework;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel.Tests;

/// <summary>
/// The differential characterization of the projector-backed evaluation path: the
/// answer table of the item-2 pinned reader, transcribed as fixtures, must hold
/// unchanged now that <c>ObservationProjector</c> + <c>MaterializationLookup</c>
/// produce every internal read ("the projections replace the production path, not
/// the interface").
/// </summary>
public sealed class ReaderProjectorParityTests
{
    private static PredicateContractRef Register(
        KernelFixture fixture, string id, PredicateExpression expression)
    {
        var contract = new PredicateContractRef(new PredicateContractId(id), new ContractVersion(1, 0));
        fixture.Runtime.Bootstrap.RegisterPredicateContract(contract, new PredicateDefinition(
            ValueArray<PredicateClause>.From(new[]
            {
                new PredicateClause(new ClauseId("c0"), expression),
            })));
        return contract;
    }

    [Test]
    public void TheItemTwoAnswerTableHoldsThroughTheProjector()
    {
        var fixture = new KernelFixture(start: false);
        var labelIsSave = Register(fixture, "labelIsSave", new ComparisonExpression(
            new FieldPath("nodes/save/attributes/label"),
            ComparisonOperator.Eq, PredicateOperand.Of("Save")));
        var missingAttribute = Register(fixture, "missingAttribute", new ExistsExpression(
            new FieldPath("nodes/save/attributes/tooltip")));
        var hiddenNode = Register(fixture, "hiddenNode", new ExistsExpression(
            new FieldPath("nodes/secret/attributes/label")));
        var unregisteredNode = Register(fixture, "unregisteredNode", new ExistsExpression(
            new FieldPath("nodes/never-registered/attributes/label")));
        var secretSourceField = Register(fixture, "secretSourceField", new ComparisonExpression(
            new FieldPath("sources/inventory/secret"),
            ComparisonOperator.Eq, PredicateOperand.Of("hunter2")));
        var unpublishedSource = Register(fixture, "unpublishedSource", new ComparisonExpression(
            new FieldPath("sources/inventory/count"),
            ComparisonOperator.Ge, PredicateOperand.Of(1L)));
        fixture.Runtime.Start(fixture.Executor);

        var before = Evaluate(
            fixture, labelIsSave, missingAttribute, hiddenNode, unregisteredNode,
            secretSourceField, unpublishedSource);
        Assert.That(before[0], Is.EqualTo(PredicateEvaluationOutcome.Satisfied), "label == 'Save'");
        Assert.That(before[1], Is.EqualTo(PredicateEvaluationOutcome.False), "absent attribute");
        Assert.That(
            before[2],
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.OutOfScope)),
            "hidden node");
        Assert.That(
            before[3], Is.EqualTo(before[2]),
            "hidden and unregistered nodes answer identically — the exposure equivalence");
        Assert.That(
            before[4],
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.SourceUnavailable)),
            "before any publication the whole source is unavailable — even its sensitive field");
        Assert.That(
            before[5],
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.SourceUnavailable)),
            "a registered source without a document");

        fixture.PublishInventory(2);
        fixture.PumpUntilIdle();
        var after = Evaluate(fixture, unpublishedSource, secretSourceField);
        Assert.That(after[0], Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(
            after[1],
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Redacted)),
            "once a document exists the sensitive field answers Redacted, never False");
    }

    private static PredicateEvaluationOutcome[] Evaluate(
        KernelFixture fixture, params PredicateContractRef[] predicates)
    {
        var observer = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(predicates),
            KernelFixture.Agent,
            observer));
        fixture.PumpUntilIdle();
        return observer.Results!.Value.Select(result => result.Outcome).ToArray();
    }
}
