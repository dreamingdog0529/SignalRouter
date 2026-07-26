using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// The StateStore-first coordinator seam (ADR 0011, observation-state.md §5.1):
/// honest degradation without the codec, expected-basis matching, structured cache
/// leases with diagnostic-never-fails-evidence eviction, and the exact after-basis
/// retained until the terminal evidence commits.
/// </summary>
public sealed class RecordObservationServicesTests
{
    private static readonly ViewContractRef RecordView =
        new(new ViewContractId("record-standard"), new ContractVersion(1, 0));

    private static readonly Principal Recorder =
        new(Principal.WellKnownKinds.TestHarness, "recorder");

    private static KernelFixture BuildWithRecordView(
        ICanonicalStateCodec? codec,
        long stateStoreMaxTotalBytes = 64L * 1024 * 1024,
        int stateStoreMaxBlobBytes = 1024 * 1024,
        IEvidenceCoordinator? coordinator = null)
    {
        var fixture = new KernelFixture(
            codec: codec,
            stateStoreMaxTotalBytes: stateStoreMaxTotalBytes,
            stateStoreMaxBlobBytes: stateStoreMaxBlobBytes,
            coordinator: coordinator,
            start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            RecordView, ViewFamily.Record, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Start(fixture.Executor);
        return fixture;
    }

    [Test]
    public void WithoutTheCodecTheSeamDegradesHonestly()
    {
        var fixture = BuildWithRecordView(codec: null);
        var services = fixture.Runtime.RecordObservation;

        Assert.That(services.CanAddress, Is.False);
        AssertEx.Throws<KernelFaultException>(() => services.TryMaterializeView(
            RecordView, "root", null, out _, out _));
    }

    [Test]
    public void MaterializationIsAddressedAndHonorsTheExpectedBasis()
    {
        var fixture = BuildWithRecordView(new TestCanonicalStateCodec());
        var services = fixture.Runtime.RecordObservation;
        Assert.That(services.CanAddress, Is.True);

        Assert.That(
            services.TryMaterializeView(RecordView, "root", null, out var first, out var mismatch),
            Is.True);
        Assert.That(mismatch, Is.False);
        Assert.That(first!.Snapshot.IsAddressed, Is.True);
        Assert.That(first.Canonical.Length, Is.GreaterThan(0));

        // The revision moves; the stale expected basis answers mismatch — never a
        // silently different-revision materialization.
        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();
        Assert.That(
            services.TryMaterializeView(
                RecordView, "root", first.Snapshot.Basis.Revision, out var stale, out var staleMismatch),
            Is.False);
        Assert.That(staleMismatch, Is.True);
        Assert.That(stale, Is.Null);

        Assert.That(
            services.TryMaterializeView(RecordView, "root", null, out var fresh, out _), Is.True);
        Assert.That(
            services.TryMaterializeView(
                RecordView, "root", fresh!.Snapshot.Basis.Revision, out var pinned, out _),
            Is.True);
        Assert.That(pinned!.Snapshot.Basis.Revision, Is.EqualTo(fresh.Snapshot.Basis.Revision));
    }

    [Test]
    public void AnUnregisteredOrAgentViewFailsFast()
    {
        var fixture = BuildWithRecordView(new TestCanonicalStateCodec());
        AssertEx.Throws<KernelFaultException>(() => fixture.Runtime.RecordObservation.TryMaterializeView(
            new ViewContractRef(new ViewContractId("nope"), new ContractVersion(1, 0)),
            "root", null, out _, out _));
    }

    [Test]
    public void LeasesAreStructuredAndIdempotent()
    {
        var fixture = BuildWithRecordView(new TestCanonicalStateCodec());
        var services = fixture.Runtime.RecordObservation;
        services.TryMaterializeView(RecordView, "root", null, out var materialization, out _);
        var recording = new OperationId("recording-1");

        Assert.That(services.TryLease(materialization!, recording), Is.EqualTo(LeaseAnswer.Retained));
        Assert.That(
            services.TryLease(materialization!, recording), Is.EqualTo(LeaseAnswer.Retained),
            "Put is idempotent by content");
        services.ReleaseRecording(recording);
    }

    [Test]
    public void AnOverBoundBlobIsRefusedStructurally()
    {
        var fixture = BuildWithRecordView(
            new TestCanonicalStateCodec(), stateStoreMaxBlobBytes: 8);
        var services = fixture.Runtime.RecordObservation;
        services.TryMaterializeView(RecordView, "root", null, out var materialization, out _);

        Assert.That(
            services.TryLease(materialization!, new OperationId("recording-1")),
            Is.EqualTo(LeaseAnswer.OverBlobBound));
    }

    [Test]
    public void DiagnosticRetentionNeverFailsAnEvidenceLease()
    {
        // Size the store so the timeline checkpoint and a new evidence blob cannot
        // coexist: the lease must evict the diagnostic pin and succeed
        // (observation-state.md §5.1).
        var fixture = BuildWithRecordView(
            new TestCanonicalStateCodec(), stateStoreMaxTotalBytes: 700);
        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueList<NodeAttribute>.From(new[]
            {
                new NodeAttribute(
                    "label", FieldValue.Of(new string('x', 400)), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();
        Assert.That(
            fixture.Runtime.Timeline.Snapshot(Recorder), Is.Not.Empty,
            "the checkpoint holds the store's budget");

        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();
        var services = fixture.Runtime.RecordObservation;
        services.TryMaterializeView(RecordView, "root", null, out var materialization, out _);

        Assert.That(
            services.TryLease(materialization!, new OperationId("recording-1")),
            Is.EqualTo(LeaseAnswer.Retained),
            "timeline pins release oldest-first before an evidence lease is refused");
        Assert.That(
            fixture.Runtime.Timeline.Snapshot(Recorder), Is.Empty,
            "the diagnostic entries were sacrificed for the evidence blob");
    }

    [Test]
    public void TheAfterBasisIsExactAndHeldUntilTheTerminalEvidenceCommits()
    {
        var coordinator = new ScriptedCoordinator { TerminalAnswer = EvidenceReadiness.Pending };
        var fixture = BuildWithRecordView(new TestCanonicalStateCodec(), coordinator: coordinator);
        fixture.Executor.OnExecute = _ => fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueList<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Saved"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.Submit("r1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(new CompletionEvidence(
            KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.Pump();

        var services = fixture.Runtime.RecordObservation;
        Assert.That(
            services.TryGetAfterMaterialization(new RequestId("r1"), out var after), Is.True,
            "the terminal evidence is Pending, so the exact after-basis stays held");
        var label = after!.Materialization.Nodes.Single(node => node.Key.Value == "save")
            .Attributes.Single(attribute => attribute.Name == "label");
        Assert.That(label.Value, Is.EqualTo(FieldValue.Of("Saved")));

        // Later mutations never disturb the retained basis.
        fixture.PublishInventory(9);
        fixture.Pump();
        services.TryGetAfterMaterialization(new RequestId("r1"), out var unchanged);
        Assert.That(unchanged!.Snapshot.Basis.Revision, Is.EqualTo(after.Snapshot.Basis.Revision));

        coordinator.TerminalAnswer = EvidenceReadiness.Ready;
        fixture.PumpUntilIdle();
        Assert.That(
            coordinator.Terminals.Single().AfterWatermark,
            Is.EqualTo(after.Snapshot.Basis.Revision),
            "the retained basis is exactly the E4 after-watermark");
        Assert.That(
            services.TryGetAfterMaterialization(new RequestId("r1"), out _), Is.False,
            "the commit releases the retained basis");
    }

    [Test]
    public void ZeroEffectTerminalsCarryAnAfterBasisToo()
    {
        // A precondition rejection never enters Observing, yet every E4 carries an
        // after record-view (guarantees.md §5.4, kernel-execution.md §5).
        var failingPrecondition = new PredicateDefinition(
            ValueList<PredicateClause>.From(new[]
            {
                new PredicateClause(new ClauseId("c0"), new ComparisonExpression(
                    new FieldPath("nodes/save/attributes/label"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of("NeverThisValue"))),
            }));
        var coordinator = new ScriptedCoordinator { TerminalAnswer = EvidenceReadiness.Pending };
        var fixture = new KernelFixture(
            codec: new TestCanonicalStateCodec(),
            invokePrecondition: failingPrecondition,
            coordinator: coordinator,
            start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            RecordView, ViewFamily.Record, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Start(fixture.Executor);

        fixture.Submit("r1");
        fixture.Pump();

        Assert.That(
            fixture.Runtime.RecordObservation.TryGetAfterMaterialization(
                new RequestId("r1"), out var after),
            Is.True);
        Assert.That(after!.Snapshot.IsAddressed, Is.True);
        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Rejected)));
    }
}
