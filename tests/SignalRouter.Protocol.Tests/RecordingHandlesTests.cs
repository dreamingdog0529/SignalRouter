using NUnit.Framework;

namespace SignalRouter.Protocol.Tests;

public sealed class RecordingHandlesTests
{
    [TestCase("rec-20260724t0100-1a2b3c4d")]
    [TestCase("rec-0")]
    [TestCase("rec-a-b-c")]
    public void WellFormedStemsAreValid(string handle)
    {
        Assert.That(RecordingHandles.IsValid(handle), Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("rec-")]
    [TestCase("log-20260724")]
    [TestCase("rec-UPPER")]
    [TestCase("rec-has.dot")]
    [TestCase("rec-has/slash")]
    [TestCase("rec-has\\slash")]
    [TestCase("rec-has space")]
    [TestCase("../rec-escape")]
    public void MalformedOrTraversalStemsAreRejected(string? handle)
    {
        Assert.That(RecordingHandles.IsValid(handle), Is.False);
    }

    [Test]
    public void OverlongStemsAreRejected()
    {
        var handle = "rec-" + new string('a', RecordingHandles.MaxHandleChars);

        Assert.That(RecordingHandles.IsValid(handle), Is.False);
    }

    [Test]
    public void RequireThrowsWithTheParameterName()
    {
        var exception = NUnitCompat.Throws<ArgumentException>(
            () => RecordingHandles.Require("bad handle", "handle"));

        Assert.That(exception!.ParamName, Is.EqualTo("handle"));
    }
}
