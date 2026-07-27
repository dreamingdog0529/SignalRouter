using NUnit.Framework;
using SignalRouter.V2.Benchmarks;

namespace SignalRouter.V2.Performance.Tests;

/// <summary>
/// Gating allocation regressions — unlike the characterization tests, these
/// FAIL on regression. One gate lands with the fix that makes it true (plan
/// P1: per-fix gates; the consolidated quiescent-zero gate arrives with the
/// track's completion), so the suite only ever asserts properties the current
/// code actually has. Counters are deterministic on the kernel owner thread —
/// equality and absolute gates carry no tolerance — and every gate runs the
/// exact workload its PROFILE-default.md row claims (bench/v2), so a green CI
/// vouches for the profile's L1 rows, not a smaller stand-in.
/// </summary>
[NonParallelizable]
[Category("AllocationGate")]
public sealed class AllocationRegressionGates
{
    /// <summary>
    /// P1a/P1c (findings A1/A4): the quiescent pump publishes the status
    /// snapshot only on change and consults the deadline index instead of
    /// scanning, so its allocation is independent of retained terminals — at
    /// the profile workload of 4096 (the default terminal capacity). Before
    /// the fixes this was 280 B vs ~714 KB.
    /// </summary>
    [Test]
    public void IdlePumpAllocationIsIndependentOfRetainedTerminals()
    {
        var pristine = BenchWorld.Create(nodeCount: 64, withCodec: true);
        pristine.PumpUntilIdle();
        var loaded = BenchWorld.Create(nodeCount: 64, withCodec: true);
        loaded.FillTerminals(4096);
        loaded.PumpUntilIdle();

        var pristineBytes = AllocationMeter.BytesPerOperation(() => pristine.Pump());
        var loadedBytes = AllocationMeter.BytesPerOperation(() => loaded.Pump());

        TestContext.Out.WriteLine(
            $"[allocation-gate] idle pump: pristine {pristineBytes} B/op, " +
            $"4096 retained terminals {loadedBytes} B/op");
        Assert.That(
            loadedBytes, Is.EqualTo(pristineBytes),
            "the quiescent pump must not allocate proportionally to retained state (plan L0)");
    }

    /// <summary>
    /// B3 interim: until the pump report becomes a value type, the quiescent
    /// pump allocates exactly the report instance and nothing else. The
    /// constant is the report's x64 object size; when the representation
    /// change lands, this gate becomes the zero assertion.
    /// </summary>
    [Test]
    public void IdlePumpAllocatesOnlyThePumpReport()
    {
        var world = BenchWorld.Create(nodeCount: 64, withCodec: true);
        world.PumpUntilIdle();

        var bytes = AllocationMeter.BytesPerOperation(() => world.Pump());

        TestContext.Out.WriteLine($"[allocation-gate] idle pump absolute: {bytes} B/op");
        Assert.That(bytes, Is.EqualTo(56), "one PumpReport instance, nothing else");
    }

    /// <summary>
    /// P1b (finding A2): a steady-state emit into a full trace ring is
    /// allocation-free — eviction is O(1) slot reuse and the gap marker carries
    /// its count as an integer — at the profile workload (the default 8192-entry
    /// ring). Before the fix this was ~64 KB per emit (a full rebuild of the
    /// retained ring plus the marker string).
    /// </summary>
    [Test]
    public void TraceRingEmitAtCapacityAllocatesZero()
    {
        var ring = new SignalRouter.V2.Kernel.KernelTraceRing(
            capacity: 8192, byteCapacity: 4 * 1024 * 1024);
        var semanticEvent = new SignalRouter.V2.Contracts.SemanticEvent(
            new SignalRouter.V2.Contracts.EventKind("StateTransition"),
            new SignalRouter.V2.Contracts.RuntimeIncarnationId("gate-incarnation"),
            SignalRouter.V2.Contracts.EventCausation.None,
            detailCode: "GateProbe");
        for (var i = 0; i < 8300; i++)
        {
            ring.Emit(semanticEvent);
        }

        var bytes = AllocationMeter.BytesPerOperation(() => ring.Emit(semanticEvent));

        TestContext.Out.WriteLine($"[allocation-gate] trace ring emit at capacity: {bytes} B/op");
        Assert.That(bytes, Is.EqualTo(0), "emission at capacity is eviction by slot reuse, never a rebuild");
    }

    /// <summary>
    /// P1d (finding A3), a proportionality gate (spec/performance.md §2): in a
    /// sampled-free world a revision advance materializes once per domain and
    /// pays only cheap per-wait evaluations on top, so 256 armed waits must
    /// cost less than twice one armed wait (measured ratio ~1.18; the per-wait
    /// re-materialization it guards against was ~96×).
    /// </summary>
    [Test]
    public void WaitReevaluationAllocationIsSharedAcrossWaitsOfADomain()
    {
        var single = BenchWorld.Create(nodeCount: 256, withCodec: true);
        single.ArmWaits(1);
        var many = BenchWorld.Create(nodeCount: 256, withCodec: true);
        many.ArmWaits(256);

        var singleBytes = AllocationMeter.BytesPerOperation(
            () =>
            {
                single.PublishInventory(5);
                single.PumpUntilIdle();
            },
            warmupIterations: 16,
            measuredIterations: 64);
        var manyBytes = AllocationMeter.BytesPerOperation(
            () =>
            {
                many.PublishInventory(5);
                many.PumpUntilIdle();
            },
            warmupIterations: 16,
            measuredIterations: 64);

        TestContext.Out.WriteLine(
            $"[allocation-gate] revision advance: 1 wait {singleBytes} B/op, 256 waits {manyBytes} B/op");
        Assert.That(
            manyBytes, Is.LessThan(2 * singleBytes),
            "the evaluation read is shared per domain; per-wait re-materialization would be ~96x");
    }
}
