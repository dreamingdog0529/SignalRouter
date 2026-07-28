using System;
using NUnit.Framework;

namespace SignalRouter.Contracts.Tests;

/// <summary>
/// recording-replay.md §5 / ADR 0015 — the declarative comparison-profile
/// content model the artifact embeds: constructor-validated, rule sets
/// ordinal-sorted and unique, vocabularies open with only the v2.0 codes
/// reserved.
/// </summary>
public sealed class ReplayComparisonProfileTests
{
    private static ReplayComparisonProfile Profile(
        ValueArray<ComparedNodeRule>? nodeRules = null,
        ValueArray<ItemKeyRule>? itemKeyRules = null,
        ValueArray<CollectionRule>? collectionRules = null,
        ValueArray<ExtensionPolicy>? extensions = null,
        ValueArray<ContractVersion>? projectable = null) =>
        new(
            TestData.ComparisonProfile,
            TestData.RecordView,
            "root",
            new RedactionPolicyId("default-redaction"),
            ReplayComparisonProfile.MatchByAuthorKey,
            nodeRules ?? ValueArray<ComparedNodeRule>.Empty,
            ValueArray<ComparedSourceRule>.Empty,
            itemKeyRules ?? ValueArray<ItemKeyRule>.Empty,
            collectionRules ?? ValueArray<CollectionRule>.Empty,
            ValueArray<NormalizationRule>.Empty,
            requireCompleteForScope: true,
            extensions ?? ValueArray<ExtensionPolicy>.Empty,
            projectable ?? ValueArray<ContractVersion>.Empty);

    [Test]
    public void AMinimalProfileIsDefaultStrict()
    {
        var profile = Profile();

        Assert.That(profile.NodeMatching, Is.EqualTo("AuthorKey"));
        Assert.That(profile.NodeRules.Count, Is.Zero, "empty rules mean every field compares");
        Assert.That(profile.RequireCompleteForScope, Is.True);
        Assert.That(profile.ProjectableFromVersions.Count, Is.Zero);
    }

    [Test]
    public void RuleSetsMustBeSortedAndUnique()
    {
        AssertEx.Throws<ArgumentException>(() => Profile(nodeRules:
            ValueArray<ComparedNodeRule>.From(new[]
            {
                new ComparedNodeRule("button", ValueArray<string>.From(new[] { "label" })),
                new ComparedNodeRule("button", ValueArray<string>.From(new[] { "value" })),
            })));
        AssertEx.Throws<ArgumentException>(() => Profile(collectionRules:
            ValueArray<CollectionRule>.From(new[]
            {
                new CollectionRule("nodes/list/items", CollectionComparison.Set),
                new CollectionRule("nodes/list/items", CollectionComparison.Ordered),
            })));
        AssertEx.Throws<ArgumentException>(() => Profile(extensions:
            ValueArray<ExtensionPolicy>.From(new[]
            {
                new ExtensionPolicy("ext-b", mandatory: false),
                new ExtensionPolicy("ext-a", mandatory: true),
            })));
        AssertEx.Throws<ArgumentException>(() => _ = new ComparedNodeRule(
            "button", ValueArray<string>.From(new[] { "value", "label" })));
        AssertEx.Throws<ArgumentException>(() => Profile(itemKeyRules:
            ValueArray<ItemKeyRule>.From(new[]
            {
                new ItemKeyRule("nodes/list/items", "key"),
                new ItemKeyRule("nodes/list/items", "id"),
            })));
    }

    [Test]
    public void RulePathsFollowTheFieldPathGrammar()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new CollectionRule(
            "/nodes/save", CollectionComparison.Ordered));
        AssertEx.Throws<ArgumentException>(() => _ = new ComparedNodeRule(
            "button", ValueArray<string>.From(new[] { "nodes//save" })));
        AssertEx.Throws<ArgumentException>(() => _ = new ItemKeyRule(
            "nodes/list/", "key"));
    }

    [Test]
    public void ProjectableVersionsMustBeStrictlyOlderAscendingUnique()
    {
        // The test profile reference is version 1.0 — nothing is older.
        AssertEx.Throws<ArgumentException>(() => Profile(projectable:
            ValueArray<ContractVersion>.From(new[] { new ContractVersion(1, 0) })));
        AssertEx.Throws<ArgumentException>(() => Profile(projectable:
            ValueArray<ContractVersion>.From(new[] { new ContractVersion(2, 0) })));
    }

    [Test]
    public void ComponentValidationFailsFast()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new ReplayComparisonProfile(
            default,
            TestData.RecordView,
            "root",
            new RedactionPolicyId("default-redaction"),
            ReplayComparisonProfile.MatchByAuthorKey,
            ValueArray<ComparedNodeRule>.Empty,
            ValueArray<ComparedSourceRule>.Empty,
            ValueArray<ItemKeyRule>.Empty,
            ValueArray<CollectionRule>.Empty,
            ValueArray<NormalizationRule>.Empty,
            true,
            ValueArray<ExtensionPolicy>.Empty,
            ValueArray<ContractVersion>.Empty));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new CollectionRule(
            "nodes/list/items", (CollectionComparison)7));
        Assert.That(NormalizationRule.Identity, Is.EqualTo("Identity"));
    }

    [Test]
    public void ADiffCarriesAtLeastOneRecordingSafeEntry()
    {
        var entry = new SemanticDiffEntry(
            "nodes/save/attributes/label", "ValueMismatch", "Save", "Store");
        var diff = new SemanticDiff(ValueArray<SemanticDiffEntry>.From(new[] { entry }));

        Assert.That(diff.Entries[0].Recorded, Is.EqualTo("Save"));
        AssertEx.Throws<ArgumentException>(() => _ = new SemanticDiff(
            ValueArray<SemanticDiffEntry>.Empty));
        AssertEx.Throws<ArgumentException>(() => _ = new SemanticDiffEntry(
            string.Empty, "ValueMismatch", "a", "b"));
    }
}
