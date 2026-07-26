using System;
using System.Collections.Generic;

namespace SignalRouter.V2.Contracts
{
    /// <summary>One completeness region: a segment-wise FieldPath prefix and its reason (observation-state.md §3).</summary>
    public readonly struct CompletenessEntry : IEquatable<CompletenessEntry>
    {
        public CompletenessEntry(FieldPath region, CompletenessReason reason)
        {
            if (region.IsDefault)
            {
                throw new ArgumentException("A completeness entry requires a region.", nameof(region));
            }

            Region = region;
            Reason = reason;
        }

        public FieldPath Region { get; }

        public CompletenessReason Reason { get; }

        public bool IsDefault => Region.IsDefault;

        public bool Equals(CompletenessEntry other) =>
            Region.Equals(other.Region) && Reason == other.Reason;

        public override bool Equals(object? obj) => obj is CompletenessEntry other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(Region.GetHashCode(), (int)Reason);

        public override string ToString() => $"{Region}: {Reason}";

        public static bool operator ==(CompletenessEntry left, CompletenessEntry right) => left.Equals(right);

        public static bool operator !=(CompletenessEntry left, CompletenessEntry right) => !left.Equals(right);
    }

    /// <summary>
    /// The per-region completeness of one materialization (observation-state.md §3):
    /// a bounded, ordinally sorted set of `(regionPrefix, reason)` entries with the
    /// prefix as a unique key; the longest matching prefix answers a path; regions
    /// without an entry are complete. One slot is reserved for the root-region
    /// `BudgetTruncated` marker, into which the deepest entries fold
    /// deterministically when the bound would be exceeded — a completeness condition
    /// is never silently unrepresented.
    /// </summary>
    public sealed class CompletenessMap : IEquatable<CompletenessMap>
    {
        private CompletenessMap(ValueList<CompletenessEntry> entries, bool rootTruncated)
        {
            Entries = entries;
            RootTruncated = rootTruncated;
        }

        /// <summary>The complete materialization: no entries, no root truncation.</summary>
        public static CompletenessMap Complete { get; } =
            new CompletenessMap(ValueList<CompletenessEntry>.Empty, rootTruncated: false);

        public static CompletenessMap From(
            IReadOnlyList<CompletenessEntry> entries, int maxEntries, bool rootTruncated = false)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (maxEntries < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntries), "The entry bound is at least one.");
            }

            var byRegion = new SortedDictionary<string, CompletenessEntry>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry.IsDefault)
                {
                    throw new ArgumentException("Entries must be non-default.", nameof(entries));
                }

                if (byRegion.ContainsKey(entry.Region.Value))
                {
                    // The prefix is a unique key regardless of reason.
                    throw new ArgumentException(
                        $"Duplicate completeness region '{entry.Region}'.", nameof(entries));
                }

                byRegion.Add(entry.Region.Value, entry);
            }

            // One slot is reserved for the root marker; fold the deepest entries
            // (ties: ordinal-last) into it until the map fits.
            var budget = rootTruncated ? maxEntries - 1 : maxEntries;
            if (byRegion.Count > budget)
            {
                rootTruncated = true;
                budget = maxEntries - 1;
                var ordered = new List<CompletenessEntry>(byRegion.Values);
                ordered.Sort(DeepestFirstThenOrdinalLast);
                for (var i = 0; byRegion.Count > budget && i < ordered.Count; i++)
                {
                    byRegion.Remove(ordered[i].Region.Value);
                }
            }

            if (byRegion.Count == 0 && !rootTruncated)
            {
                return Complete;
            }

            var sorted = new List<CompletenessEntry>(byRegion.Values);
            return new CompletenessMap(ValueList<CompletenessEntry>.From(sorted), rootTruncated);
        }

        /// <summary>Specific regions, ordinally sorted by region path.</summary>
        public ValueList<CompletenessEntry> Entries { get; }

        /// <summary>The reserved root-region `BudgetTruncated` marker (covers every path).</summary>
        public bool RootTruncated { get; }

        public bool IsComplete => Entries.Count == 0 && !RootTruncated;

        /// <summary>Longest-prefix answer; falls back to the root marker when present.</summary>
        public bool TryGetReason(FieldPath path, out CompletenessReason reason)
        {
            if (path.IsDefault)
            {
                throw new ArgumentException("A non-default path is required.", nameof(path));
            }

            var bestLength = -1;
            reason = default;
            foreach (var entry in Entries)
            {
                if (IsSegmentPrefix(entry.Region, path) && entry.Region.Segments.Count > bestLength)
                {
                    bestLength = entry.Region.Segments.Count;
                    reason = entry.Reason;
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

        /// <summary>Whether every path under <paramref name="regionPrefix"/> is complete.</summary>
        public bool IsCompleteUnder(FieldPath regionPrefix)
        {
            if (regionPrefix.IsDefault)
            {
                throw new ArgumentException("A non-default prefix is required.", nameof(regionPrefix));
            }

            if (RootTruncated)
            {
                return false;
            }

            foreach (var entry in Entries)
            {
                // An entry inside the subtree, or an ancestor entry covering it,
                // makes the region incomplete.
                if (IsSegmentPrefix(regionPrefix, entry.Region) || IsSegmentPrefix(entry.Region, regionPrefix))
                {
                    return false;
                }
            }

            return true;
        }

        public bool Equals(CompletenessMap? other) =>
            other != null && RootTruncated == other.RootTruncated && Entries.Equals(other.Entries);

        public override bool Equals(object? obj) => Equals(obj as CompletenessMap);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(Entries.GetHashCode(), RootTruncated ? 1 : 0);

        public override string ToString() =>
            IsComplete ? "Complete" : $"{Entries.Count} regions{(RootTruncated ? " + root" : "")}";

        private static bool IsSegmentPrefix(FieldPath prefix, FieldPath path)
        {
            var prefixSegments = prefix.Segments;
            var pathSegments = path.Segments;
            if (prefixSegments.Count > pathSegments.Count)
            {
                return false;
            }

            for (var i = 0; i < prefixSegments.Count; i++)
            {
                if (!string.Equals(prefixSegments[i], pathSegments[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static int DeepestFirstThenOrdinalLast(CompletenessEntry left, CompletenessEntry right)
        {
            var byDepth = right.Region.Segments.Count.CompareTo(left.Region.Segments.Count);
            if (byDepth != 0)
            {
                return byDepth;
            }

            return string.CompareOrdinal(right.Region.Value, left.Region.Value);
        }
    }
}
