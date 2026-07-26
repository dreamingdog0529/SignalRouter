using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// The pure lookup over one materialization — same basis, same answers, and the
/// four comparator inputs (absent / null / unknown / redacted) stay distinct
/// (recording-replay.md §5.2, observation-state.md §3).
/// </summary>
public sealed class MaterializationLookupTests
{
    private static readonly ViewContractRef View =
        new(new ViewContractId("agent-standard"), new ContractVersion(1, 0));

    private static MaterializationLookup Build(
        CompletenessMap? completeness = null,
        CompletenessReason? sourceOmission = null)
    {
        var basis = new ObservationBasis(
            new RuntimeIncarnationId("incarnation-1"),
            new SourceRevision(3),
            View,
            new SecurityDomainId("agent-domain"),
            "root");
        var node = new MaterializedNode(
            new AuthorKey("save"),
            NodeRole.Button,
            parent: null,
            ValueList<MaterializedAttribute>.From(new[]
            {
                new MaterializedAttribute("label", FieldValue.Of("Save"), redacted: false),
                new MaterializedAttribute("nullable", FieldValue.Null, redacted: false),
                new MaterializedAttribute("secret", default, redacted: true),
            }),
            ValueList<MaterializedCapability>.Empty,
            visibleChildCount: 2);
        var source = new MaterializedSource(
            new StateSourceKey("inventory"),
            new StateSourceContractRef(new StateSourceContractId("inventory"), new ContractVersion(1, 0)),
            sourceOmission.HasValue
                ? ValueList<NamedField>.Empty
                : ValueList<NamedField>.From(new[] { new NamedField("count", FieldValue.Of(5L)) }),
            ValueList<string>.From(new[] { "secretField" }),
            sourceOmission);
        return new MaterializationLookup(new ObservationMaterialization(
            basis,
            ValueList<MaterializedNode>.From(new[] { node }),
            ValueList<MaterializedSource>.From(new[] { source }),
            completeness ?? CompletenessMap.Complete));
    }

    [Test]
    public void TheFourComparatorInputsStayDistinct()
    {
        var lookup = Build();

        // Present with a value; present-and-null; absent; redacted — four answers.
        Assert.That(
            lookup.Lookup(new FieldPath("nodes/save/attributes/label")),
            Is.EqualTo(FieldLookup.Present(FieldValue.Of("Save"))));
        Assert.That(
            lookup.Lookup(new FieldPath("nodes/save/attributes/nullable")),
            Is.EqualTo(FieldLookup.Present(FieldValue.Null)));
        Assert.That(
            lookup.Lookup(new FieldPath("nodes/save/attributes/missing")),
            Is.EqualTo(FieldLookup.Absent));
        Assert.That(
            lookup.Lookup(new FieldPath("nodes/save/attributes/secret")),
            Is.EqualTo(FieldLookup.Redacted));
    }

    [Test]
    public void UnmaterializedNodesAnswerOutOfScopeWithoutACompletenessEntry()
    {
        var lookup = Build();
        Assert.That(
            lookup.Lookup(new FieldPath("nodes/hidden/attributes/label")),
            Is.EqualTo(FieldLookup.OutOfScope),
            "unregistered, hidden, and out-of-scope nodes answer identically");
        Assert.That(
            lookup.CountCollection(new FieldPath("nodes/hidden/children")),
            Is.EqualTo(CollectionCountLookup.OutOfScope));
    }

    [Test]
    public void ATruncatedRegionAnswersItsReasonNeverAFabricatedAbsence()
    {
        var truncated = Build(CompletenessMap.From(
            new[]
            {
                new CompletenessEntry(new FieldPath("nodes/cut"), CompletenessReason.BudgetTruncated),
            },
            maxEntries: 8));

        Assert.That(
            truncated.Lookup(new FieldPath("nodes/cut/attributes/label")),
            Is.EqualTo(FieldLookup.Incomplete(CompletenessReason.BudgetTruncated)));
        Assert.That(
            truncated.CountCollection(new FieldPath("nodes/cut/children")),
            Is.EqualTo(CollectionCountLookup.Incomplete(CompletenessReason.BudgetTruncated)));
        Assert.That(
            truncated.Lookup(new FieldPath("nodes/other/attributes/label")),
            Is.EqualTo(FieldLookup.OutOfScope),
            "regions without an entry keep their default answer");
    }

    [Test]
    public void SourceAnswersFollowTheSchemaAndOmission()
    {
        var lookup = Build();
        Assert.That(
            lookup.Lookup(new FieldPath("sources/inventory/count")),
            Is.EqualTo(FieldLookup.Present(FieldValue.Of(5L))));
        Assert.That(
            lookup.Lookup(new FieldPath("sources/inventory/secretField")),
            Is.EqualTo(FieldLookup.Redacted),
            "a declared sensitive field answers Redacted even when unpublished");
        Assert.That(
            lookup.Lookup(new FieldPath("sources/inventory/undeclared")),
            Is.EqualTo(FieldLookup.Absent));
        Assert.That(
            lookup.Lookup(new FieldPath("sources/unknown/count")),
            Is.EqualTo(FieldLookup.OutOfScope));

        var stale = Build(sourceOmission: CompletenessReason.Stale);
        Assert.That(
            stale.Lookup(new FieldPath("sources/inventory/count")),
            Is.EqualTo(FieldLookup.Incomplete(CompletenessReason.Stale)));
    }

    [Test]
    public void ChildCountsAnswerFromTheMaterializedNode()
    {
        var lookup = Build();
        Assert.That(
            lookup.CountCollection(new FieldPath("nodes/save/children")),
            Is.EqualTo(CollectionCountLookup.Present(2)));
    }

    [Test]
    public void TheMirrorToUnevaluableStaysSingleSourced()
    {
        // guarantees.md §3.5: the Unevaluable vocabulary deliberately mirrors the
        // CompletenessMap reasons; the collapse runs through the one mapping.
        Assert.That(
            FieldLookup.Incomplete(CompletenessReason.BudgetTruncated).ToUnevaluable(),
            Is.EqualTo(CompletenessReasons.ToUnevaluable(CompletenessReason.BudgetTruncated)));
        Assert.That(
            FieldLookup.Incomplete(CompletenessReason.Virtualized).ToUnevaluable(),
            Is.EqualTo(UnevaluableReason.Incompleteness));
        Assert.That(
            FieldLookup.Incomplete(CompletenessReason.Stale).ToUnevaluable(),
            Is.EqualTo(UnevaluableReason.Stale));
    }
}
