using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// Deadline behavior of armed waits through the public surface (plan P1c,
/// finding A4): the per-pump scan became a deadline index, and everything an
/// observer can see is pinned here — every due wait resolves, none early, in
/// deterministic order (deadline first, arm order on ties), cancellation
/// leaves no residue, and a wait armed without a timeout never fires.
/// </summary>
public sealed class DeadlineIndexTests
{
    private static KernelFixture Arm(out RecordingWaitObserver observer, params long[] timeouts)
    {
        var fixture = new KernelFixture();
        observer = new RecordingWaitObserver();
        foreach (var timeout in timeouts)
        {
            fixture.Runtime.Control.ArmWait(
                KernelFixture.LabelExists, KernelFixture.Agent, timeout, observer);
        }

        fixture.PumpUntilIdle();
        return fixture;
    }

    [Test]
    public void DueWaitsResolveInDeadlineOrderWithArmOrderTies()
    {
        var fixture = new KernelFixture();
        var observer = new RecordingWaitObserver();
        var timeouts = new long[] { 300, 150, 200, 150 };
        var operations = new OperationId[timeouts.Length];
        for (var i = 0; i < timeouts.Length; i++)
        {
            operations[i] = fixture.Runtime.Control.ArmWait(
                KernelFixture.LabelExists, KernelFixture.Agent, timeouts[i], observer);
        }

        fixture.PumpUntilIdle();
        Assert.That(observer.Resolutions, Is.Empty, "nothing is due at logical now 100");

        fixture.LogicalNow = 300;
        fixture.Pump();

        Assert.That(
            observer.Resolutions.Select(pair => pair.Resolution).Distinct().Single(),
            Is.EqualTo(PredicateResolution.TimedOut));
        Assert.That(
            observer.Resolutions.Select(pair => pair.Operation).ToArray(),
            Is.EqualTo(new[] { operations[1], operations[3], operations[2], operations[0] }),
            "deadline ascending (150, 150, 200, 300); the tie at 150 resolves in arm order");
    }

    [Test]
    public void NoWaitResolvesBeforeItsDeadline()
    {
        var fixture = Arm(out var observer, 200, 300);

        fixture.LogicalNow = 250;
        fixture.Pump();

        Assert.That(observer.Resolutions.Count, Is.EqualTo(1), "only the 200 deadline is due at 250");

        fixture.LogicalNow = 300;
        fixture.Pump();
        Assert.That(observer.Resolutions.Count, Is.EqualTo(2), "the 300 deadline follows exactly at 300");
    }

    [Test]
    public void AWaitArmedWithoutATimeoutNeverFires()
    {
        var fixture = Arm(out var observer, long.MaxValue);

        fixture.LogicalNow = long.MaxValue - 1;
        fixture.Pump();

        Assert.That(observer.Resolutions, Is.Empty);
    }

    [Test]
    public void CancelledWaitsLeaveNoResidueBehind()
    {
        // Fill to the armed-wait bound with no-timeout waits, cancel them all,
        // then arm to the bound again: a tombstone anywhere would either leak
        // (capacity refusal) or fire a stale resolution later.
        var fixture = new KernelFixture();
        var observer = new RecordingWaitObserver();
        var operations = new OperationId[256];
        for (var i = 0; i < 256; i++)
        {
            operations[i] = fixture.Runtime.Control.ArmWait(
                KernelFixture.LabelExists, KernelFixture.Agent, long.MaxValue, observer);
        }

        fixture.PumpUntilIdle();
        foreach (var operation in operations)
        {
            fixture.Runtime.Control.CancelWait(operation);
        }

        fixture.PumpUntilIdle();
        var cancelled = observer.Resolutions.Count;

        var second = new RecordingWaitObserver();
        for (var i = 0; i < 256; i++)
        {
            fixture.Runtime.Control.ArmWait(
                KernelFixture.LabelExists, KernelFixture.Agent, 500 + i, second);
        }

        fixture.PumpUntilIdle();
        Assert.That(observer.Resolutions.Count, Is.EqualTo(cancelled), "no stale resolution from the first batch");

        fixture.LogicalNow = 10_000;
        fixture.Pump();
        Assert.That(
            second.Resolutions.Count, Is.EqualTo(256),
            "the second full batch armed and timed out completely — nothing leaked from the first");
        Assert.That(
            second.Resolutions.Select(pair => pair.Resolution).Distinct().Single(),
            Is.EqualTo(PredicateResolution.TimedOut));
    }

    [Test]
    public void MiddleCancellationsKeepTheRemainingOrderExact()
    {
        var fixture = new KernelFixture();
        var observer = new RecordingWaitObserver();
        var operations = new OperationId[16];
        for (var i = 0; i < 16; i++)
        {
            // Reverse deadline order: op i has deadline 400 - 10 * i.
            operations[i] = fixture.Runtime.Control.ArmWait(
                KernelFixture.LabelExists, KernelFixture.Agent, 400 - 10 * i, observer);
        }

        fixture.PumpUntilIdle();
        for (var i = 0; i < 16; i += 2)
        {
            fixture.Runtime.Control.CancelWait(operations[i]);
        }

        fixture.PumpUntilIdle();
        var cancelledCount = observer.Resolutions.Count;

        fixture.LogicalNow = 1000;
        fixture.Pump();

        var timedOut = observer.Resolutions.Skip(cancelledCount)
            .Select(pair => pair.Operation).ToArray();
        Assert.That(
            timedOut,
            Is.EqualTo(new[]
            {
                operations[15], operations[13], operations[11], operations[9],
                operations[7], operations[5], operations[3], operations[1],
            }),
            "survivors resolve in strict deadline order after middle removals");
    }
}
