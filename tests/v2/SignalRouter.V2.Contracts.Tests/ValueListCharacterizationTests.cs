using System;
using System.Collections.Generic;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// Characterization of the current <see cref="ValueList{T}"/> semantics
/// (performance-track plan P0a, design D2): the aggregate representation is
/// scheduled to change to a struct-backed <c>ValueArray&lt;T&gt;</c>, and every
/// behavior pinned here is the contract the replacement must satisfy — except
/// where an ADR explicitly approves a divergence. These tests freeze semantics,
/// not implementation: nothing here asserts allocation shape or class-ness.
/// </summary>
public sealed class ValueListCharacterizationTests
{
    [Test]
    public void FromDefensivelyCopiesTheSource()
    {
        var source = new[] { "a", "b", "c" };
        var list = ValueList<string>.From(source);

        source[1] = "mutated";

        Assert.That(list[1], Is.EqualTo("b"), "later mutation of the source never shows through");
        Assert.That(list.Count, Is.EqualTo(3));
    }

    [Test]
    public void FromRejectsNullSourceAndNullElements()
    {
        AssertEx.Throws<ArgumentNullException>(() => ValueList<string>.From(null!));
        AssertEx.Throws<ArgumentException>(() => ValueList<string>.From(new[] { "a", null!, "c" }));
    }

    [Test]
    public void EmptyIsASingletonAndFromEmptyReturnsIt()
    {
        Assert.That(ValueList<string>.Empty, Is.SameAs(ValueList<string>.Empty));
        Assert.That(
            ValueList<string>.From(Array.Empty<string>()), Is.SameAs(ValueList<string>.Empty),
            "an empty copy collapses to the singleton");
        Assert.That(ValueList<string>.Empty.Count, Is.EqualTo(0));
    }

    [Test]
    public void EnumerationPreservesInsertionOrder()
    {
        var list = ValueList<int>.From(new[] { 3, 1, 2 });
        var seen = new List<int>();
        foreach (var item in list)
        {
            seen.Add(item);
        }

        Assert.That(seen, Is.EqualTo(new[] { 3, 1, 2 }), "the list never reorders; sorting is the caller's job");
    }

    [Test]
    public void EqualityIsElementWiseAndOrderSensitive()
    {
        var left = ValueList<int>.From(new[] { 1, 2, 3 });
        var sameElements = ValueList<int>.From(new[] { 1, 2, 3 });
        var reordered = ValueList<int>.From(new[] { 3, 2, 1 });
        var shorter = ValueList<int>.From(new[] { 1, 2 });

        Assert.That(left.Equals(sameElements), Is.True);
        Assert.That(left.Equals(reordered), Is.False, "order participates in equality");
        Assert.That(left.Equals(shorter), Is.False);
        Assert.That(left.GetHashCode(), Is.EqualTo(sameElements.GetHashCode()));

        // Last: flow analysis treats Equals(null) as a null comparison and joins
        // the receiver's null-state to maybe-null afterwards.
        Assert.That(left.Equals((ValueList<int>?)null), Is.False);
    }

    [Test]
    public void ValueTypeElementsCompareByValueEquality()
    {
        // Unlike v1's EquatableList, elements may be value types (the class doc's
        // stated reason for the type's existence).
        var left = ValueList<SourceRevision>.From(new[] { new SourceRevision(1), new SourceRevision(2) });
        var right = ValueList<SourceRevision>.From(new[] { new SourceRevision(1), new SourceRevision(2) });

        Assert.That(left.Equals(right), Is.True);
        Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
    }

    [Test]
    public void WorksAsADictionaryKey()
    {
        var index = new Dictionary<ValueList<string>, int>
        {
            [ValueList<string>.From(new[] { "a", "b" })] = 1,
        };

        Assert.That(index[ValueList<string>.From(new[] { "a", "b" })], Is.EqualTo(1));
        Assert.That(index.ContainsKey(ValueList<string>.From(new[] { "b", "a" })), Is.False);
    }

    [Test]
    public void IndexerAnswersByPositionAndThrowsOutOfRange()
    {
        var list = ValueList<string>.From(new[] { "x", "y" });

        Assert.That(list[0], Is.EqualTo("x"));
        Assert.That(list[1], Is.EqualTo("y"));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = list[2]);
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = list[-1]);
    }

    [Test]
    public void ImplementsIReadOnlyListWithTheSameOrder()
    {
        IReadOnlyList<int> list = ValueList<int>.From(new[] { 5, 4 });
        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(list[0], Is.EqualTo(5));
        Assert.That(list[1], Is.EqualTo(4));
    }
}
