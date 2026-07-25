using System;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// verification.md §2.2 — the AST is exactly the allowlist, with constructor
/// invariants and structural accounting.
/// </summary>
public sealed class PredicateModelTests
{
    private static PredicateExpression Leaf(string path = "nodes/a/attributes/x") =>
        new ExistsExpression(new FieldPath(path));

    [Test]
    public void ClauseIdsMustBeUniqueAndDefinitionNonEmpty()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new PredicateDefinition(
            ValueList<PredicateClause>.Empty));

        var clause = new PredicateClause(new ClauseId("c1"), Leaf());
        AssertEx.Throws<ArgumentException>(() => _ = new PredicateDefinition(
            ValueList<PredicateClause>.From(new[]
            {
                clause,
                new PredicateClause(new ClauseId("c1"), Leaf("nodes/b/attributes/y")),
            })));
    }

    [Test]
    public void BooleanCompositionRequiresAtLeastTwoOperands()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new BooleanExpression(
            BooleanOperator.And, ValueList<PredicateExpression>.From(new[] { Leaf() })));
    }

    [Test]
    public void BooleanOperandsSupportOnlyEquality()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new ComparisonExpression(
            new FieldPath("nodes/a/attributes/enabled"),
            ComparisonOperator.Lt,
            PredicateOperand.Of(true)));
    }

    [Test]
    public void StringMatchRequiresAStringOperand()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new StringMatchExpression(
            new FieldPath("nodes/a/attributes/label"),
            StringMatchKind.Prefix,
            PredicateOperand.Of(42L)));
    }

    [Test]
    public void CountOperandMustNotBeNegative()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new CountExpression(
            new FieldPath("sources/inventory/items"), ComparisonOperator.Eq, -1));
    }

    [Test]
    public void StructuralAccountingCountsNodesAndDepth()
    {
        var tree = new NotExpression(new BooleanExpression(
            BooleanOperator.And,
            ValueList<PredicateExpression>.From(new[] { Leaf(), Leaf("nodes/b/attributes/y") })));

        Assert.That(tree.NodeCount, Is.EqualTo(4));
        Assert.That(tree.Depth, Is.EqualTo(3));
    }

    [Test]
    public void FieldPathsAreSegmentedAndRejectMalformedShapes()
    {
        var path = new FieldPath("sources/inventory/items");
        Assert.That(path.Segments, Is.EqualTo(new[] { "sources", "inventory", "items" }));
        AssertEx.Throws<ArgumentException>(() => _ = new FieldPath("/leading"));
        AssertEx.Throws<ArgumentException>(() => _ = new FieldPath("trailing/"));
        AssertEx.Throws<ArgumentException>(() => _ = new FieldPath("a//b"));
    }
}
