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
            var segments = path.Segments;
            if (segments.Count == 4 && segments[0] == "nodes" && segments[2] == "attributes")
            {
                if (!TryFindNode(segments[1], out var node))
                {
                    // Unregistered, hidden, and out-of-scope nodes answer
                    // identically; a truncated region answers its reason.
                    return IncompleteOr(path, FieldLookup.OutOfScope);
                }

                foreach (var attribute in node.Attributes)
                {
                    if (string.Equals(attribute.Name, segments[3], StringComparison.Ordinal))
                    {
                        return attribute.Redacted
                            ? FieldLookup.Redacted
                            : FieldLookup.Present(attribute.Value);
                    }
                }

                return IncompleteOr(path, FieldLookup.Absent);
            }

            if (segments.Count == 3 && segments[0] == "sources")
            {
                return LookupSource(path, segments[1], segments[2]);
            }

            return FieldLookup.OutOfScope;
        }

        public CollectionCountLookup CountCollection(FieldPath path)
        {
            var segments = path.Segments;
            if (segments.Count == 3 && segments[0] == "nodes" && segments[2] == "children")
            {
                if (!TryFindNode(segments[1], out var node))
                {
                    return materialization.Completeness.TryGetReason(path, out var reason)
                        ? CollectionCountLookup.Incomplete(reason)
                        : CollectionCountLookup.OutOfScope;
                }

                return CollectionCountLookup.Present(node.VisibleChildCount);
            }

            return CollectionCountLookup.OutOfScope;
        }

        private FieldLookup LookupSource(FieldPath path, string sourceKey, string fieldName)
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

            foreach (var redacted in source.RedactedFieldNames)
            {
                if (string.Equals(redacted, fieldName, StringComparison.Ordinal))
                {
                    return FieldLookup.Redacted;
                }
            }

            foreach (var field in source.Fields)
            {
                if (string.Equals(field.Name, fieldName, StringComparison.Ordinal))
                {
                    return FieldLookup.Present(field.Value);
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

        private bool TryFindNode(string key, out MaterializedNode node)
        {
            // Nodes are ordinally sorted at construction
            // (ObservationMaterialization invariant): binary search, not a scan.
            var nodes = materialization.Nodes;
            var low = 0;
            var high = nodes.Count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                var comparison = string.CompareOrdinal(nodes[middle].Key.Value, key);
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

        private bool TryFindSource(string key, out MaterializedSource source)
        {
            // Sources share the same construction-time ordinal sort.
            var sources = materialization.Sources;
            var low = 0;
            var high = sources.Count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                var comparison = string.CompareOrdinal(sources[middle].Key.Value, key);
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
