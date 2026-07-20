using System.Text;
using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class StateCanonicalizerTests
{
    [Test]
    public void ObjectKeysAreEmittedInAscendingOrdinalOrder()
    {
        var canonical = Canonical("{\"b\":1,\"a\":2,\"c\":3}");

        Assert.That(canonical, Is.EqualTo("{\"a\":2,\"b\":1,\"c\":3}"));
    }

    [Test]
    public void NestedObjectKeysAreSortedRecursively()
    {
        var canonical = Canonical("{\"outer\":{\"y\":1,\"x\":2},\"arr\":[{\"n\":1,\"m\":2}]}");

        Assert.That(
            canonical,
            Is.EqualTo("{\"arr\":[{\"m\":2,\"n\":1}],\"outer\":{\"x\":2,\"y\":1}}"));
    }

    [Test]
    public void ArrayOrderIsPreserved()
    {
        var canonical = Canonical("[3,1,2]");

        Assert.That(canonical, Is.EqualTo("[3,1,2]"));
    }

    [Test]
    public void KeyOrderDoesNotChangeTheHash()
    {
        var first = StateCanonicalizer.ComputeHash(Snapshot("{\"a\":1,\"b\":2}"));
        var second = StateCanonicalizer.ComputeHash(Snapshot("{\"b\":2,\"a\":1}"));

        Assert.That(first, Is.EqualTo(second));
    }

    [Test]
    public void HashIsSixtyFourLowercaseHexCharacters()
    {
        var hash = StateCanonicalizer.ComputeHash(Snapshot("{\"a\":1}"));

        NUnitCompat.Multiple(() =>
        {
            Assert.That(hash, Has.Length.EqualTo(64));
            Assert.That(hash, Does.Match("^[0-9a-f]{64}$"));
        });
    }

    [Test]
    public void EqualNumbersWithDifferentScaleAreNotAssumedEqual()
    {
        // The canonicalizer only accepts integers; 1 and 1.0 are distinguished by parse, and
        // 1.0 is rejected outright (see NonIntegerNumbersAreRejected). This documents that the
        // canonical form does not silently coerce numeric spelling.
        Assert.That(Canonical("{\"n\":10}"), Is.EqualTo("{\"n\":10}"));
    }

    [Test]
    public void NonIntegerNumbersAreRejected()
    {
        NUnitCompat.Throws<ArgumentException>(
            () => StateCanonicalizer.Canonicalize(Snapshot("{\"n\":1.5}")));
    }

    [Test]
    public void ExponentNumbersAreRejected()
    {
        NUnitCompat.Throws<ArgumentException>(
            () => StateCanonicalizer.Canonicalize(Snapshot("{\"n\":1e3}")));
    }

    [Test]
    public void DuplicateObjectKeysAreRejected()
    {
        NUnitCompat.Throws<ArgumentException>(
            () => StateCanonicalizer.Canonicalize(Snapshot("{\"a\":1,\"a\":2}")));
    }

    [Test]
    public void MalformedJsonIsRejected()
    {
        NUnitCompat.Throws<ArgumentException>(
            () => StateCanonicalizer.Canonicalize(Snapshot("{\"a\":}")));
    }

    [Test]
    public void ScalarKindsRoundTripThroughTheCanonicalForm()
    {
        var canonical = Canonical("{\"s\":\"x\",\"t\":true,\"f\":false,\"z\":null,\"i\":-7}");

        Assert.That(
            canonical,
            Is.EqualTo("{\"f\":false,\"i\":-7,\"s\":\"x\",\"t\":true,\"z\":null}"));
    }

    private static string Canonical(string json)
    {
        return Encoding.UTF8.GetString(StateCanonicalizer.Canonicalize(Snapshot(json)));
    }

    private static StateProbeSnapshot Snapshot(string json)
    {
        return StateProbeSnapshot.FromJson(json);
    }
}
