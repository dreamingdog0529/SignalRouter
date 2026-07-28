using System.Linq;
using NUnit.Framework;

namespace SignalRouter.Contracts.Tests;

/// <summary>
/// verification.md §2.2–§2.3 — the pure snapshot-local evaluator: operator
/// semantics, the no-boolean-oracle rule, three-valued composition, determinism,
/// and the evaluation cost bound.
/// </summary>
public sealed class PredicateEvaluatorTests
{
    private const string Label = "nodes/save/attributes/label";
    private const string Enabled = "nodes/save/attributes/enabled";
    private const string Items = "sources/inventory/items";

    private static PredicateDefinition Define(params PredicateExpression[] expressions)
    {
        var clauses = expressions
            .Select((expression, index) => new PredicateClause(new ClauseId($"c{index}"), expression));
        return new PredicateDefinition(ValueArray<PredicateClause>.From(clauses));
    }

    private static PredicateEvaluationOutcome Evaluate(
        FakeObservationLookup lookup, params PredicateExpression[] expressions) =>
        PredicateEvaluator.Evaluate(Define(expressions), lookup, PredicateStructuralBounds.Default).Outcome;

    [Test]
    public void OperatorSemanticsMatchTheSpecTable()
    {
        var lookup = new FakeObservationLookup()
            .WithValue(Label, FieldValue.Of("Save file"))
            .WithValue(Enabled, FieldValue.Of(true))
            .WithCollection(Items, CollectionCountLookup.Present(3));

        Assert.That(
            Evaluate(lookup, new ExistsExpression(new FieldPath(Label))),
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(
            Evaluate(lookup, new ComparisonExpression(
                new FieldPath(Label), ComparisonOperator.Eq, PredicateOperand.Of("Save file"))),
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(
            Evaluate(lookup, new StringMatchExpression(
                new FieldPath(Label), StringMatchKind.Prefix, PredicateOperand.Of("Save"))),
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(
            Evaluate(lookup, new StringMatchExpression(
                new FieldPath(Label), StringMatchKind.Suffix, PredicateOperand.Of("file"))),
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(
            Evaluate(lookup, new StringMatchExpression(
                new FieldPath(Label), StringMatchKind.Contains, PredicateOperand.Of("ve fi"))),
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(
            Evaluate(lookup, new CountExpression(new FieldPath(Items), ComparisonOperator.Ge, 3)),
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(
            Evaluate(lookup, new CountExpression(new FieldPath(Items), ComparisonOperator.Lt, 3)),
            Is.EqualTo(PredicateEvaluationOutcome.False));
    }

    [Test]
    public void AbsentAndNullHoldNoValue()
    {
        var lookup = new FakeObservationLookup().WithValue(Label, FieldValue.Null);

        // Null: present without a value — equality false, inequality true.
        Assert.That(
            Evaluate(lookup, new ComparisonExpression(
                new FieldPath(Label), ComparisonOperator.Eq, PredicateOperand.Of("x"))),
            Is.EqualTo(PredicateEvaluationOutcome.False));
        Assert.That(
            Evaluate(lookup, new ComparisonExpression(
                new FieldPath(Label), ComparisonOperator.Ne, PredicateOperand.Of("x"))),
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(
            Evaluate(lookup, new ExistsExpression(new FieldPath(Label))),
            Is.EqualTo(PredicateEvaluationOutcome.False));

        // Absent: same comparison behavior through the absent path.
        Assert.That(
            Evaluate(lookup, new ComparisonExpression(
                new FieldPath("nodes/other/attributes/x"), ComparisonOperator.Eq, PredicateOperand.Of("x"))),
            Is.EqualTo(PredicateEvaluationOutcome.False));
        Assert.That(
            Evaluate(lookup, new ExistsExpression(new FieldPath("nodes/other/attributes/x"))),
            Is.EqualTo(PredicateEvaluationOutcome.False));
    }

    [Test]
    public void HiddenValuesNeverEvaluateFalse()
    {
        // verification.md §2.3: "a comparison against a value the caller is not
        // entitled to read is Unevaluable(Redacted) ... never False".
        var lookup = new FakeObservationLookup()
            .With(Label, FieldLookup.Redacted)
            .With(Enabled, FieldLookup.OutOfScope)
            .With("sources/inventory/count", FieldLookup.Incomplete(CompletenessReason.SourceUnavailable));

        Assert.That(
            Evaluate(lookup, new ComparisonExpression(
                new FieldPath(Label), ComparisonOperator.Eq, PredicateOperand.Of("secret-guess"))),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Redacted)));
        Assert.That(
            Evaluate(lookup, new ExistsExpression(new FieldPath(Enabled))),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.OutOfScope)));
        Assert.That(
            Evaluate(lookup, new ComparisonExpression(
                new FieldPath("sources/inventory/count"), ComparisonOperator.Eq, PredicateOperand.Of(1L))),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.SourceUnavailable)));
    }

    [Test]
    public void SecretOperandsAreUnevaluableWithoutAResolver()
    {
        var lookup = new FakeObservationLookup().WithValue(Label, FieldValue.Of("Save"));
        Assert.That(
            Evaluate(lookup, new ComparisonExpression(
                new FieldPath(Label), ComparisonOperator.Eq,
                PredicateOperand.OfSecret(new SecretReference("secret-1")))),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Redacted)));
    }

    [Test]
    public void CompositionIsThreeValued()
    {
        // verification.md §2.3 composition table.
        var lookup = new FakeObservationLookup()
            .WithValue(Label, FieldValue.Of("Save"))
            .With(Enabled, FieldLookup.Redacted);

        PredicateExpression trueLeaf = new ExistsExpression(new FieldPath(Label));
        PredicateExpression falseLeaf = new ComparisonExpression(
            new FieldPath(Label), ComparisonOperator.Eq, PredicateOperand.Of("Other"));
        PredicateExpression hiddenLeaf = new ExistsExpression(new FieldPath(Enabled));

        Assert.That(
            Evaluate(lookup, new BooleanExpression(BooleanOperator.And,
                ValueArray<PredicateExpression>.From(new[] { falseLeaf, hiddenLeaf }))),
            Is.EqualTo(PredicateEvaluationOutcome.False),
            "False AND Unevaluable = False");
        Assert.That(
            Evaluate(lookup, new BooleanExpression(BooleanOperator.And,
                ValueArray<PredicateExpression>.From(new[] { trueLeaf, hiddenLeaf }))),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Redacted)),
            "True AND Unevaluable = Unevaluable");
        Assert.That(
            Evaluate(lookup, new BooleanExpression(BooleanOperator.Or,
                ValueArray<PredicateExpression>.From(new[] { trueLeaf, hiddenLeaf }))),
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied),
            "True OR Unevaluable = True");
        Assert.That(
            Evaluate(lookup, new BooleanExpression(BooleanOperator.Or,
                ValueArray<PredicateExpression>.From(new[] { falseLeaf, hiddenLeaf }))),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Redacted)),
            "False OR Unevaluable = Unevaluable");
        Assert.That(
            Evaluate(lookup, new NotExpression(hiddenLeaf)),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Redacted)),
            "NOT Unevaluable = Unevaluable");

        // Commutativity: operand order never changes the answer.
        Assert.That(
            Evaluate(lookup, new BooleanExpression(BooleanOperator.And,
                ValueArray<PredicateExpression>.From(new[] { hiddenLeaf, falseLeaf }))),
            Is.EqualTo(PredicateEvaluationOutcome.False));
    }

    [Test]
    public void RuntimeTypeMismatchIsAContractConditionNotFalsity()
    {
        var lookup = new FakeObservationLookup().WithValue(Label, FieldValue.Of(42L));
        Assert.That(
            Evaluate(lookup, new ComparisonExpression(
                new FieldPath(Label), ComparisonOperator.Eq, PredicateOperand.Of("Save"))),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.UnsupportedContract)));
    }

    [Test]
    public void EvaluationIsDeterministic()
    {
        var lookup = new FakeObservationLookup()
            .WithValue(Label, FieldValue.Of("Save"))
            .WithCollection(Items, CollectionCountLookup.Present(2));
        var definition = Define(
            new ExistsExpression(new FieldPath(Label)),
            new CountExpression(new FieldPath(Items), ComparisonOperator.Eq, 2));

        var first = PredicateEvaluator.Evaluate(definition, lookup, PredicateStructuralBounds.Default);
        var second = PredicateEvaluator.Evaluate(definition, lookup, PredicateStructuralBounds.Default);

        Assert.That(second.Outcome, Is.EqualTo(first.Outcome));
        Assert.That(second.Clauses, Is.EqualTo(first.Clauses));
    }

    [Test]
    public void StepBudgetExhaustionIsUnevaluableNotAnException()
    {
        var lookup = new FakeObservationLookup().WithValue(Label, FieldValue.Of("Save"));
        var tinyBounds = new PredicateStructuralBounds(
            maxDepth: 16, maxNodeCount: 256, maxOperandLength: 4096,
            maxBatchSize: 32, maxEvaluationSteps: 2);
        var definition = Define(new BooleanExpression(BooleanOperator.And,
            ValueArray<PredicateExpression>.From(new PredicateExpression[]
            {
                new ExistsExpression(new FieldPath(Label)),
                new ExistsExpression(new FieldPath(Label)),
                new ExistsExpression(new FieldPath(Label)),
            })));

        var result = PredicateEvaluator.Evaluate(definition, lookup, tinyBounds);
        Assert.That(
            result.Outcome,
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(PredicateEvaluator.EvaluationBudgetExceeded)));
    }

    [Test]
    public void ClauseReportsCarryStableIdsAndRenderings()
    {
        var lookup = new FakeObservationLookup().WithValue(Label, FieldValue.Of("Save"));
        var result = PredicateEvaluator.Evaluate(
            Define(
                new ExistsExpression(new FieldPath(Label)),
                new ExistsExpression(new FieldPath("nodes/gone/attributes/x"))),
            lookup,
            PredicateStructuralBounds.Default);

        Assert.That(result.Outcome, Is.EqualTo(PredicateEvaluationOutcome.False));
        Assert.That(result.Clauses[0], Is.EqualTo(new ClauseEvaluation("c0", "true", "true")));
        Assert.That(result.Clauses[1], Is.EqualTo(new ClauseEvaluation("c1", "true", "false")));
    }
}
