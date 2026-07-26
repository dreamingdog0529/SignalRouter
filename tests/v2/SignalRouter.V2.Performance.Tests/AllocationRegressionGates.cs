using NUnit.Framework;
using SignalRouter.V2.Benchmarks;

namespace SignalRouter.V2.Performance.Tests;

/// <summary>
/// Gating allocation regressions — unlike the characterization tests, these
/// FAIL on regression. One gate lands with the fix that makes it true (plan
/// P1: per-fix gates; P6 consolidates them into the idle-zero gate), so the
/// suite only ever asserts properties the current code actually has. Exact
/// byte equality is deterministic on the kernel owner thread: these are
/// allocation counters, not timings, and hold in Debug and Release alike.
/// </summary>
[NonParallelizable]
[Category("AllocationGate")]
public sealed class AllocationRegressionGates
{
    /// <summary>
    /// P1a (finding A1): the quiescent pump publishes the status snapshot only
    /// on change, so its allocation no longer scales with retained terminals.
    /// Before the fix this was 280 B vs ~168 KB at 1024 retained terminals.
    /// </summary>
    [Test]
    public void IdlePumpAllocationIsIndependentOfRetainedTerminals()
    {
        var pristine = BenchWorld.Create(nodeCount: 64, withCodec: true);
        pristine.PumpUntilIdle();
        var loaded = BenchWorld.Create(nodeCount: 64, withCodec: true);
        loaded.FillTerminals(1024);
        loaded.PumpUntilIdle();

        var pristineBytes = AllocationMeter.BytesPerOperation(() => pristine.Pump());
        var loadedBytes = AllocationMeter.BytesPerOperation(() => loaded.Pump());

        TestContext.Out.WriteLine(
            $"[allocation-gate] idle pump: pristine {pristineBytes} B/op, " +
            $"1024 retained terminals {loadedBytes} B/op");
        Assert.That(
            loadedBytes, Is.EqualTo(pristineBytes),
            "the quiescent pump must not allocate proportionally to retained state (plan L0)");
    }
}
