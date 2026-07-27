using System.Linq;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// verification.md §4 — validate_predicate type-checks against the catalog without
/// evaluating: unknown field, type mismatch, unsupported operator, bound violations,
/// reported per clause.
/// </summary>
public sealed class PredicateTypeCheckTests
{
    private static readonly PredicateCatalog Catalog = new(ValueArray<FieldSchema>.From(new[]
    {
        new FieldSchema(new FieldPath("nodes/save/attributes/label"), FieldType.String),
        new FieldSchema(new FieldPath("nodes/save/attributes/enabled"), FieldType.Boolean),
        new FieldSchema(new FieldPath("sources/inventory/count"), FieldType.Integer),
        new FieldSchema(new FieldPath("sources/inventory/items"), FieldType.KeyedCollection),
    }));

    private static PredicateDefinition Define(params PredicateExpression[] expressions)
    {
        var clauses = expressions
            .Select((expression, index) => new PredicateClause(new ClauseId($"c{index}"), expression));
        return new PredicateDefinition(ValueArray<PredicateClause>.From(clauses));
    }

    [Test]
    public void ValidPredicatePassesWithoutErrors()
    {
        var result = PredicateTypeChecker.Check(
            Define(
                new ComparisonExpression(
                    new FieldPath("nodes/save/attributes/label"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of("Save")),
                new CountExpression(
                    new FieldPath("sources/inventory/items"), ComparisonOperator.Ge, 1)),
            Catalog,
            PredicateStructuralBounds.Default);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void UnknownFieldIsReportedPerClause()
    {
        var result = PredicateTypeChecker.Check(
            Define(new ExistsExpression(new FieldPath("nodes/missing/attributes/x"))),
            Catalog,
            PredicateStructuralBounds.Default);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors[0].Kind, Is.EqualTo(PredicateValidationErrorKind.UnknownField));
        Assert.That(result.Errors[0].Clause, Is.EqualTo(new ClauseId("c0")));
    }

    [Test]
    public void TypeMismatchesAndUnsupportedOperatorsAreReported()
    {
        var result = PredicateTypeChecker.Check(
            Define(
                new ComparisonExpression(
                    new FieldPath("sources/inventory/count"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of("not-an-integer")),
                new StringMatchExpression(
                    new FieldPath("sources/inventory/count"),
                    StringMatchKind.Contains,
                    PredicateOperand.Of("x")),
                new CountExpression(
                    new FieldPath("nodes/save/attributes/label"), ComparisonOperator.Eq, 1),
                new ComparisonExpression(
                    new FieldPath("sources/inventory/items"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of(1L))),
            Catalog,
            PredicateStructuralBounds.Default);

        Assert.That(result.Errors.Select(e => e.Kind), Is.EquivalentTo(new[]
        {
            PredicateValidationErrorKind.TypeMismatch,
            PredicateValidationErrorKind.TypeMismatch,
            PredicateValidationErrorKind.UnsupportedOperator,
            PredicateValidationErrorKind.UnsupportedOperator,
        }));
    }

    [Test]
    public void StructuralBoundsAreEnforced()
    {
        var tinyBounds = new PredicateStructuralBounds(
            maxDepth: 1, maxNodeCount: 1, maxOperandLength: 4, maxBatchSize: 1, maxEvaluationSteps: 16);

        var tooDeep = PredicateTypeChecker.Check(
            Define(new NotExpression(new ExistsExpression(new FieldPath("nodes/save/attributes/label")))),
            Catalog,
            tinyBounds);
        Assert.That(
            tooDeep.Errors.Any(e => e.Kind == PredicateValidationErrorKind.BoundViolation),
            Is.True);

        var operandTooLong = PredicateTypeChecker.Check(
            Define(new ComparisonExpression(
                new FieldPath("nodes/save/attributes/label"),
                ComparisonOperator.Eq,
                PredicateOperand.Of("long-operand"))),
            Catalog,
            tinyBounds);
        Assert.That(
            operandTooLong.Errors.Any(e => e.Kind == PredicateValidationErrorKind.BoundViolation),
            Is.True);

        // Secret references honor the configured operand bound too.
        var secretTooLong = PredicateTypeChecker.Check(
            Define(new StringMatchExpression(
                new FieldPath("nodes/save/attributes/label"),
                StringMatchKind.Contains,
                PredicateOperand.OfSecret(new SecretReference("secret-longer-than-four")))),
            Catalog,
            tinyBounds);
        Assert.That(
            secretTooLong.Errors.Any(e => e.Kind == PredicateValidationErrorKind.BoundViolation),
            Is.True);
    }

    [Test]
    public void AMaximumLengthUnknownPathStillValidatesInsteadOfThrowing()
    {
        var longPath = new FieldPath("nodes/" + new string('x', 1018));
        Assert.That(longPath.Value.Length, Is.EqualTo(1024));

        var result = PredicateTypeChecker.Check(
            Define(new ExistsExpression(longPath)),
            Catalog,
            PredicateStructuralBounds.Default);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors[0].Kind, Is.EqualTo(PredicateValidationErrorKind.UnknownField));
    }
}
