using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The pure, snapshot-local <see cref="IObservationLookup"/> over one
    /// <see cref="ObservationMaterialization"/> — same basis, same answers. It lives
    /// in Contracts deliberately: the kernel's pinned evaluation reads, replay
    /// comparison over decoded blobs, and E8 re-evaluation all answer through this
    /// one implementation, so "the materialization is the observation" holds by
    /// construction.
    ///
    /// Path grammar (verification.md §2.2): <c>nodes/&lt;authorKey&gt;/attributes/&lt;name&gt;</c>,
    /// <c>nodes/&lt;authorKey&gt;/children</c> (keyed collection), and
    /// <c>sources/&lt;key&gt;/&lt;field&gt;</c>. A path whose target is not in the
    /// materialization consults the completeness map (longest prefix) before
    /// answering its default — an unmaterialized region answers `Incomplete`, never
    /// a fabricated `Absent`/`OutOfScope`.
    /// </summary>
    public sealed class MaterializationLookup : IObservationLookup
    {
        private readonly ObservationMaterialization materialization;

        public MaterializationLookup(ObservationMaterialization materialization)
        {
            this.materialization = materialization ?? throw new ArgumentNullException(nameof(materialization));
        }

        public ObservationBasis Basis => materialization.Basis;

        public FieldLookup Lookup(FieldPath path)
        {
            // Span-parsed path shapes — no splitting, no allocation on a lookup.
            var remaining = path.Value.AsSpan();
            var first = NextSegment(ref remaining);
            if (first.SequenceEqual("nodes".AsSpan()))
            {
                var key = NextSegment(ref remaining);
                var kind = NextSegment(ref remaining);
                var name = NextSegment(ref remaining);
                if (remaining.Length == 0 && !name.IsEmpty && kind.SequenceEqual("attributes".AsSpan()))
                {
                    if (!TryFindNode(key, out var node))
                    {
                        // Unregistered, hidden, and out-of-scope nodes answer
                        // identically; a truncated region answers its reason.
                        return IncompleteOr(path, FieldLookup.OutOfScope);
                    }

                    // Indexed: a ValueList foreach boxes its enumerator (B1).
                    var attributes = node.Attributes;
                    for (var i = 0; i < attributes.Count; i++)
                    {
                        var attribute = attributes[i];
                        if (attribute.Name.AsSpan().SequenceEqual(name))
                        {
                            return attribute.Redacted
                                ? FieldLookup.Redacted
                                : FieldLookup.Present(attribute.Value);
                        }
                    }

                    return IncompleteOr(path, FieldLookup.Absent);
                }

                return FieldLookup.OutOfScope;
            }

            if (first.SequenceEqual("sources".AsSpan()))
            {
                var sourceKey = NextSegment(ref remaining);
                var fieldName = NextSegment(ref remaining);
                if (remaining.Length == 0 && !fieldName.IsEmpty)
                {
                    return LookupSource(path, sourceKey, fieldName);
                }
            }

            return FieldLookup.OutOfScope;
        }

        public CollectionCountLookup CountCollection(FieldPath path)
        {
            var remaining = path.Value.AsSpan();
            var first = NextSegment(ref remaining);
            if (first.SequenceEqual("nodes".AsSpan()))
            {
                var key = NextSegment(ref remaining);
                var kind = NextSegment(ref remaining);
                if (remaining.Length == 0 && kind.SequenceEqual("children".AsSpan()))
                {
                    if (!TryFindNode(key, out var node))
                    {
                        return materialization.Completeness.TryGetReason(path, out var reason)
                            ? CollectionCountLookup.Incomplete(reason)
                            : CollectionCountLookup.OutOfScope;
                    }

                    return CollectionCountLookup.Present(node.VisibleChildCount);
                }
            }

            return CollectionCountLookup.OutOfScope;
        }

        /// <summary>The next '/'-delimited segment; empty when the path is exhausted.</summary>
        private static ReadOnlySpan<char> NextSegment(ref ReadOnlySpan<char> remaining)
        {
            var separator = remaining.IndexOf('/');
            if (separator < 0)
            {
                var last = remaining;
                remaining = default;
                return last;
            }

            var segment = remaining.Slice(0, separator);
            remaining = remaining.Slice(separator + 1);
            return segment;
        }

        private FieldLookup LookupSource(FieldPath path, ReadOnlySpan<char> sourceKey, ReadOnlySpan<char> fieldName)
        {
            if (!TryFindSource(sourceKey, out var source))
            {
                // Hidden and unregistered sources answer identically.
                return IncompleteOr(path, FieldLookup.OutOfScope);
            }

            if (source.Omission.HasValue)
            {
                return FieldLookup.Incomplete(source.Omission.Value);
            }

            // Indexed: a ValueList foreach boxes its enumerator (B1).
            var redactedNames = source.RedactedFieldNames;
            for (var i = 0; i < redactedNames.Count; i++)
            {
                if (redactedNames[i].AsSpan().SequenceEqual(fieldName))
                {
                    return FieldLookup.Redacted;
                }
            }

            var fields = source.Fields;
            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i].Name.AsSpan().SequenceEqual(fieldName))
                {
                    return FieldLookup.Present(fields[i].Value);
                }
            }

            return IncompleteOr(path, FieldLookup.Absent);
        }

        private FieldLookup IncompleteOr(FieldPath path, FieldLookup fallback)
        {
            return materialization.Completeness.TryGetReason(path, out var reason)
                ? FieldLookup.Incomplete(reason)
                : fallback;
        }

        private bool TryFindNode(ReadOnlySpan<char> key, out MaterializedNode node)
        {
            // Nodes are ordinally sorted at construction
            // (ObservationMaterialization invariant): binary search, not a scan.
            // Span CompareTo with Ordinal is the same order as CompareOrdinal.
            var nodes = materialization.Nodes;
            var low = 0;
            var high = nodes.Count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                var comparison = nodes[middle].Key.Value.AsSpan().CompareTo(key, StringComparison.Ordinal);
                if (comparison == 0)
                {
                    node = nodes[middle];
                    return true;
                }

                if (comparison < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            node = null!;
            return false;
        }

        private bool TryFindSource(ReadOnlySpan<char> key, out MaterializedSource source)
        {
            // Sources share the same construction-time ordinal sort.
            var sources = materialization.Sources;
            var low = 0;
            var high = sources.Count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                var comparison = sources[middle].Key.Value.AsSpan().CompareTo(key, StringComparison.Ordinal);
                if (comparison == 0)
                {
                    source = sources[middle];
                    return true;
                }

                if (comparison < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            source = null!;
            return false;
        }
    }
}
