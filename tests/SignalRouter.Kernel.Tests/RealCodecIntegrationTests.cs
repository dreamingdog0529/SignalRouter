using System.Linq;
using NUnit.Framework;
using SignalRouter.Codec.CanonicalState;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel.Tests;

/// <summary>
/// The production codec through the kernel seam: snapshots address as
/// "sha256"@1, timeline blobs verify, and — the ADR 0012 temporal-leg exclusion
/// at work — an unchanged visible state re-addresses to the same ContentId across
/// revision advances, so the idempotent StateStore put and timeline pin counting
/// carry multiple entries over one blob.
/// </summary>
public sealed class RealCodecIntegrationTests
{
    private static readonly ViewContractRef AgentView =
        new(new ViewContractId("agent-standard"), new ContractVersion(1, 0));

    private static readonly Principal Recorder =
        new(Principal.WellKnownKinds.TestHarness, "recorder");

    [Test]
    public void SnapshotsAddressAndVerifyUnderTheProductionCodec()
    {
        var fixture = new KernelFixture(codec: new CanonicalStateCodec(), start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            AgentView, ViewFamily.Agent, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Start(fixture.Executor);
        fixture.PublishInventory(3);
        fixture.PumpUntilIdle();

        var observer = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(AgentView, KernelFixture.Agent, "root", observer);
        fixture.PumpUntilIdle();

        var snapshot = observer.Pinned.Single().Snapshot;
        Assert.That(snapshot.Snapshot.IsAddressed, Is.True);
        Assert.That(snapshot.Snapshot.ContentId.DigestAlgorithmId, Is.EqualTo("sha256"));
        Assert.That(snapshot.Snapshot.ContentId.CanonicalRepresentationVersion, Is.EqualTo(1));

        var reencoded = new CanonicalStateCodec().Encode(snapshot.Materialization);
        Assert.That(reencoded.Id, Is.EqualTo(snapshot.Snapshot.ContentId));
        Assert.That(
            CanonicalStateCodec.Verify(snapshot.Snapshot.ContentId, reencoded.CopyPayload()),
            Is.True, "verify-before-use holds for the kernel-produced address");
    }

    [Test]
    public void AnUnchangedStateReaddressesToTheSameBlobAcrossRevisions()
    {
        var fixture = new KernelFixture(codec: new CanonicalStateCodec());
        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();
        var first = fixture.Runtime.Timeline.Snapshot(Recorder)[^1];

        // The same document re-published: the revision advances, the visible state
        // does not — the checkpoint re-addresses to the same ContentId.
        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();
        var entries = fixture.Runtime.Timeline.Snapshot(Recorder);
        var second = entries[^1];

        Assert.That(second.Revision.Value, Is.GreaterThan(first.Revision.Value));
        Assert.That(
            second.ContentId, Is.EqualTo(first.ContentId),
            "temporal legs never influence the ContentId (ADR 0012 / guarantees §5.3 reuse)");
        Assert.That(
            entries.Count(entry => entry.ContentId.Equals(first.ContentId)),
            Is.GreaterThanOrEqualTo(2),
            "both timeline entries reference the one deduplicated blob");

        // A genuinely different state re-addresses differently.
        fixture.PublishInventory(2);
        fixture.PumpUntilIdle();
        Assert.That(
            fixture.Runtime.Timeline.Snapshot(Recorder)[^1].ContentId,
            Is.Not.EqualTo(first.ContentId));
    }

    [Test]
    public void TheRecordSeamProducesVerifiableMaterializations()
    {
        var recordView = new ViewContractRef(
            new ViewContractId("record-standard"), new ContractVersion(1, 0));
        var fixture = new KernelFixture(codec: new CanonicalStateCodec(), start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            recordView, ViewFamily.Record, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Start(fixture.Executor);
        fixture.PumpUntilIdle();

        var services = fixture.Runtime.RecordObservation;
        Assert.That(services.CanAddress, Is.True);
        Assert.That(
            services.TryMaterializeView(recordView, "root", null, out var materialization, out _),
            Is.True);
        Assert.That(
            CanonicalStateCodec.Verify(
                materialization!.Snapshot.ContentId, materialization.Canonical.CopyPayload()),
            Is.True);
        Assert.That(
            new CanonicalStateCodec().Decode(
                materialization.Canonical.CopyPayload(),
                materialization.Snapshot.Basis.Incarnation,
                materialization.Snapshot.Basis.Revision).Basis,
            Is.EqualTo(materialization.Snapshot.Basis),
            "the round trip through the seam reconstructs the exact basis");
    }
}
