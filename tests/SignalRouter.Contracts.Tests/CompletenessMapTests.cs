using System.Linq;
using NUnit.Framework;

namespace SignalRouter.Contracts.Tests;

/// <summary>
/// observation-state.md §3 — regions are segment-wise FieldPath prefixes with the
/// prefix as a unique key; longest prefix answers; bounded coalescing folds the
/// deepest entries into the reserved root marker and never exceeds the bound.
/// </summary>
public sealed class CompletenessMapTests
{
    private static CompletenessEntry Entry(string region, CompletenessReason reason) =>
        new(new FieldPath(region), reason);

    [Test]
    public void TheEmptyMapIsCompleteAndAnswersNothing()
    {
        Assert.That(CompletenessMap.Complete.IsComplete, Is.True);
        Assert.That(
            CompletenessMap.Complete.TryGetReason(new FieldPath("nodes/save/attributes/label"), out _),
            Is.False);
        Assert.That(
            CompletenessMap.Complete.IsCompleteUnder(new FieldPath("nodes")), Is.True);
    }

    [Test]
    public void PrefixMatchingIsSegmentWiseNeverTextual()
    {
        var map = CompletenessMap.From(
            new[] { Entry("nodes/save", CompletenessReason.Virtualized) }, maxEntries: 16);

        Assert.That(
            map.TryGetReason(new FieldPath("nodes/save/attributes/label"), out var reason), Is.True);
        Assert.That(reason, Is.EqualTo(CompletenessReason.Virtualized));
        Assert.That(
            map.TryGetReason(new FieldPath("nodes/save2/attributes/label"), out _), Is.False,
            "'nodes/save' must never cover 'nodes/save2' — matching is per segment");
    }

    [Test]
    public void TheLongestMatchingPrefixWins()
    {
        var map = CompletenessMap.From(
            new[]
            {
                Entry("nodes", CompletenessReason.Virtualized),
                Entry("nodes/save", CompletenessReason.Redacted),
            },
            maxEntries: 16);

        Assert.That(map.TryGetReason(new FieldPath("nodes/save/attributes/label"), out var deep), Is.True);
        Assert.That(deep, Is.EqualTo(CompletenessReason.Redacted));
        Assert.That(map.TryGetReason(new FieldPath("nodes/other"), out var shallow), Is.True);
        Assert.That(shallow, Is.EqualTo(CompletenessReason.Virtualized));
    }

    [Test]
    public void ThePrefixIsAUniqueKeyRegardlessOfReason()
    {
        AssertEx.Throws<System.ArgumentException>(() => CompletenessMap.From(
            new[]
            {
                Entry("nodes/save", CompletenessReason.Virtualized),
                Entry("nodes/save", CompletenessReason.Redacted),
            },
            maxEntries: 16));
    }

    [Test]
    public void EntriesAreOrdinallySortedRegardlessOfInputOrder()
    {
        var map = CompletenessMap.From(
            new[]
            {
                Entry("sources/inventory", CompletenessReason.Stale),
                Entry("nodes/save", CompletenessReason.Virtualized),
            },
            maxEntries: 16);

        Assert.That(
            map.Entries.Select(entry => entry.Region.Value),
            Is.EqualTo(new[] { "nodes/save", "sources/inventory" }));
    }

    [Test]
    public void CoalescingFoldsDeepestFirstAndNeverExceedsTheBound()
    {
        var map = CompletenessMap.From(
            new[]
            {
                Entry("nodes/a", CompletenessReason.Virtualized),
                Entry("nodes/b/attributes/x", CompletenessReason.BudgetTruncated),
                Entry("nodes/c/attributes/y", CompletenessReason.BudgetTruncated),
                Entry("sources/inventory", CompletenessReason.Stale),
            },
            maxEntries: 3);

        // One slot is reserved for the root marker; the two deepest entries fold.
        Assert.That(map.RootTruncated, Is.True);
        Assert.That(map.Entries.Count, Is.LessThanOrEqualTo(2));
        Assert.That(
            map.Entries.Select(entry => entry.Region.Value),
            Is.EqualTo(new[] { "nodes/a", "sources/inventory" }));

        // The folded conditions still answer through the root marker.
        Assert.That(
            map.TryGetReason(new FieldPath("nodes/b/attributes/x"), out var folded), Is.True);
        Assert.That(folded, Is.EqualTo(CompletenessReason.BudgetTruncated));
        Assert.That(map.IsComplete, Is.False);
    }

    [Test]
    public void RootTruncationAnswersEveryUnlistedPath()
    {
        var map = CompletenessMap.From(
            new[] { Entry("sources/inventory", CompletenessReason.Stale) },
            maxEntries: 16,
            rootTruncated: true);

        Assert.That(map.TryGetReason(new FieldPath("nodes/anything"), out var reason), Is.True);
        Assert.That(reason, Is.EqualTo(CompletenessReason.BudgetTruncated));
        Assert.That(map.TryGetReason(new FieldPath("sources/inventory/count"), out var specific), Is.True);
        Assert.That(specific, Is.EqualTo(CompletenessReason.Stale), "the specific entry still wins");
        Assert.That(map.IsCompleteUnder(new FieldPath("nodes")), Is.False);
    }

    [Test]
    public void IsCompleteUnderSeesBothAncestorsAndDescendants()
    {
        var map = CompletenessMap.From(
            new[] { Entry("nodes/save/attributes/label", CompletenessReason.BudgetTruncated) },
            maxEntries: 16);

        Assert.That(map.IsCompleteUnder(new FieldPath("nodes/save")), Is.False, "a descendant entry");
        Assert.That(
            map.IsCompleteUnder(new FieldPath("nodes/save/attributes/label")), Is.False,
            "the entry itself");
        Assert.That(map.IsCompleteUnder(new FieldPath("nodes/other")), Is.True);
    }
}
