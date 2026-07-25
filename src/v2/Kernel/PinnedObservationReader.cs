using System;
using System.Collections.Generic;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>
    /// The kernel's materialized observation over the node store and source slots —
    /// item 2's implementation of <see cref="IObservationLookup"/>; item 3's view
    /// projections replace the production path, not the interface. Redaction
    /// executes at materialization (a sensitive value never enters the retained
    /// copy) and exposure filters per domain at lookup, which is what makes the
    /// no-boolean-oracle answers real rather than stubs.
    ///
    /// Path grammar: <c>nodes/&lt;authorKey&gt;/attributes/&lt;name&gt;</c>,
    /// <c>nodes/&lt;authorKey&gt;/children</c> (keyed collection), and
    /// <c>sources/&lt;key&gt;/&lt;field&gt;</c>.
    /// </summary>
    internal sealed class PinnedObservationReader : IObservationLookup
    {
        private sealed class AttributeEntry
        {
            internal AttributeEntry(FieldValue value, bool redacted, ExposurePolicy exposure)
            {
                Value = value;
                Redacted = redacted;
                Exposure = exposure;
            }

            internal FieldValue Value { get; }

            internal bool Redacted { get; }

            internal ExposurePolicy Exposure { get; }
        }

        private sealed class SourceEntry
        {
            internal SourceEntry(
                StateSourceRegistration registration,
                Dictionary<string, FieldValue>? fields,
                bool unavailable,
                bool stale)
            {
                Registration = registration;
                Fields = fields;
                Unavailable = unavailable;
                Stale = stale;
            }

            internal StateSourceRegistration Registration { get; }

            internal Dictionary<string, FieldValue>? Fields { get; }

            internal bool Unavailable { get; }

            internal bool Stale { get; }
        }

        private readonly Dictionary<string, AttributeEntry> attributes =
            new Dictionary<string, AttributeEntry>(StringComparer.Ordinal);

        private readonly Dictionary<string, int> childCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private readonly Dictionary<string, ExposurePolicy> nodeExposure =
            new Dictionary<string, ExposurePolicy>(StringComparer.Ordinal);

        private readonly Dictionary<string, SourceEntry> sources =
            new Dictionary<string, SourceEntry>(StringComparer.Ordinal);

        private readonly SecurityDomainId domain;
        private readonly SecurityDomainId recordDomain;

        internal PinnedObservationReader(
            NodeStore store,
            SourceSlotTable sourceTable,
            SecurityDomainId domain,
            SecurityDomainId recordDomain,
            ViewContractRef view,
            string scope,
            long logicalNow)
        {
            this.domain = domain;
            this.recordDomain = recordDomain;
            Basis = new ObservationBasis(store.Incarnation, store.Revision, view, domain, scope);

            foreach (var record in store.LiveRecords)
            {
                if (!record.Registration.AuthorKey.HasValue)
                {
                    continue; // keyless nodes are not path-addressable
                }

                var key = record.Registration.AuthorKey.Value.Value;
                nodeExposure[key] = record.Registration.Exposure;
                foreach (var attribute in record.Attributes.Values)
                {
                    // Redaction at value production: the sensitive value itself
                    // never enters the materialization (semantic-model.md 7).
                    attributes[key + "/" + attribute.Name] = new AttributeEntry(
                        attribute.Sensitivity == Sensitivity.Sensitive ? default : attribute.Value,
                        attribute.Sensitivity == Sensitivity.Sensitive,
                        record.Registration.Exposure);
                }

                if (record.Registration.Parent.HasValue)
                {
                    var parentKey = record.Registration.Parent.Value.Value;
                    childCounts.TryGetValue(parentKey, out var count);
                    childCounts[parentKey] = count + 1;
                }
            }

            foreach (var registration in sourceTable.Registrations)
            {
                Dictionary<string, FieldValue>? fields = null;
                var unavailable = false;
                var stale = false;
                if (registration.SourceClass == StateSourceClass.RevisionBound)
                {
                    if (sourceTable.TryGetDocument(registration.Key, out var document))
                    {
                        fields = Materialize(registration, document);
                    }
                    else
                    {
                        unavailable = true;
                    }
                }
                else
                {
                    var reading = registration.SampledReader!.Read();
                    if (reading == null)
                    {
                        unavailable = true;
                    }
                    else if (logicalNow - reading.ProducedAtLogicalTime >
                        registration.FreshnessBoundLogicalTime!.Value)
                    {
                        stale = true;
                    }
                    else
                    {
                        fields = Materialize(registration, reading.Document);
                    }
                }

                sources[registration.Key.Value] = new SourceEntry(registration, fields, unavailable, stale);
            }
        }

        public ObservationBasis Basis { get; }

        public FieldLookup Lookup(FieldPath path)
        {
            var segments = path.Segments;
            if (segments.Count == 4 && segments[0] == "nodes" && segments[2] == "attributes")
            {
                if (!nodeExposure.TryGetValue(segments[1], out var exposure))
                {
                    // Unregistered and hidden nodes answer identically.
                    return FieldLookup.OutOfScope;
                }

                if (!exposure.IsVisibleTo(domain))
                {
                    return FieldLookup.OutOfScope;
                }

                if (!attributes.TryGetValue(segments[1] + "/" + segments[3], out var entry))
                {
                    return FieldLookup.Absent;
                }

                return entry.Redacted ? FieldLookup.Redacted : FieldLookup.Present(entry.Value);
            }

            if (segments.Count == 3 && segments[0] == "sources")
            {
                return LookupSource(segments[1], segments[2]);
            }

            return FieldLookup.OutOfScope;
        }

        public CollectionCountLookup CountCollection(FieldPath path)
        {
            var segments = path.Segments;
            if (segments.Count == 3 && segments[0] == "nodes" && segments[2] == "children")
            {
                if (!nodeExposure.TryGetValue(segments[1], out var exposure) ||
                    !exposure.IsVisibleTo(domain))
                {
                    return CollectionCountLookup.OutOfScope;
                }

                childCounts.TryGetValue(segments[1], out var count);
                return CollectionCountLookup.Present(count);
            }

            return CollectionCountLookup.OutOfScope;
        }

        private FieldLookup LookupSource(string sourceKey, string fieldName)
        {
            if (!sources.TryGetValue(sourceKey, out var entry))
            {
                return FieldLookup.OutOfScope;
            }

            var descriptor = entry.Registration.Descriptor;
            var visible = domain.Equals(recordDomain) ? descriptor.RecordVisible : descriptor.AgentVisible;
            if (!visible)
            {
                // Hidden and unregistered sources answer identically.
                return FieldLookup.OutOfScope;
            }

            if (entry.Unavailable)
            {
                return FieldLookup.Incomplete(CompletenessReason.SourceUnavailable);
            }

            if (entry.Stale)
            {
                return FieldLookup.Incomplete(CompletenessReason.Stale);
            }

            foreach (var field in descriptor.Fields)
            {
                if (string.Equals(field.Name, fieldName, StringComparison.Ordinal))
                {
                    if (field.Sensitivity == Sensitivity.Sensitive)
                    {
                        return FieldLookup.Redacted;
                    }

                    return entry.Fields!.TryGetValue(fieldName, out var value)
                        ? FieldLookup.Present(value)
                        : FieldLookup.Absent;
                }
            }

            return FieldLookup.Absent;
        }

        private static Dictionary<string, FieldValue> Materialize(
            StateSourceRegistration registration, SourceDocument document)
        {
            // Redaction at value production: sensitive fields are dropped from the
            // materialized copy entirely; lookups answer Redacted from the schema.
            var fields = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
            foreach (var field in document.Fields)
            {
                var sensitive = false;
                foreach (var schema in registration.Descriptor.Fields)
                {
                    if (string.Equals(schema.Name, field.Name, StringComparison.Ordinal))
                    {
                        sensitive = schema.Sensitivity == Sensitivity.Sensitive;
                        break;
                    }
                }

                if (!sensitive)
                {
                    fields[field.Name] = field.Value;
                }
            }

            return fields;
        }
    }
}
