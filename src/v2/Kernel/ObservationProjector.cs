using System;
using System.Collections.Generic;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>The node/byte budget one materialization runs under.</summary>
    internal readonly struct ObservationBudget
    {
        internal ObservationBudget(int maxBytes, int maxNodes)
        {
            MaxBytes = maxBytes;
            MaxNodes = maxNodes;
        }

        internal int MaxBytes { get; }

        internal int MaxNodes { get; }
    }

    /// <summary>One projection's answer: the materialization plus its budget accounting.</summary>
    internal sealed class ProjectionResult
    {
        internal ProjectionResult(ObservationMaterialization materialization, bool truncated, int approximateBytes)
        {
            Materialization = materialization;
            Truncated = truncated;
            ApproximateBytes = approximateBytes;
        }

        internal ObservationMaterialization Materialization { get; }

        /// <summary>Whether the node/byte budget cut the materialization short (root truncation).</summary>
        internal bool Truncated { get; }

        internal int ApproximateBytes { get; }
    }

    /// <summary>
    /// The single materialization path (observation-state.md §1–§3, ADR 0011):
    /// projects the node store and source slots under one view contract descriptor
    /// into a post-visibility, post-redaction <see cref="ObservationMaterialization"/>.
    /// Redaction executes at value production; hidden nodes and family-invisible
    /// sources are excluded entirely; truncation is deterministic (ordinal node
    /// order) and surfaces as completeness, never silent omission. Pump thread only.
    /// </summary>
    internal static class ObservationProjector
    {
        internal static ProjectionResult Materialize(
            NodeStore store,
            SourceSlotTable sourceTable,
            ViewContractDescriptor descriptor,
            SecurityDomainId domain,
            long logicalNow,
            ObservationBudget budget,
            int maxFieldBytes,
            int maxCompletenessEntries)
        {
            var effectiveFieldBytes = Math.Min(maxFieldBytes, descriptor.MaxFieldBytes);
            var maxNodes = Math.Min(budget.MaxNodes, descriptor.MaxNodes);
            var completeness = new List<CompletenessEntry>();
            var truncated = false;
            var bytesUsed = 0;

            // Candidate selection: keyed, visible to the domain, inside the scope.
            var candidates = new List<NodeRecord>();
            foreach (var record in store.LiveRecords)
            {
                if (!record.Registration.AuthorKey.HasValue)
                {
                    continue; // keyless nodes are never path-addressable
                }

                if (!record.Registration.Exposure.IsVisibleTo(domain))
                {
                    continue;
                }

                if (!IsInScope(store, record, descriptor.Scope))
                {
                    continue;
                }

                candidates.Add(record);
            }

            candidates.Sort((left, right) => string.CompareOrdinal(
                left.Registration.AuthorKey!.Value.Value, right.Registration.AuthorKey!.Value.Value));

            // Deterministic budget cut: first N candidates in ordinal key order.
            var included = new List<NodeRecord>();
            var includedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in candidates)
            {
                var cost = NodeCost(record, effectiveFieldBytes);
                if (included.Count + 1 > maxNodes || bytesUsed + cost > budget.MaxBytes)
                {
                    truncated = true;
                    break;
                }

                bytesUsed += cost;
                included.Add(record);
                includedKeys.Add(record.Registration.AuthorKey!.Value.Value);
            }

            // Visible child counts, over the same visibility/scope rules; keyless
            // children participate only when the view includes them.
            var childCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var record in store.LiveRecords)
            {
                if (!record.Registration.Parent.HasValue ||
                    !record.Registration.Exposure.IsVisibleTo(domain) ||
                    !IsInScope(store, record, descriptor.Scope))
                {
                    continue;
                }

                if (!record.Registration.AuthorKey.HasValue && !descriptor.IncludeKeylessNodes)
                {
                    continue;
                }

                var parentKey = record.Registration.Parent.Value.Value;
                childCounts.TryGetValue(parentKey, out var count);
                childCounts[parentKey] = count + 1;
            }

            var nodes = new List<MaterializedNode>(included.Count);
            foreach (var record in included)
            {
                var key = record.Registration.AuthorKey!.Value;
                var attributes = new List<MaterializedAttribute>(record.Attributes.Count);
                foreach (var attribute in record.Attributes.Values)
                {
                    if (attribute.Sensitivity == Sensitivity.Sensitive)
                    {
                        // Redaction at value production: presence without content.
                        attributes.Add(new MaterializedAttribute(attribute.Name, default, redacted: true));
                        continue;
                    }

                    if (ValueUnits(attribute.Value) > effectiveFieldBytes)
                    {
                        completeness.Add(new CompletenessEntry(
                            new FieldPath("nodes/" + key.Value + "/attributes/" + attribute.Name),
                            CompletenessReason.BudgetTruncated));
                        continue;
                    }

                    attributes.Add(new MaterializedAttribute(attribute.Name, attribute.Value, redacted: false));
                }

                var capabilities = new List<MaterializedCapability>(record.Availability.Count);
                foreach (var availability in record.Availability)
                {
                    capabilities.Add(new MaterializedCapability(availability.Key, availability.Value));
                }

                // A parent outside the materialized set terminates traversal as a
                // completeness condition, never a dangling key that would reveal a
                // hidden node (observation-state.md §3). The narrow `parent` region
                // marks the condition without shadowing attribute lookups.
                AuthorKey? parent = null;
                if (record.Registration.Parent.HasValue)
                {
                    if (includedKeys.Contains(record.Registration.Parent.Value.Value))
                    {
                        parent = record.Registration.Parent.Value;
                    }
                    else
                    {
                        completeness.Add(new CompletenessEntry(
                            new FieldPath("nodes/" + key.Value + "/parent"),
                            CompletenessReason.OutOfScope));
                    }
                }

                childCounts.TryGetValue(key.Value, out var childCount);
                nodes.Add(new MaterializedNode(
                    key,
                    record.Registration.Role,
                    parent,
                    ValueArray<MaterializedAttribute>.From(attributes),
                    ValueArray<MaterializedCapability>.From(capabilities),
                    childCount));
            }

            // Sources: family selects the exposure opt-in (observation-state.md
            // §7.2). Candidates sort by key before the budget cut so registration
            // order never changes snapshot membership.
            var sourceCandidates = new List<StateSourceRegistration>();
            foreach (var registration in sourceTable.Registrations)
            {
                var visible = descriptor.Family == ViewFamily.Record
                    ? registration.Descriptor.RecordVisible
                    : registration.Descriptor.AgentVisible;
                if (visible)
                {
                    sourceCandidates.Add(registration);
                }
            }

            sourceCandidates.Sort((left, right) =>
                string.CompareOrdinal(left.Key.Value, right.Key.Value));

            var sources = new List<MaterializedSource>();
            foreach (var registration in sourceCandidates)
            {
                var materialized = MaterializeSource(
                    sourceTable, registration, logicalNow, effectiveFieldBytes,
                    completeness, out var cost);
                if (bytesUsed + cost > budget.MaxBytes)
                {
                    truncated = true;
                    break;
                }

                bytesUsed += cost;
                if (materialized.Omission.HasValue)
                {
                    completeness.Add(new CompletenessEntry(
                        new FieldPath("sources/" + registration.Key.Value),
                        materialized.Omission.Value));
                }

                sources.Add(materialized);
            }

            var basis = new ObservationBasis(
                store.Incarnation, store.Revision, descriptor.Contract, domain, descriptor.Scope);
            var materialization = new ObservationMaterialization(
                basis,
                ValueArray<MaterializedNode>.From(nodes),
                ValueArray<MaterializedSource>.From(sources),
                CompletenessMap.From(completeness, maxCompletenessEntries, rootTruncated: truncated));
            return new ProjectionResult(materialization, truncated, bytesUsed);
        }

        private static MaterializedSource MaterializeSource(
            SourceSlotTable sourceTable,
            StateSourceRegistration registration,
            long logicalNow,
            int maxFieldUnits,
            List<CompletenessEntry> completeness,
            out int approximateBytes)
        {
            var descriptor = registration.Descriptor;
            var redactedNames = new List<string>();
            foreach (var schema in descriptor.Fields)
            {
                if (schema.Sensitivity == Sensitivity.Sensitive)
                {
                    redactedNames.Add(schema.Name);
                }
            }

            CompletenessReason? omission = null;
            SourceDocument? document = null;
            if (registration.SourceClass == StateSourceClass.RevisionBound)
            {
                if (!sourceTable.TryGetDocument(registration.Key, out var adopted))
                {
                    omission = CompletenessReason.SourceUnavailable;
                }
                else
                {
                    document = adopted;
                }
            }
            else
            {
                var reading = registration.SampledReader!.Read();
                if (reading == null)
                {
                    omission = CompletenessReason.SourceUnavailable;
                }
                else if (logicalNow - reading.ProducedAtLogicalTime >
                    registration.FreshnessBoundLogicalTime!.Value)
                {
                    omission = CompletenessReason.Stale;
                }
                else if (!ConformsToContract(descriptor, reading.Document))
                {
                    // A sampled reading gets the same contract validation an
                    // adoption gets (declared fields, matching types, the byte
                    // ceiling); a non-conforming reading produced no usable
                    // document and is never partially exposed.
                    omission = CompletenessReason.SourceUnavailable;
                }
                else
                {
                    document = reading.Document;
                }
            }

            approximateBytes = 32;
            var fields = new List<NamedField>();
            if (document != null)
            {
                // Redaction at value production: sensitive fields never enter the
                // materialized copy (the slot table already redacts revision-bound
                // adoptions; sampled readings are redacted here). Oversized values
                // follow the same per-field ceiling as node attributes.
                foreach (var field in document.Fields)
                {
                    var sensitive = false;
                    foreach (var schema in descriptor.Fields)
                    {
                        if (string.Equals(schema.Name, field.Name, StringComparison.Ordinal))
                        {
                            sensitive = schema.Sensitivity == Sensitivity.Sensitive;
                            break;
                        }
                    }

                    if (sensitive)
                    {
                        continue;
                    }

                    if (ValueUnits(field.Value) > maxFieldUnits)
                    {
                        completeness.Add(new CompletenessEntry(
                            new FieldPath("sources/" + registration.Key.Value + "/" + field.Name),
                            CompletenessReason.BudgetTruncated));
                        continue;
                    }

                    fields.Add(field);
                    approximateBytes += 2 * field.Name.Length + ValueUnits(field.Value) * 2 + 16;
                }
            }

            return new MaterializedSource(
                registration.Key,
                descriptor.Contract,
                ValueArray<NamedField>.From(fields),
                ValueArray<string>.From(redactedNames),
                omission);
        }

        /// <summary>The adoption-time contract checks, applied to a sampled reading (observation-state.md §7.2).</summary>
        private static bool ConformsToContract(StateSourceContractDescriptor descriptor, SourceDocument document)
        {
            var totalBytes = 32;
            foreach (var field in document.Fields)
            {
                SourceFieldSchema? declared = null;
                foreach (var schema in descriptor.Fields)
                {
                    if (string.Equals(schema.Name, field.Name, StringComparison.Ordinal))
                    {
                        declared = schema;
                        break;
                    }
                }

                if (declared == null)
                {
                    return false; // undeclared field
                }

                if (field.Value.Kind != FieldValueKind.Null &&
                    !TypeMatches(declared.Value.Type, field.Value.Kind))
                {
                    return false; // runtime type contradicts the schema
                }

                totalBytes += 2 * field.Name.Length + ValueUnits(field.Value) * 2 + 16;
            }

            return totalBytes <= descriptor.MaxDocumentBytes;
        }

        private static bool TypeMatches(FieldType declared, FieldValueKind actual)
        {
            switch (declared)
            {
                case FieldType.String:
                    return actual == FieldValueKind.String;
                case FieldType.Integer:
                    return actual == FieldValueKind.Integer;
                case FieldType.Boolean:
                    return actual == FieldValueKind.Boolean;
                case FieldType.Float:
                    return actual == FieldValueKind.Float;
                default:
                    return false;
            }
        }

        internal static bool IsInScope(NodeStore store, NodeRecord record, string scope)
        {
            if (string.Equals(scope, ViewContractDescriptor.RootScope, StringComparison.Ordinal))
            {
                return true;
            }

            var current = record;
            while (true)
            {
                if (current.Registration.AuthorKey.HasValue &&
                    string.Equals(current.Registration.AuthorKey.Value.Value, scope, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!current.Registration.Parent.HasValue ||
                    !store.TryResolveByKey(current.Registration.Parent.Value, out current!))
                {
                    return false;
                }
            }
        }

        private static int NodeCost(NodeRecord record, int maxFieldBytes)
        {
            var cost = 64 + 2 * record.Registration.AuthorKey!.Value.Value.Length;
            foreach (var attribute in record.Attributes.Values)
            {
                var units = attribute.Sensitivity == Sensitivity.Sensitive
                    ? 0
                    : Math.Min(ValueUnits(attribute.Value), maxFieldBytes);
                cost += 2 * attribute.Name.Length + units * 2 + 16;
            }

            cost += 24 * record.Availability.Count;
            return cost;
        }

        /// <summary>Approximate value size in UTF-16 code units (the MaxFieldBytes unit).</summary>
        private static int ValueUnits(FieldValue value)
        {
            return value.Kind == FieldValueKind.String ? value.AsString.Length : 4;
        }
    }
}
