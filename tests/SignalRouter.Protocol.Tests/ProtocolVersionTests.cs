using NUnit.Framework;

namespace SignalRouter.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Test]
    public void CurrentCarriesTheDeclaredComponents()
    {
        Assert.That(ProtocolVersion.Current.Major, Is.EqualTo(ProtocolVersion.CurrentMajor));
        Assert.That(ProtocolVersion.Current.Minor, Is.EqualTo(ProtocolVersion.CurrentMinor));
    }

    [TestCase(0, 0, "0.0")]
    [TestCase(1, 0, "1.0")]
    [TestCase(12, 34, "12.34")]
    public void ToStringAndTryParseRoundTrip(int major, int minor, string text)
    {
        var version = new ProtocolVersion(major, minor);

        Assert.That(version.ToString(), Is.EqualTo(text));
        Assert.That(ProtocolVersion.TryParse(text, out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(version));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("1")]
    [TestCase("1.")]
    [TestCase(".1")]
    [TestCase("1.0.0")]
    [TestCase("1..0")]
    [TestCase("01.0")]
    [TestCase("1.00")]
    [TestCase("1.01")]
    [TestCase("+1.0")]
    [TestCase("-1.0")]
    [TestCase("1.-1")]
    [TestCase(" 1.0")]
    [TestCase("1.0 ")]
    [TestCase("1 .0")]
    [TestCase("a.b")]
    [TestCase("1.0a")]
    [TestCase("１.０")]
    [TestCase("1234567890.0")]
    public void TryParseRejectsAnythingButStrictMajorDotMinor(string? text)
    {
        Assert.That(ProtocolVersion.TryParse(text, out _), Is.False);
    }

    [Test]
    public void ZeroComponentsParseWithoutBeingTreatedAsLeadingZeros()
    {
        Assert.That(ProtocolVersion.TryParse("0.1", out var version), Is.True);
        Assert.That(version, Is.EqualTo(new ProtocolVersion(0, 1)));
    }

    [Test]
    public void ConstructorRejectsNegativeComponents()
    {
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new ProtocolVersion(-1, 0));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new ProtocolVersion(0, -1));
    }

    [Test]
    public void MajorCompatibilityIgnoresTheMinorComponent()
    {
        var baseline = new ProtocolVersion(1, 0);

        Assert.That(baseline.IsMajorCompatibleWith(new ProtocolVersion(1, 7)), Is.True);
        Assert.That(baseline.IsMajorCompatibleWith(new ProtocolVersion(2, 0)), Is.False);
    }

    [Test]
    public void EqualityComparesBothComponents()
    {
        var left = new ProtocolVersion(1, 2);
        var same = new ProtocolVersion(1, 2);
        var differentMinor = new ProtocolVersion(1, 3);
        var differentMajor = new ProtocolVersion(2, 2);

        Assert.That(left, Is.EqualTo(same));
        Assert.That(left.GetHashCode(), Is.EqualTo(same.GetHashCode()));
        Assert.That(left == same, Is.True);
        Assert.That(left != differentMinor, Is.True);
        Assert.That(left.Equals(differentMajor), Is.False);
    }
}
