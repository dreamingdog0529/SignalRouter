using NUnit.Framework;
using SignalRouter.Benchmarks;

namespace SignalRouter.Performance.Tests;

/// <summary>
/// Gating allocation regressions — unlike the characterization tests, these
/// FAIL on regression. One gate lands with the fix that makes it true (plan
/// P1: per-fix gates; the consolidated quiescent-zero gate arrives with the
/// track's completion), so the suite only ever asserts properties the current
/// code actually has. Counters are deterministic on the kernel owner thread —
/// equality and absolute gates carry no tolerance — and every gate runs the
/// exact workload its PROFILE-default.md row claims (bench), so a green CI
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
        Assert.That(bytes, Is.EqualTo(0), "the report is a value type: the quiescent pump allocates nothing (L0)");
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
        var ring = new SignalRouter.Kernel.KernelTraceRing(
            capacity: 8192, byteCapacity: 4 * 1024 * 1024);
        var semanticEvent = new SignalRouter.Contracts.SemanticEvent(
            new SignalRouter.Contracts.EventKind("StateTransition"),
            new SignalRouter.Contracts.RuntimeIncarnationId("gate-incarnation"),
            SignalRouter.Contracts.EventCausation.None,
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
    /// P3c (finding B4): constructing aggregates from canonically-ordered
    /// immutable input keeps the input arrays — the only allocation is the
    /// aggregate object itself. Before the fix every construction re-copied and
    /// re-sorted its lists (~5 copies of the same data per materialization).
    /// </summary>
    [Test]
    public void AggregateConstructionFromSortedInputAllocatesOnlyTheObject()
    {
        var attributes = SignalRouter.Contracts.ValueArray<SignalRouter.Contracts.MaterializedAttribute>.From(new[]
        {
            new SignalRouter.Contracts.MaterializedAttribute(
                "label", SignalRouter.Contracts.FieldValue.Of("x"), redacted: false),
            new SignalRouter.Contracts.MaterializedAttribute(
                "value", SignalRouter.Contracts.FieldValue.Of(1L), redacted: false),
        });
        var capabilities = SignalRouter.Contracts.ValueArray<SignalRouter.Contracts.MaterializedCapability>.From(new[]
        {
            new SignalRouter.Contracts.MaterializedCapability(
                new SignalRouter.Contracts.CapabilityContractRef(
                    new SignalRouter.Contracts.CapabilityContractId("Invoke"),
                    new SignalRouter.Contracts.ContractVersion(1, 0)),
                available: true),
        });
        var key = new SignalRouter.Contracts.AuthorKey("n");
        var role = SignalRouter.Contracts.NodeRole.Button;
        object? sink = null;

        var bytes = AllocationMeter.BytesPerOperation(() =>
            sink = new SignalRouter.Contracts.MaterializedNode(
                key, role, null, attributes, capabilities, 0));

        TestContext.Out.WriteLine($"[allocation-gate] sorted-input node construction: {bytes} B/op (sink: {sink != null})");
        Assert.That(bytes, Is.EqualTo(72), "one MaterializedNode instance; the input arrays are kept, never re-copied");
    }

    /// <summary>
    /// P3a (finding B2): a materialization lookup — path parse, node/source
    /// binary search, attribute/field match, completeness longest-prefix — is
    /// allocation-free. Before the fix every lookup split the path (and every
    /// completeness consultation re-split it per entry).
    /// </summary>
    [Test]
    public void MaterializationLookupAllocatesZero()
    {
        var world = BenchWorld.Create(nodeCount: 512, withCodec: true);
        world.VerifySnapshotSucceeds(expectedNodes: 512);
        var observer = new CollectingSnapshotObserver();
        world.Runtime.Control.RequestSnapshot(
            BenchWorld.AgentView, BenchWorld.Agent, "root", observer);
        world.PumpUntilIdle();
        var lookup = observer.Last!.Lookup;
        var present = new SignalRouter.Contracts.FieldPath("nodes/node-00300/attributes/label");
        var missing = new SignalRouter.Contracts.FieldPath("nodes/never-registered/attributes/label");
        var sourceField = new SignalRouter.Contracts.FieldPath("sources/inventory/count");
        var children = new SignalRouter.Contracts.FieldPath("nodes/node-00300/children");

        // A separate materialization with populated completeness entries: the
        // longest-prefix loop must also be allocation-free, and the snapshot
        // world above is verified complete, so it never exercises that loop.
        var incompleteLookup = new SignalRouter.Contracts.MaterializationLookup(
            new SignalRouter.Contracts.ObservationMaterialization(
                new SignalRouter.Contracts.ObservationBasis(
                    new SignalRouter.Contracts.RuntimeIncarnationId("gate-incarnation"),
                    new SignalRouter.Contracts.SourceRevision(1),
                    BenchWorld.AgentView,
                    BenchWorld.AgentDomain,
                    "root"),
                SignalRouter.Contracts.ValueArray<SignalRouter.Contracts.MaterializedNode>.Empty,
                SignalRouter.Contracts.ValueArray<SignalRouter.Contracts.MaterializedSource>.Empty,
                SignalRouter.Contracts.CompletenessMap.From(
                    new[]
                    {
                        new SignalRouter.Contracts.CompletenessEntry(
                            new SignalRouter.Contracts.FieldPath("nodes/virtual"),
                            SignalRouter.Contracts.CompletenessReason.Virtualized),
                        new SignalRouter.Contracts.CompletenessEntry(
                            new SignalRouter.Contracts.FieldPath("nodes/virtual/attributes/label"),
                            SignalRouter.Contracts.CompletenessReason.Redacted),
                        new SignalRouter.Contracts.CompletenessEntry(
                            new SignalRouter.Contracts.FieldPath("sources/gone"),
                            SignalRouter.Contracts.CompletenessReason.SourceUnavailable),
                    },
                    maxEntries: 8)));
        var underRegion = new SignalRouter.Contracts.FieldPath("nodes/virtual/attributes/value");
        var noRegion = new SignalRouter.Contracts.FieldPath("nodes/other/attributes/value");

        var bytes = AllocationMeter.BytesPerOperation(() =>
        {
            lookup.Lookup(present);
            lookup.Lookup(missing);
            lookup.Lookup(sourceField);
            lookup.CountCollection(children);
            incompleteLookup.Lookup(underRegion);
            incompleteLookup.Lookup(noRegion);
        });

        TestContext.Out.WriteLine($"[allocation-gate] six lookups incl. completeness walk: {bytes} B/op");
        Assert.That(bytes, Is.EqualTo(0), "lookups parse by span and search sorted lists in place");
    }

    private sealed class CollectingSnapshotObserver : SignalRouter.Kernel.ISnapshotObserver
    {
        internal SignalRouter.Kernel.PinnedSnapshot? Last;

        public void OnPinned(
            SignalRouter.Contracts.OperationId operation,
            SignalRouter.Kernel.PinnedSnapshot snapshot) => Last = snapshot;

        public void OnRefused(SignalRouter.Contracts.OperationId operation, string reasonCode) =>
            throw new System.InvalidOperationException("Snapshot refused: " + reasonCode);
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
