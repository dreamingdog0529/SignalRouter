using NUnit.Framework;
using SignalRouter.Protocol.Transport;

namespace SignalRouter.Protocol.Tests;

public sealed class ProtocolReconnectPolicyTests
{
    [Test]
    public void TheDelayCeilingGrowsExponentiallyToTheCap()
    {
        // random() = just under 1 makes NextDelay return (almost) the ceiling
        // itself, exposing the exponential series.
        var policy = ProtocolReconnectPolicy.CreateDefault(() => 0.999999);

        Assert.That(policy.NextDelay(0).TotalMilliseconds, Is.EqualTo(250).Within(1));
        Assert.That(policy.NextDelay(1).TotalMilliseconds, Is.EqualTo(500).Within(1));
        Assert.That(policy.NextDelay(2).TotalMilliseconds, Is.EqualTo(1000).Within(1));
        Assert.That(policy.NextDelay(4).TotalMilliseconds, Is.EqualTo(4000).Within(1));
        Assert.That(policy.NextDelay(5).TotalMilliseconds, Is.EqualTo(5000).Within(1));
        Assert.That(policy.NextDelay(50).TotalMilliseconds, Is.EqualTo(5000).Within(1));
    }

    [Test]
    public void FullJitterSpansDownToZero()
    {
        var policy = ProtocolReconnectPolicy.CreateDefault(() => 0.0);

        Assert.That(policy.NextDelay(10), Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void TheJitterSampleScalesTheCeiling()
    {
        var policy = ProtocolReconnectPolicy.CreateDefault(() => 0.5);

        Assert.That(policy.NextDelay(0).TotalMilliseconds, Is.EqualTo(125).Within(0.001));
        Assert.That(policy.NextDelay(50).TotalMilliseconds, Is.EqualTo(2500).Within(0.001));
    }

    [Test]
    public void ConstructionAndInputsAreValidated()
    {
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new ProtocolReconnectPolicy(
            TimeSpan.Zero,
            2.0,
            TimeSpan.FromSeconds(5),
            () => 0.5));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new ProtocolReconnectPolicy(
            TimeSpan.FromMilliseconds(250),
            0.5,
            TimeSpan.FromSeconds(5),
            () => 0.5));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new ProtocolReconnectPolicy(
            TimeSpan.FromSeconds(10),
            2.0,
            TimeSpan.FromSeconds(5),
            () => 0.5));
        NUnitCompat.Throws<ArgumentNullException>(
            () => _ = new ProtocolReconnectPolicy(
                TimeSpan.FromMilliseconds(250),
                2.0,
                TimeSpan.FromSeconds(5),
                null!));

        var policy = ProtocolReconnectPolicy.CreateDefault(() => 1.0);
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = policy.NextDelay(-1));
        NUnitCompat.Throws<InvalidOperationException>(() => _ = policy.NextDelay(0));
    }
}
