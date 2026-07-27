using System;
using System.Collections.Generic;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// The aggregate contract of <see cref="ValueArray{T}"/> (performance-track plan
/// P3b): the semantics frozen from its class-backed predecessor — defensive
/// copy, null-element rejection, order-sensitive equality, dictionary-key
/// behavior, indexer bounds — plus the ADR 0013-approved divergences of the
/// struct representation: <c>default</c> is the empty list, and the singleton
/// identity of <c>Empty</c> is value equality rather than reference identity.
/// </summary>
public sealed class ValueArrayCharacterizationTests
{
    [Test]
    public void FromDefensivelyCopiesTheSource()
    {
        var source = new[] { "a", "b", "c" };
        var list = ValueArray<string>.From(source);

        source[1] = "mutated";

        Assert.That(list[1], Is.EqualTo("b"), "later mutation of the source never shows through");
        Assert.That(list.Count, Is.EqualTo(3));
    }

    [Test]
    public void FromRejectsNullSourceAndNullElements()
    {
        AssertEx.Throws<ArgumentNullException>(() => ValueArray<string>.From(null!));
        AssertEx.Throws<ArgumentException>(() => ValueArray<string>.From(new[] { "a", null!, "c" }));
    }

    [Test]
    public void DefaultIsTheEmptyList()
    {
        // ADR 0013: default == Empty — "missing versus empty" is expressed by
        // the surrounding type (a nullable), never a distinguished default.
        ValueArray<string> uninitialized = default;

        Assert.That(uninitialized.Count, Is.EqualTo(0));
        Assert.That(uninitialized, Is.EqualTo(ValueArray<string>.Empty));
        Assert.That(uninitialized.AsSpan().Length, Is.EqualTo(0));
        Assert.That(ValueArray<string>.From(Array.Empty<string>()), Is.EqualTo(uninitialized));

        var enumerated = 0;
        foreach (var _ in uninitialized)
        {
            enumerated++;
        }

        Assert.That(enumerated, Is.EqualTo(0), "default enumerates nothing");
    }

    [Test]
    public void EnumerationPreservesInsertionOrder()
    {
        var list = ValueArray<int>.From(new[] { 3, 1, 2 });
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
        var left = ValueArray<int>.From(new[] { 1, 2, 3 });
        var sameElements = ValueArray<int>.From(new[] { 1, 2, 3 });
        var reordered = ValueArray<int>.From(new[] { 3, 2, 1 });
        var shorter = ValueArray<int>.From(new[] { 1, 2 });

        Assert.That(left.Equals(sameElements), Is.True);
        Assert.That(left == sameElements, Is.True);
        Assert.That(left.Equals(reordered), Is.False, "order participates in equality");
        Assert.That(left.Equals(shorter), Is.False);
        Assert.That(left.Equals(default(ValueArray<int>)), Is.False);
        Assert.That(left.GetHashCode(), Is.EqualTo(sameElements.GetHashCode()));
    }

    [Test]
    public void ValueTypeElementsCompareByValueEquality()
    {
        var left = ValueArray<SourceRevision>.From(new[] { new SourceRevision(1), new SourceRevision(2) });
        var right = ValueArray<SourceRevision>.From(new[] { new SourceRevision(1), new SourceRevision(2) });

        Assert.That(left.Equals(right), Is.True);
        Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
    }

    [Test]
    public void WorksAsADictionaryKey()
    {
        var index = new Dictionary<ValueArray<string>, int>
        {
            [ValueArray<string>.From(new[] { "a", "b" })] = 1,
        };

        Assert.That(index[ValueArray<string>.From(new[] { "a", "b" })], Is.EqualTo(1));
        Assert.That(index.ContainsKey(ValueArray<string>.From(new[] { "b", "a" })), Is.False);
    }

    [Test]
    public void IndexerAnswersByPositionAndThrowsOutOfRange()
    {
        var list = ValueArray<string>.From(new[] { "x", "y" });

        Assert.That(list[0], Is.EqualTo("x"));
        Assert.That(list[1], Is.EqualTo("y"));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = list[2]);
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = list[-1]);
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = default(ValueArray<string>)[0]);
    }

    [Test]
    public void ImplementsIReadOnlyListWithTheSameOrder()
    {
        IReadOnlyList<int> list = ValueArray<int>.From(new[] { 5, 4 });
        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(list[0], Is.EqualTo(5));
        Assert.That(list[1], Is.EqualTo(4));
    }

    [Test]
    public void AsSpanExposesTheElementsWithoutExposingTheStorage()
    {
        var list = ValueArray<int>.From(new[] { 7, 8, 9 });
        var span = list.AsSpan();

        Assert.That(span.Length, Is.EqualTo(3));
        Assert.That(span[1], Is.EqualTo(8));
    }
}
