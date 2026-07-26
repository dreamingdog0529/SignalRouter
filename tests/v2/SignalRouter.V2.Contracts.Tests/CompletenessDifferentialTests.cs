using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// Differential tests: the production <see cref="CompletenessMap"/> must agree
/// with the frozen <see cref="CompletenessReferenceModel"/> (a spec transcription
/// that uses nothing but string.Split) over a generated corpus. This is the
/// oracle that lets the FieldPath/CompletenessMap representation change
/// (plan P3a: span segment enumeration, no per-access Split) while proving the
/// external semantics stayed fixed. The corpus generator is deterministic — a
/// fixed seed, no time, no environment — so every run checks the same corpus.
/// </summary>
public sealed class CompletenessDifferentialTests
{
    private static readonly string[] SegmentAlphabet =
    {
        "a", "b", "ab", "nodes", "save", "save2", "sources", "attributes", "label", "x",
    };

    private static readonly CompletenessReason[] Reasons =
        (CompletenessReason[])Enum.GetValues(typeof(CompletenessReason));

    [Test]
    public void ProductionAgreesWithTheFrozenReferenceOverTheGeneratedCorpus()
    {
        var random = new Random(20260726); // fixed seed: the corpus is part of the test
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var regions = GenerateDistinctRegions(random, count: random.Next(0, 9));
            var input = regions
                .Select(region => (Region: region, Reason: Reasons[random.Next(Reasons.Length)]))
                .ToArray();
            var maxEntries = random.Next(1, 8);
            var rootTruncated = random.Next(2) == 0;

            var reference = CompletenessReferenceModel.Build(input, maxEntries, rootTruncated);
            var production = CompletenessMap.From(
                input.Select(pair => new CompletenessEntry(new FieldPath(pair.Region), pair.Reason)).ToArray(),
                maxEntries,
                rootTruncated);

            AssertSameShape(reference, production, iteration);
            foreach (var query in GenerateQueries(random, regions))
            {
                AssertSameAnswers(reference, production, query, iteration);
            }
        }
    }

    [Test]
    public void SegmentBoundariesNeverMatchByStringPrefix()
    {
        // The spec's own example: `nodes/save` covers `nodes/save/attributes/label`,
        // never `nodes/save2` (observation-state.md §3).
        var map = CompletenessMap.From(
            new[] { new CompletenessEntry(new FieldPath("nodes/save"), CompletenessReason.Redacted) },
            maxEntries: 4);

        Assert.That(map.TryGetReason(new FieldPath("nodes/save/attributes/label"), out var reason), Is.True);
        Assert.That(reason, Is.EqualTo(CompletenessReason.Redacted));
        Assert.That(map.TryGetReason(new FieldPath("nodes/save2"), out _), Is.False);
        Assert.That(map.IsCompleteUnder(new FieldPath("nodes/save2")), Is.True);
        Assert.That(map.IsCompleteUnder(new FieldPath("nodes/save")), Is.False);
        Assert.That(map.IsCompleteUnder(new FieldPath("nodes")), Is.False, "an entry inside the subtree");
    }

    [Test]
    public void FoldingIsDeepestFirstWithOrdinalLastTies()
    {
        // Four entries into a bound of three: one slot goes to the root marker,
        // so two survive. The deepest ("a/b/c") folds first; the depth-2 tie
        // ("a/b" vs "a/a") folds ordinal-last first ("a/b").
        var map = CompletenessMap.From(
            new[]
            {
                new CompletenessEntry(new FieldPath("a/a"), CompletenessReason.Virtualized),
                new CompletenessEntry(new FieldPath("a/b"), CompletenessReason.Virtualized),
                new CompletenessEntry(new FieldPath("a/b/c"), CompletenessReason.Virtualized),
                new CompletenessEntry(new FieldPath("z"), CompletenessReason.Stale),
            },
            maxEntries: 3);

        Assert.That(map.RootTruncated, Is.True);
        Assert.That(
            map.Entries.Select(entry => entry.Region.Value).ToArray(),
            Is.EqualTo(new[] { "a/a", "z" }));
    }

    private static void AssertSameShape(
        CompletenessReferenceModel reference, CompletenessMap production, int iteration)
    {
        Assert.That(
            production.RootTruncated, Is.EqualTo(reference.RootTruncated),
            $"iteration {iteration}: rootTruncated diverged");
        Assert.That(
            production.Entries.Select(entry => (entry.Region.Value, entry.Reason)).ToArray(),
            Is.EqualTo(reference.Entries.Select(pair => (pair.Region, pair.Reason)).ToArray()),
            $"iteration {iteration}: surviving entries diverged");
    }

    private static void AssertSameAnswers(
        CompletenessReferenceModel reference, CompletenessMap production, string query, int iteration)
    {
        var referenceAnswered = reference.TryGetReason(query, out var referenceReason);
        var productionAnswered = production.TryGetReason(new FieldPath(query), out var productionReason);
        Assert.That(
            productionAnswered, Is.EqualTo(referenceAnswered),
            $"iteration {iteration}: TryGetReason presence diverged for '{query}'");
        if (referenceAnswered)
        {
            Assert.That(
                productionReason, Is.EqualTo(referenceReason),
                $"iteration {iteration}: TryGetReason reason diverged for '{query}'");
        }

        Assert.That(
            production.IsCompleteUnder(new FieldPath(query)),
            Is.EqualTo(reference.IsCompleteUnder(query)),
            $"iteration {iteration}: IsCompleteUnder diverged for '{query}'");
    }

    private static List<string> GenerateDistinctRegions(Random random, int count)
    {
        var regions = new HashSet<string>(StringComparer.Ordinal);
        while (regions.Count < count)
        {
            regions.Add(GeneratePath(random));
        }

        return regions.ToList();
    }

    private static IEnumerable<string> GenerateQueries(Random random, List<string> regions)
    {
        // Query at the regions themselves, below them, and at unrelated paths —
        // exactly where longest-prefix answers can diverge.
        foreach (var region in regions)
        {
            yield return region;
            yield return region + "/" + SegmentAlphabet[random.Next(SegmentAlphabet.Length)];
        }

        for (var i = 0; i < 8; i++)
        {
            yield return GeneratePath(random);
        }
    }

    private static string GeneratePath(Random random)
    {
        var depth = random.Next(1, 6);
        var segments = new string[depth];
        for (var i = 0; i < depth; i++)
        {
            segments[i] = SegmentAlphabet[random.Next(SegmentAlphabet.Length)];
        }

        return string.Join("/", segments);
    }
}
