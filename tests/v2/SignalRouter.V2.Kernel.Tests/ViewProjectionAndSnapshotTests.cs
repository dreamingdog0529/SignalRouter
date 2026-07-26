using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// observation-state.md §1–§4 — registered views project the full comparison
/// surface under family exposure rules; pinned snapshots are revision-consistent
/// and immune to later mutations; refusal is existence-concealing; teardown
/// answers every observer exactly once.
/// </summary>
public sealed class ViewProjectionAndSnapshotTests
{
    private static readonly ViewContractRef AgentView =
        new(new ViewContractId("agent-standard"), new ContractVersion(1, 0));

    private static readonly ViewContractRef RecordView =
        new(new ViewContractId("record-standard"), new ContractVersion(1, 0));

    private static KernelFixture BuildWithViews(
        int maxPinnedSnapshots = 32, string agentScope = "root")
    {
        var fixture = new KernelFixture(maxPinnedSnapshots: maxPinnedSnapshots, start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            AgentView, ViewFamily.Agent, agentScope,
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            RecordView, ViewFamily.Record, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        return fixture;
    }

    private static PinnedSnapshot Pin(
        KernelFixture fixture, ViewContractRef view, Principal principal, string scope = "root")
    {
        var observer = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(view, principal, scope, observer);
        fixture.PumpUntilIdle();
        Assert.That(observer.Refused, Is.Empty, "expected a pinned answer");
        return observer.Pinned.Single().Snapshot;
    }

    [Test]
    public void AnAgentSnapshotProjectsTheComparisonSurfacePostVisibility()
    {
        var fixture = BuildWithViews();
        fixture.Runtime.Start(fixture.Executor);
        fixture.PublishInventory(3);
        fixture.PumpUntilIdle();

        var snapshot = Pin(fixture, AgentView, KernelFixture.Agent);
        Assert.That(snapshot.Snapshot.IsAddressed, Is.False, "no codec: honestly unaddressed");
        Assert.That(snapshot.Snapshot.Completeness.IsComplete, Is.True);

        var save = snapshot.Materialization.Nodes.Single(node => node.Key.Value == "save");
        Assert.That(save.Role, Is.EqualTo(NodeRole.Button));
        Assert.That(save.Capabilities.Single().Contract, Is.EqualTo(KernelFixture.Invoke));
        Assert.That(save.Capabilities.Single().Available, Is.True);
        Assert.That(
            snapshot.Materialization.Nodes.Any(node => node.Key.Value == "secret"), Is.False,
            "a hidden node never appears in an agent materialization");
        Assert.That(
            snapshot.Lookup.Lookup(new FieldPath("nodes/secret/attributes/label")),
            Is.EqualTo(FieldLookup.OutOfScope));

        var inventory = snapshot.Materialization.Sources.Single();
        Assert.That(inventory.Fields.Single().Value, Is.EqualTo(FieldValue.Of(3L)));
        Assert.That(
            inventory.RedactedFieldNames, Is.EqualTo(new[] { "secret" }),
            "the sensitive source field is presence-without-content");
    }

    [Test]
    public void APinnedSnapshotIsImmuneToLaterMutations()
    {
        var fixture = BuildWithViews();
        fixture.Runtime.Start(fixture.Executor);
        fixture.PumpUntilIdle();

        var snapshot = Pin(fixture, AgentView, KernelFixture.Agent);
        var pinnedRevision = snapshot.Snapshot.Basis.Revision;

        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueList<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Changed"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();

        Assert.That(
            snapshot.Lookup.Lookup(new FieldPath("nodes/save/attributes/label")),
            Is.EqualTo(FieldLookup.Present(FieldValue.Of("Save"))),
            "every page of a pinned snapshot describes one revision (observation-state.md §4)");
        Assert.That(snapshot.Snapshot.Basis.Revision, Is.EqualTo(pinnedRevision));
    }

    [Test]
    public void RecordFamilyExposureIsIndependentOfAgentExposure()
    {
        var recorder = new Principal(Principal.WellKnownKinds.TestHarness, "recorder");
        var fixture = new KernelFixture(start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            AgentView, ViewFamily.Agent, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            RecordView, ViewFamily.Record, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Bootstrap.RegisterStateSource(new StateSourceRegistration(
            new StateSourceKey("record-only"),
            new StateSourceContractDescriptor(
                new StateSourceContractRef(
                    new StateSourceContractId("record-only"), new ContractVersion(1, 0)),
                ValueList<SourceFieldSchema>.From(new[]
                {
                    new SourceFieldSchema("flag", FieldType.Boolean, Sensitivity.Standard),
                }),
                agentVisible: false,
                recordVisible: true,
                maxDocumentBytes: 1024),
            StateSourceClass.RevisionBound));
        fixture.Runtime.Start(fixture.Executor);
        fixture.PumpUntilIdle();

        // The record-domain principal sees the record-visible source; the agent
        // snapshot never contains it (independent opt-ins, observation-state.md §7.2).
        var record = Pin(fixture, RecordView, recorder);
        Assert.That(
            record.Materialization.Sources.Any(source => source.Key.Value == "record-only"),
            Is.True);

        var agent = Pin(fixture, AgentView, KernelFixture.Agent);
        Assert.That(
            agent.Materialization.Sources.Any(source => source.Key.Value == "record-only"),
            Is.False);
        Assert.That(
            agent.Lookup.Lookup(new FieldPath("sources/record-only/flag")),
            Is.EqualTo(FieldLookup.OutOfScope),
            "hidden and unregistered sources answer identically");
    }

    [Test]
    public void RefusalIsExistenceConcealingAcrossAllThreeCauses()
    {
        var fixture = BuildWithViews();
        fixture.Runtime.Start(fixture.Executor);

        // Unbound principal; unregistered view; family/domain mismatch — one code.
        var unbound = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(
            AgentView, new Principal("UnknownKind", "nobody"), "root", unbound);
        var unregistered = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(
            new ViewContractRef(new ViewContractId("nope"), new ContractVersion(1, 0)),
            KernelFixture.Agent, "root", unregistered);
        var mismatch = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(RecordView, KernelFixture.Agent, "root", mismatch);
        fixture.PumpUntilIdle();

        Assert.That(unbound.Refused.Single().Reason, Is.EqualTo("ViewUnavailable"));
        Assert.That(unregistered.Refused.Single().Reason, Is.EqualTo("ViewUnavailable"));
        Assert.That(mismatch.Refused.Single().Reason, Is.EqualTo("ViewUnavailable"));
    }

    [Test]
    public void PinCapacityRefusesExplicitly()
    {
        var fixture = BuildWithViews(maxPinnedSnapshots: 1);
        fixture.Runtime.Start(fixture.Executor);

        Pin(fixture, AgentView, KernelFixture.Agent);
        var second = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(AgentView, KernelFixture.Agent, "root", second);
        fixture.PumpUntilIdle();
        Assert.That(second.Refused.Single().Reason, Is.EqualTo("CapacityExhausted"));
    }

    [Test]
    public void ReleasingAPinFreesItsSlot()
    {
        var fixture = BuildWithViews(maxPinnedSnapshots: 1);
        fixture.Runtime.Start(fixture.Executor);

        var observer = new RecordingSnapshotObserver();
        var operation = fixture.Runtime.Control.RequestSnapshot(
            AgentView, KernelFixture.Agent, "root", observer);
        fixture.PumpUntilIdle();
        Assert.That(observer.Pinned, Has.Count.EqualTo(1));

        fixture.Runtime.Control.ReleaseSnapshot(operation);
        fixture.PumpUntilIdle();
        Pin(fixture, AgentView, KernelFixture.Agent);
    }

    [Test]
    public void AScopedViewExcludesNodesOutsideItsSubtree()
    {
        var fixture = new KernelFixture(start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            AgentView, ViewFamily.Agent, "panel",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        var visibleToAll = new ExposurePolicy(ValueList<SecurityDomainId>.From(new[]
        {
            KernelFixture.AgentDomain, KernelFixture.HumanDomain, KernelFixture.RecordDomain,
        }));
        fixture.Runtime.Bootstrap.RegisterNode(new NodeRegistration(
            new AuthorKey("panel"), NodeRole.Container, parent: null,
            ValueList<NodeAttribute>.Empty, ValueList<CapabilityDeclaration>.Empty, visibleToAll));
        fixture.Runtime.Bootstrap.RegisterNode(new NodeRegistration(
            new AuthorKey("inside"), NodeRole.Button, new AuthorKey("panel"),
            ValueList<NodeAttribute>.Empty, ValueList<CapabilityDeclaration>.Empty, visibleToAll));
        fixture.Runtime.Start(fixture.Executor);

        var snapshot = Pin(fixture, AgentView, KernelFixture.Agent, scope: "panel");
        Assert.That(
            snapshot.Materialization.Nodes.Select(node => node.Key.Value),
            Is.EqualTo(new[] { "inside", "panel" }),
            "the subtree only — 'save' and 'secret' are outside the scope");
        Assert.That(
            snapshot.Lookup.Lookup(new FieldPath("nodes/save/attributes/label")),
            Is.EqualTo(FieldLookup.OutOfScope));
    }

    [Test]
    public void AParentOutsideTheMaterializationIsACompletenessConditionNotALeak()
    {
        // A visible child under a hidden parent: the parent link must neither
        // dangle nor reveal the hidden key (observation-state.md §3).
        var fixture = new KernelFixture(start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            AgentView, ViewFamily.Agent, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        var agentVisible = new ExposurePolicy(
            ValueList<SecurityDomainId>.From(new[] { KernelFixture.AgentDomain }));
        fixture.Runtime.Bootstrap.RegisterNode(new NodeRegistration(
            new AuthorKey("hidden-parent"), NodeRole.Container, parent: null,
            ValueList<NodeAttribute>.Empty, ValueList<CapabilityDeclaration>.Empty,
            ExposurePolicy.Hidden));
        fixture.Runtime.Bootstrap.RegisterNode(new NodeRegistration(
            new AuthorKey("orphaned"), NodeRole.Button, new AuthorKey("hidden-parent"),
            ValueList<NodeAttribute>.Empty, ValueList<CapabilityDeclaration>.Empty, agentVisible));
        fixture.Runtime.Start(fixture.Executor);

        var snapshot = Pin(fixture, AgentView, KernelFixture.Agent);
        var orphaned = snapshot.Materialization.Nodes.Single(node => node.Key.Value == "orphaned");
        Assert.That(orphaned.Parent, Is.Null);
        Assert.That(
            snapshot.Snapshot.Completeness.TryGetReason(
                new FieldPath("nodes/orphaned/parent"), out var reason),
            Is.True);
        Assert.That(reason, Is.EqualTo(CompletenessReason.OutOfScope));
    }

    [Test]
    public void TeardownAnswersEveryObserverExactlyOnce()
    {
        var fixture = BuildWithViews();
        fixture.Runtime.Start(fixture.Executor);
        fixture.Runtime.Control.TearDownIncarnation();
        var late = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(AgentView, KernelFixture.Agent, "root", late);
        fixture.PumpUntilIdle();

        Assert.That(late.Refused.Single().Reason, Is.EqualTo("TornDown"));
        Assert.That(late.Pinned, Is.Empty);
    }
}
