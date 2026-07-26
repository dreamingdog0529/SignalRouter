using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// Characterization of the trace ring's observable behavior (plan P1b): the
/// storage is being rewritten from a rebuild-on-eviction queue to a true
/// circular buffer, and everything a reader can see — event order, gap
/// placement, gap drop counts, <see cref="KernelTraceRing.TotalDropped"/> —
/// is pinned here first so the rewrite provably changes cost only.
/// </summary>
public sealed class KernelTraceRingTests
{
    private static readonly RuntimeIncarnationId Incarnation = new("inc-1");

    private static SemanticEvent Event(string code) => new(
        new EventKind("StateTransition"),
        Incarnation,
        EventCausation.None,
        detailCode: code);

    private static List<string> Rendered(KernelTraceRing ring) =>
        ring.Snapshot()
            .Select(e => e.Kind.Value + ":" + (e.DetailCode ?? "-"))
            .ToList();

    [Test]
    public void EventsAreRetainedInEmissionOrderUnderCapacity()
    {
        var ring = new KernelTraceRing(capacity: 8, byteCapacity: 1024 * 1024);
        for (var i = 0; i < 5; i++)
        {
            ring.Emit(Event("E" + i));
        }

        Assert.That(Rendered(ring), Is.EqualTo(new[]
        {
            "StateTransition:E0", "StateTransition:E1", "StateTransition:E2",
            "StateTransition:E3", "StateTransition:E4",
        }));
        Assert.That(ring.TotalDropped, Is.EqualTo(0));
    }

    [Test]
    public void CountOverflowDropsOldestAndStandsAGapAtTheLossPoint()
    {
        var ring = new KernelTraceRing(capacity: 4, byteCapacity: 1024 * 1024);
        for (var i = 0; i < 5; i++)
        {
            ring.Emit(Event("E" + i));
        }

        // Capacity 4: the fifth emit evicts E0 and E1 (to capacity - 1, one
        // slot reserved for the marker) and the gap stands before the tail.
        Assert.That(Rendered(ring), Is.EqualTo(new[]
        {
            "TraceGap:Dropped2", "StateTransition:E2", "StateTransition:E3", "StateTransition:E4",
        }));
        Assert.That(ring.TotalDropped, Is.EqualTo(2));
    }

    [Test]
    public void AnEvictedGapMarkerIsReplacedAndNeverCountedAsALostEvent()
    {
        var ring = new KernelTraceRing(capacity: 4, byteCapacity: 1024 * 1024);
        for (var i = 0; i < 5; i++)
        {
            ring.Emit(Event("E" + i));
        }

        Assert.That(ring.TotalDropped, Is.EqualTo(2), "sanity: the first overflow dropped E0, E1");

        // The next emit evicts the old marker (bookkeeping, never counted as a
        // lost event) plus E2 (counted), and a fresh marker with the new batch
        // count stands at the loss point.
        ring.Emit(Event("E5"));

        Assert.That(Rendered(ring), Is.EqualTo(new[]
        {
            "TraceGap:Dropped1", "StateTransition:E3", "StateTransition:E4", "StateTransition:E5",
        }));
        Assert.That(ring.TotalDropped, Is.EqualTo(3), "the marker itself never inflates the count");
    }

    [Test]
    public void SustainedOverflowKeepsExactlyOneLeadingGapWithTheLatestBatchCount()
    {
        var ring = new KernelTraceRing(capacity: 4, byteCapacity: 1024 * 1024);
        for (var i = 0; i < 12; i++)
        {
            ring.Emit(Event("E" + i));
        }

        var rendered = Rendered(ring);
        Assert.That(rendered.Count(line => line.StartsWith("TraceGap")), Is.LessThanOrEqualTo(1));
        Assert.That(rendered[^1], Is.EqualTo("StateTransition:E11"), "the newest event always survives");
        Assert.That(
            ring.TotalDropped + rendered.Count(line => !line.StartsWith("TraceGap")),
            Is.EqualTo(12),
            "every emitted event is either retained or counted as dropped");
    }

    [Test]
    public void ByteOverflowEvictsUntilTheBudgetHoldsAndMarksTheGap()
    {
        // Each event estimates at 64 + kind + incarnation + detail; a tight byte
        // ceiling forces eviction while the count stays far under capacity.
        var ring = new KernelTraceRing(capacity: 64, byteCapacity: 600);
        for (var i = 0; i < 6; i++)
        {
            ring.Emit(Event("E" + i));
        }

        var rendered = Rendered(ring);
        Assert.That(ring.TotalDropped, Is.GreaterThan(0));
        Assert.That(rendered[0], Does.StartWith("TraceGap:Dropped"));
        Assert.That(rendered[^1], Is.EqualTo("StateTransition:E5"));
    }

    [Test]
    public void SnapshotIsAPointInTimeCopy()
    {
        var ring = new KernelTraceRing(capacity: 8, byteCapacity: 1024 * 1024);
        ring.Emit(Event("E0"));
        var before = ring.Snapshot();
        ring.Emit(Event("E1"));

        Assert.That(before.Count, Is.EqualTo(1), "a snapshot never observes later emissions");
        Assert.That(ring.Snapshot().Count, Is.EqualTo(2));
    }
}
