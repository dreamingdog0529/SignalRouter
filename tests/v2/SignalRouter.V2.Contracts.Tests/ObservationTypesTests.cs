using System;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// observation-state.md §1–§2 — the descriptor's family constraints, the snapshot
/// identity tuple with its honest unaddressed form, and the canonical ordering the
/// materialization types enforce at construction.
/// </summary>
public sealed class ObservationTypesTests
{
    private static readonly ViewContractRef AgentView =
        new(new ViewContractId("agent-standard"), new ContractVersion(1, 0));

    private static ObservationBasis Basis() => new(
        new RuntimeIncarnationId("incarnation-1"),
        new SourceRevision(7),
        AgentView,
        new SecurityDomainId("agent-domain"),
        "root");

    [Test]
    public void ARecordFamilyViewMustExcludeKeylessNodes()
    {
        AssertEx.Throws<ArgumentException>(() => new ViewContractDescriptor(
            AgentView, ViewFamily.Record, "root",
            maxNodes: 16, maxFieldBytes: 256, includeKeylessNodes: true));
    }

    [Test]
    public void AnUnaddressedSnapshotIsHonestAboutItsMissingContentId()
    {
        var snapshot = new ObservationSnapshot(Basis(), default, CompletenessMap.Complete);
        Assert.That(snapshot.IsAddressed, Is.False);
        Assert.That(snapshot.ContentId.IsDefault, Is.True);

        var addressed = new ObservationSnapshot(
            Basis(),
            new ContentId("sha256", 1, DigestValue.From(new byte[] { 1, 2, 3 })),
            CompletenessMap.Complete);
        Assert.That(addressed.IsAddressed, Is.True);
        Assert.That(addressed, Is.Not.EqualTo(snapshot), "the ContentId leg participates in identity");
    }

    [Test]
    public void SnapshotEqualityIsTheWholeTuple()
    {
        var left = new ObservationSnapshot(Basis(), default, CompletenessMap.Complete);
        var right = new ObservationSnapshot(Basis(), default, CompletenessMap.Complete);
        Assert.That(left, Is.EqualTo(right));

        var incomplete = new ObservationSnapshot(
            Basis(), default,
            CompletenessMap.From(
                new[] { new CompletenessEntry(new FieldPath("nodes"), CompletenessReason.BudgetTruncated) },
                maxEntries: 4));
        Assert.That(left, Is.Not.EqualTo(incomplete), "completeness participates in identity");
    }

    [Test]
    public void MaterializedNodesSortAttributesAndCapabilitiesOrdinally()
    {
        var invoke = new CapabilityContractRef(new CapabilityContractId("Invoke"), new ContractVersion(1, 0));
        var abort = new CapabilityContractRef(new CapabilityContractId("Abort"), new ContractVersion(1, 0));
        var node = new MaterializedNode(
            new AuthorKey("save"),
            NodeRole.Button,
            parent: null,
            ValueArray<MaterializedAttribute>.From(new[]
            {
                new MaterializedAttribute("zeta", FieldValue.Of("z"), redacted: false),
                new MaterializedAttribute("alpha", FieldValue.Of("a"), redacted: false),
            }),
            ValueArray<MaterializedCapability>.From(new[]
            {
                new MaterializedCapability(invoke, available: true),
                new MaterializedCapability(abort, available: false),
            }),
            visibleChildCount: 0);

        Assert.That(node.Attributes[0].Name, Is.EqualTo("alpha"));
        Assert.That(node.Capabilities[0].Contract, Is.EqualTo(abort));
    }

    [Test]
    public void ARedactedAttributeMarksPresenceWithoutContent()
    {
        var redacted = new MaterializedAttribute("secret", default, redacted: true);
        Assert.That(redacted.Redacted, Is.True);
        AssertEx.Throws<ArgumentException>(
            () => new MaterializedAttribute("secret", FieldValue.Of("x"), redacted: true));
        AssertEx.Throws<ArgumentException>(
            () => new MaterializedAttribute("plain", default, redacted: false));
    }

    [Test]
    public void ASourceOmissionIsOneOfTheThreeSourceReasons()
    {
        var contract = new StateSourceContractRef(
            new StateSourceContractId("inventory"), new ContractVersion(1, 0));
        AssertEx.Throws<ArgumentException>(() => new MaterializedSource(
            new StateSourceKey("inventory"), contract,
            ValueArray<NamedField>.Empty, ValueArray<string>.Empty,
            CompletenessReason.Redacted));
        AssertEx.Throws<ArgumentException>(() => new MaterializedSource(
            new StateSourceKey("inventory"), contract,
            ValueArray<NamedField>.From(new[] { new NamedField("count", FieldValue.Of(1L)) }),
            ValueArray<string>.Empty,
            CompletenessReason.Stale));
    }

    [Test]
    public void SourceFieldsNormalizeOrdinallyRegardlessOfInputOrder()
    {
        // codex review: logically identical documents must materialize identically
        // — field order is publication noise, never observation identity.
        var contract = new StateSourceContractRef(
            new StateSourceContractId("inventory"), new ContractVersion(1, 0));
        var shuffled = new MaterializedSource(
            new StateSourceKey("inventory"), contract,
            ValueArray<NamedField>.From(new[]
            {
                new NamedField("zeta", FieldValue.Of(1L)),
                new NamedField("alpha", FieldValue.Of(2L)),
            }),
            ValueArray<string>.From(new[] { "z-secret", "a-secret" }),
            omission: null);
        var ordered = new MaterializedSource(
            new StateSourceKey("inventory"), contract,
            ValueArray<NamedField>.From(new[]
            {
                new NamedField("alpha", FieldValue.Of(2L)),
                new NamedField("zeta", FieldValue.Of(1L)),
            }),
            ValueArray<string>.From(new[] { "a-secret", "z-secret" }),
            omission: null);

        Assert.That(shuffled, Is.EqualTo(ordered));
        Assert.That(shuffled.Fields[0].Name, Is.EqualTo("alpha"));
        Assert.That(shuffled.RedactedFieldNames[0], Is.EqualTo("a-secret"));
    }

    [Test]
    public void TheMaterializationRejectsDuplicateKeysAndSortsMembers()
    {
        var node = new MaterializedNode(
            new AuthorKey("save"), NodeRole.Button, null,
            ValueArray<MaterializedAttribute>.Empty, ValueArray<MaterializedCapability>.Empty, 0);
        AssertEx.Throws<ArgumentException>(() => new ObservationMaterialization(
            Basis(),
            ValueArray<MaterializedNode>.From(new[] { node, node }),
            ValueArray<MaterializedSource>.Empty,
            CompletenessMap.Complete));

        var zebra = new MaterializedNode(
            new AuthorKey("zebra"), NodeRole.Button, null,
            ValueArray<MaterializedAttribute>.Empty, ValueArray<MaterializedCapability>.Empty, 0);
        var materialization = new ObservationMaterialization(
            Basis(),
            ValueArray<MaterializedNode>.From(new[] { zebra, node }),
            ValueArray<MaterializedSource>.Empty,
            CompletenessMap.Complete);
        Assert.That(materialization.Nodes[0].Key, Is.EqualTo(new AuthorKey("save")));
    }
}
