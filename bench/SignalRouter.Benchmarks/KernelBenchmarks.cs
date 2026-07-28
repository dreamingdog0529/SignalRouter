using System.Globalization;
using BenchmarkDotNet.Attributes;
using SignalRouter.AdapterSdk;
using SignalRouter.Codec.CanonicalState;
using SignalRouter.Contracts;

namespace SignalRouter.Benchmarks;

// Macro benchmarks over whole kernel operations: MemoryDiagnoser is the
// essential diagnostic here (the performance track's first-class metric is
// allocated bytes per operation). DisassemblyDiagnoser is deliberately off for
// these — a Pump() dump spans half the kernel and explains nothing; it joins
// the micro benchmarks that accompany individual optimizations (plan P1+).

/// <summary>
/// The quiescent pump: no processable work, no due work. The performance spec
/// (plan D8) targets O(1) work and zero allocation here; the RetainedTerminals
/// axis exposes the current O(retained) behavior (PublishStatus rebuild +
/// ExpireTerminals scan on every pump, findings A1/A4).
/// </summary>
[MemoryDiagnoser]
public class IdlePumpBenchmarks
{
    [Params(0, 4096)]
    public int RetainedTerminals;

    private BenchWorld world = default!;

    [GlobalSetup]
    public void Setup()
    {
        world = BenchWorld.Create(nodeCount: 64, withCodec: true);
        world.FillTerminals(RetainedTerminals);
        world.PumpUntilIdle();
    }

    [Benchmark]
    public PumpReport IdlePump() => world.Pump();
}

/// <summary>
/// One admission -> zero-effect terminal round trip (executor refusal): the
/// canonicalizer, admission gates, state machine, terminate path, and status
/// publication. A fresh world per iteration keeps the RecoveryIndex small and
/// stable so the per-operation number does not drift with accumulation.
/// </summary>
[MemoryDiagnoser]
public class SubmitToTerminalBenchmarks
{
    private BenchWorld world = default!;

    [IterationSetup]
    public void IterationSetup()
    {
        world = BenchWorld.Create(nodeCount: 64, withCodec: true);
        // Settle the initial checkpoint (bootstrap revisions materialize and
        // encode on the first pumps) so the measured window contains only
        // submission work.
        world.PumpUntilIdle();
    }

    [Benchmark(OperationsPerInvoke = 512)]
    public void SubmitToTerminal()
    {
        for (var i = 0; i < 512; i++)
        {
            world.SubmitOne();
            world.PumpUntilIdle();
        }
    }
}

/// <summary>
/// Pin one revision-consistent snapshot and release it: the full
/// materialization path (projection, sorting, completeness, lookup
/// construction) plus canonical encoding and StateStore lease when the codec
/// is present. The busy-path headline number (plan acceptance criterion 1).
/// </summary>
[MemoryDiagnoser]
public class SnapshotBenchmarks
{
    [Params(64, 512, 2048)]
    public int Nodes;

    [Params(true, false)]
    public bool WithCodec;

    private BenchWorld world = default!;

    [GlobalSetup]
    public void Setup()
    {
        world = BenchWorld.Create(Nodes, WithCodec);
        world.VerifySnapshotSucceeds(Nodes);
    }

    [Benchmark]
    public void PinAndRelease()
    {
        var operation = world.RequestSnapshot();
        world.PumpUntilIdle();
        world.ReleaseSnapshot(operation);
        world.PumpUntilIdle();
    }
}

/// <summary>
/// The canonical-state codec alone (no kernel): representation v1 encoding of
/// an N-node materialization (plan acceptance criterion 2).
/// </summary>
[MemoryDiagnoser]
public class CanonicalEncodeBenchmarks
{
    [Params(64, 512, 2048)]
    public int Nodes;

    private readonly CanonicalStateCodec codec = new();

    private ObservationMaterialization materialization = default!;

    [GlobalSetup]
    public void Setup()
    {
        var nodes = new MaterializedNode[Nodes];
        for (var i = 0; i < Nodes; i++)
        {
            var ordinal = i.ToString("D5", CultureInfo.InvariantCulture);
            nodes[i] = new MaterializedNode(
                new AuthorKey("node-" + ordinal),
                NodeRole.Button,
                parent: null,
                ValueArray<MaterializedAttribute>.From(new[]
                {
                    new MaterializedAttribute("label", FieldValue.Of("Label " + ordinal), redacted: false),
                    new MaterializedAttribute("value", FieldValue.Of((long)i), redacted: false),
                }),
                ValueArray<MaterializedCapability>.From(new[]
                {
                    new MaterializedCapability(BenchWorld.Invoke, available: true),
                }),
                visibleChildCount: 0);
        }

        materialization = new ObservationMaterialization(
            new ObservationBasis(
                new RuntimeIncarnationId("bench-incarnation"),
                new SourceRevision(7),
                BenchWorld.AgentView,
                BenchWorld.AgentDomain,
                "root"),
            ValueArray<MaterializedNode>.From(nodes),
            ValueArray<MaterializedSource>.From(new[]
            {
                new MaterializedSource(
                    new StateSourceKey("inventory"),
                    new StateSourceContractRef(
                        new StateSourceContractId("inventory"), new ContractVersion(1, 0)),
                    ValueArray<NamedField>.From(new[] { new NamedField("count", FieldValue.Of(5L)) }),
                    ValueArray<string>.Empty,
                    omission: null),
            }),
            CompletenessMap.Complete);
    }

    [Benchmark]
    public CanonicalStateResult Encode() => codec.Encode(materialization);
}

/// <summary>
/// A revision advance with armed waits: today every armed wait triggers a full
/// node-store materialization after the turn (finding A3); the target is one
/// revision-bound materialization per domain with the sampled overlay
/// regenerated per read (plan D4, acceptance criterion 3).
/// </summary>
[MemoryDiagnoser]
public class WaitReevaluationBenchmarks
{
    [Params(1, 256)]
    public int ArmedWaits;

    private BenchWorld world = default!;

    [GlobalSetup]
    public void Setup()
    {
        world = BenchWorld.Create(nodeCount: 256, withCodec: true);
        world.ArmWaits(ArmedWaits);
    }

    [Benchmark]
    public void PublishAndReevaluate()
    {
        world.PublishInventory(5);
        world.PumpUntilIdle();
    }
}
