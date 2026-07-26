using System;
using NUnit.Framework;

namespace SignalRouter.V2.Performance.Tests;

/// <summary>
/// The harness proves itself before it measures anything (plan P0b): a meter
/// that cannot read zero as zero, or that misses a known allocation, would make
/// every downstream number meaningless.
/// </summary>
[NonParallelizable]
public sealed class AllocationMeterSelfTests
{
    private static int counter;

    [Test]
    public void AnAllocationFreeOperationMeasuresExactlyZero()
    {
        // A static lambda: no closure, no per-invocation allocation. Interlocked
        // keeps the loop body from being optimized away entirely.
        var bytes = AllocationMeter.BytesPerOperation(
            static () => System.Threading.Interlocked.Increment(ref counter));

        Assert.That(bytes, Is.EqualTo(0), "the meter must read a clean operation as exactly zero");
    }

    [Test]
    public void AKnownAllocationIsDetectedAtItsFullSize()
    {
        var bytes = AllocationMeter.BytesPerOperation(static () => Sink(new byte[128]));

        // 128 payload bytes plus the object header; the exact header size is a
        // runtime detail, so assert the floor only.
        Assert.That(bytes, Is.GreaterThanOrEqualTo(128), "a 128-byte array must be visible in full");
    }

    [Test]
    public void RoundingCanNeverHideASmallAllocation()
    {
        // One small allocation per 256 iterations: ceiling division must report
        // at least one byte per operation, never a rounded-down zero.
        var iteration = 0;
        var bytes = AllocationMeter.BytesPerOperation(
            () =>
            {
                iteration++;
                if (iteration % 256 == 0)
                {
                    Sink(new byte[16]);
                }
            },
            warmupIterations: 256,
            measuredIterations: 256);

        Assert.That(bytes, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void AFailingOperationSurfacesTheFailure()
    {
        Action measure = () =>
            AllocationMeter.BytesPerOperation(static () => throw new FormatException("boom"));

        Assert.Throws<InvalidOperationException>(measure);
    }

    private static volatile byte[]? sink;

    private static void Sink(byte[] array) => sink = array;
}
