using NUnit.Framework;
using SignalRouter.V2.Benchmarks;

namespace SignalRouter.V2.Performance.Tests;

/// <summary>
/// Non-gating allocation characterization of the kernel hot paths (plan P0b):
/// each test measures bytes per operation on the kernel owner thread, prints
/// the number, and asserts only that the harness produced a measurement — the
/// current implementation allocates on every one of these paths by design
/// findings A1–A7/B1–B5, and gating starts only as the fixes land (per-PR
/// gates in P1, the consolidated idle-zero gate in P6). The printed numbers
/// are the working record; the committed baseline lives in bench/v2/BASELINE.md.
/// </summary>
[NonParallelizable]
[Category("AllocationCharacterization")]
public sealed class KernelAllocationCharacterizationTests
{
    [Test]
    public void IdlePump()
    {
        var world = BenchWorld.Create(nodeCount: 64, withCodec: true);
        world.PumpUntilIdle();

        var bytes = AllocationMeter.BytesPerOperation(() => world.Pump());

        Report(nameof(IdlePump), bytes);
        Assert.That(bytes, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void IdlePumpWithRetainedTerminals()
    {
        var world = BenchWorld.Create(nodeCount: 64, withCodec: true);
        world.FillTerminals(1024);
        world.PumpUntilIdle();

        var bytes = AllocationMeter.BytesPerOperation(() => world.Pump());

        // Plan acceptance criterion 5 (retained-state independence) will compare
        // this number against IdlePump; today it scales with retained terminals.
        Report(nameof(IdlePumpWithRetainedTerminals), bytes);
        Assert.That(bytes, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void SubmitToZeroEffectTerminal()
    {
        var world = BenchWorld.Create(nodeCount: 64, withCodec: true);
        world.PumpUntilIdle();

        var bytes = AllocationMeter.BytesPerOperation(
            () =>
            {
                world.SubmitOne();
                world.PumpUntilIdle();
            },
            warmupIterations: 32,
            measuredIterations: 128);

        Report(nameof(SubmitToZeroEffectTerminal), bytes);
        Assert.That(bytes, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void SnapshotPinAndRelease512Nodes()
    {
        var world = BenchWorld.Create(nodeCount: 512, withCodec: true);
        world.VerifySnapshotSucceeds(expectedNodes: 512);

        var bytes = AllocationMeter.BytesPerOperation(
            () =>
            {
                var operation = world.RequestSnapshot();
                world.PumpUntilIdle();
                world.ReleaseSnapshot(operation);
                world.PumpUntilIdle();
            },
            warmupIterations: 16,
            measuredIterations: 64);

        Report(nameof(SnapshotPinAndRelease512Nodes), bytes);
        Assert.That(bytes, Is.GreaterThanOrEqualTo(0));
    }

    private static void Report(string operation, long bytes) =>
        TestContext.Out.WriteLine(
            $"[allocation-characterization] {operation}: {bytes} B/op (non-gating; see bench/v2/BASELINE.md)");
}
