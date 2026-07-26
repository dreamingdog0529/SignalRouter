using System;
using System.Collections.Generic;
using System.Linq;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// A frozen, test-only reference model of the completeness-region semantics of
/// observation-state.md §3, transcribed directly from the spec text and built on
/// nothing but <see cref="string.Split(char[])"/> — deliberately independent of
/// the production <see cref="CompletenessMap"/> and <see cref="FieldPath"/>
/// helpers so it stays a valid oracle when their representation changes
/// (performance-track plan P0a/P3a). Spec rules transcribed:
///  - a region is a FieldPath prefix matched segment-wise (`nodes/save` covers
///    `nodes/save/attributes/label`, never `nodes/save2`);
///  - the longest matching prefix answers a path; regions without an entry are
///    complete; the empty map is complete;
///  - one slot is reserved for the root-region `BudgetTruncated` marker, into
///    which the deepest entries fold first when the bound would be exceeded,
///    ties resolved by ordinal order (ordinal-last folds first).
/// </summary>
internal sealed class CompletenessReferenceModel
{
    private readonly List<(string Region, CompletenessReason Reason)> entries;

    public bool RootTruncated { get; }

    private CompletenessReferenceModel(
        List<(string, CompletenessReason)> entries, bool rootTruncated)
    {
        this.entries = entries;
        RootTruncated = rootTruncated;
    }

    /// <summary>Mirrors the spec's fold algorithm over raw strings.</summary>
    public static CompletenessReferenceModel Build(
        IReadOnlyList<(string Region, CompletenessReason Reason)> input,
        int maxEntries,
        bool rootTruncated)
    {
        var unique = new SortedDictionary<string, CompletenessReason>(StringComparer.Ordinal);
        foreach (var (region, reason) in input)
        {
            unique.Add(region, reason); // duplicate regions are the caller's bug
        }

        var budget = rootTruncated ? maxEntries - 1 : maxEntries;
        if (unique.Count > budget)
        {
            rootTruncated = true;
            budget = maxEntries - 1;
            var foldOrder = unique.Keys
                .OrderByDescending(region => Segments(region).Length)
                .ThenByDescending(region => region, StringComparer.Ordinal)
                .ToList();
            foreach (var region in foldOrder)
            {
                if (unique.Count <= budget)
                {
                    break;
                }

                unique.Remove(region);
            }
        }

        return new CompletenessReferenceModel(
            unique.Select(pair => (pair.Key, pair.Value)).ToList(), rootTruncated);
    }

    public IReadOnlyList<(string Region, CompletenessReason Reason)> Entries => entries;

    /// <summary>Longest-prefix answer; the root marker answers everything else when present.</summary>
    public bool TryGetReason(string path, out CompletenessReason reason)
    {
        var pathSegments = Segments(path);
        var bestLength = -1;
        reason = default;
        foreach (var (region, entryReason) in entries)
        {
            var regionSegments = Segments(region);
            if (regionSegments.Length > bestLength && IsSegmentPrefix(regionSegments, pathSegments))
            {
                bestLength = regionSegments.Length;
                reason = entryReason;
            }
        }

        if (bestLength >= 0)
        {
            return true;
        }

        if (RootTruncated)
        {
            reason = CompletenessReason.BudgetTruncated;
            return true;
        }

        return false;
    }

    /// <summary>A subtree is complete only when no entry sits inside it and no ancestor entry covers it.</summary>
    public bool IsCompleteUnder(string regionPrefix)
    {
        if (RootTruncated)
        {
            return false;
        }

        var prefixSegments = Segments(regionPrefix);
        foreach (var (region, _) in entries)
        {
            var entrySegments = Segments(region);
            if (IsSegmentPrefix(prefixSegments, entrySegments) ||
                IsSegmentPrefix(entrySegments, prefixSegments))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Segments(string path) => path.Split('/');

    private static bool IsSegmentPrefix(string[] prefix, string[] path)
    {
        if (prefix.Length > path.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(prefix[i], path[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
