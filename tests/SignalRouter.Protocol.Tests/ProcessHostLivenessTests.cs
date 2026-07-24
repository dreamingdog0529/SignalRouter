using System.Diagnostics;
using NUnit.Framework;
using SignalRouter.Protocol.HostDiscovery;

namespace SignalRouter.Protocol.Tests;

public sealed class ProcessHostLivenessTests
{
    [Test]
    public void TheCurrentProcessIsAlive()
    {
        using var self = Process.GetCurrentProcess();
        var startedAt = new DateTimeOffset(self.StartTime.ToUniversalTime());

        Assert.That(new ProcessHostLiveness().IsAlive(self.Id, startedAt), Is.True);
    }

    [Test]
    public void AMismatchedStartTimeIsNotAliveEvenForALivePid()
    {
        // Guards against pid reuse: the same pid with a different start time is a
        // different process, so it must not read as the recorded host.
        using var self = Process.GetCurrentProcess();
        var wrongStart = new DateTimeOffset(self.StartTime.ToUniversalTime()).AddHours(-1);

        Assert.That(new ProcessHostLiveness().IsAlive(self.Id, wrongStart), Is.False);
    }

    [Test]
    public void NonPositiveAndUnknownPidsAreNotAlive()
    {
        var liveness = new ProcessHostLiveness();
        var now = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);

        Assert.That(liveness.IsAlive(0, now), Is.False);
        Assert.That(liveness.IsAlive(-1, now), Is.False);
        // A pid that (almost certainly) names no process resolves to not-alive
        // rather than throwing.
        Assert.That(liveness.IsAlive(int.MaxValue, now), Is.False);
    }
}
