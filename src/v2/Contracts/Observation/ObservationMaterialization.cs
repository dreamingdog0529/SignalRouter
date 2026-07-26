using System;
using System.Collections.Generic;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// One materialized attribute. Redaction happened at production: a redacted
    /// attribute marks presence without content and carries no value
    /// (semantic-model.md §7, observation-state.md §3).
    /// </summary>
    public sealed class MaterializedAttribute : IEquatable<MaterializedAttribute>
    {
        public MaterializedAttribute(string name, FieldValue value, bool redacted)
        {
            Name = ContractGrammar.ValidateIdentifier(name, nameof(name));
            if (redacted && !value.IsDefault)
            {
                throw new ArgumentException(
                    "A redacted attribute never carries a value.", nameof(value));
            }

            if (!redacted && value.IsDefault)
            {
                throw new ArgumentException(
                    "A non-redacted attribute requires a value (use FieldValue.Null for explicit null).",
                    nameof(value));
            }

            Value = value;
            Redacted = redacted;
        }

        public string Name { get; }

        public FieldValue Value { get; }

        public bool Redacted { get; }

        public bool Equals(MaterializedAttribute? other) =>
            other != null &&
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            Value.Equals(other.Value) &&
            Redacted == other.Redacted;

        public override bool Equals(object? obj) => Equals(obj as MaterializedAttribute);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(Name), Value.GetHashCode());
            return ContractGrammar.CombineHashes(hash, Redacted ? 1 : 0);
        }
    }

    /// <summary>One materialized capability declaration: the contract and its current availability.</summary>
    public sealed class MaterializedCapability : IEquatable<MaterializedCapability>
    {
        public MaterializedCapability(CapabilityContractRef contract, bool available)
        {
            if (contract.IsDefault)
            {
                throw new ArgumentException(
                    "A materialized capability requires a non-default contract.", nameof(contract));
            }

            Contract = contract;
            Available = available;
        }

        public CapabilityContractRef Contract { get; }

        public bool Available { get; }

        public bool Equals(MaterializedCapability? other) =>
            other != null && Contract.Equals(other.Contract) && Available == other.Available;

        public override bool Equals(object? obj) => Equals(obj as MaterializedCapability);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(Contract.GetHashCode(), Available ? 1 : 0);
    }

    /// <summary>
    /// One materialized node: the strict-comparison surface — role, hierarchy,
    /// attributes, capability declarations — post-visibility and post-redaction
    /// (observation-state.md §1, recording-replay.md §5.2). Attributes and
    /// capabilities are ordinally sorted at construction; keyless nodes are never
    /// materialized (they are not path-addressable) though they may contribute to
    /// visible child counts.
    /// </summary>
    public sealed class MaterializedNode : IEquatable<MaterializedNode>
    {
        public MaterializedNode(
            AuthorKey key,
            NodeRole role,
            AuthorKey? parent,
            ValueList<MaterializedAttribute> attributes,
            ValueList<MaterializedCapability> capabilities,
            int visibleChildCount)
        {
            if (key.IsDefault)
            {
                throw new ArgumentException("A materialized node requires a key.", nameof(key));
            }

            if (role.IsDefault)
            {
                throw new ArgumentException("A materialized node requires a role.", nameof(role));
            }

            if (parent.HasValue && parent.Value.IsDefault)
            {
                throw new ArgumentException("A present parent must be non-default.", nameof(parent));
            }

            if (attributes == null)
            {
                throw new ArgumentNullException(nameof(attributes));
            }

            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            if (visibleChildCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(visibleChildCount));
            }

            Key = key;
            Role = role;
            Parent = parent;
            Attributes = SortUnique(
                attributes, attribute => attribute.Name, nameof(attributes));
            Capabilities = SortUnique(
                capabilities, capability => CapabilityOrderKey(capability.Contract), nameof(capabilities));
            VisibleChildCount = visibleChildCount;
        }

        public AuthorKey Key { get; }

        public NodeRole Role { get; }

        /// <summary>
        /// Null when the node has no parent, or when the parent is outside the
        /// materialized scope — the latter case carries an `OutOfScope` completeness
        /// entry for this node's region (observation-state.md §3).
        /// </summary>
        public AuthorKey? Parent { get; }

        public ValueList<MaterializedAttribute> Attributes { get; }

        public ValueList<MaterializedCapability> Capabilities { get; }

        public int VisibleChildCount { get; }

        public bool Equals(MaterializedNode? other) =>
            other != null &&
            Key.Equals(other.Key) &&
            Role.Equals(other.Role) &&
            Nullable.Equals(Parent, other.Parent) &&
            Attributes.Equals(other.Attributes) &&
            Capabilities.Equals(other.Capabilities) &&
            VisibleChildCount == other.VisibleChildCount;

        public override bool Equals(object? obj) => Equals(obj as MaterializedNode);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(Key.GetHashCode(), Role.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, Parent?.GetHashCode() ?? 0);
            hash = ContractGrammar.CombineHashes(hash, Attributes.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, Capabilities.GetHashCode());
            return ContractGrammar.CombineHashes(hash, VisibleChildCount);
        }

        internal static string CapabilityOrderKey(CapabilityContractRef contract) =>
            contract.Id.Value + "@" + contract.Version;

        private static ValueList<T> SortUnique<T>(
            ValueList<T> items, Func<T, string> orderKey, string parameterName)
        {
            var sorted = new List<T>(items);
            sorted.Sort((left, right) => string.CompareOrdinal(orderKey(left), orderKey(right)));
            for (var i = 1; i < sorted.Count; i++)
            {
                if (string.Equals(orderKey(sorted[i - 1]), orderKey(sorted[i]), StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Duplicate entry '{orderKey(sorted[i])}'.", parameterName);
                }
            }

            return ValueList<T>.From(sorted);
        }
    }

    /// <summary>
    /// One materialized state source (observation-state.md §7): post-redaction
    /// fields, the declared sensitive field names (presence without content), and
    /// the omission reason when the document is unavailable, stale, or contract-unsupported.
    /// </summary>
    public sealed class MaterializedSource : IEquatable<MaterializedSource>
    {
        public MaterializedSource(
            StateSourceKey key,
            StateSourceContractRef contract,
            ValueList<NamedField> fields,
            ValueList<string> redactedFieldNames,
            CompletenessReason? omission)
        {
            if (key.IsDefault)
            {
                throw new ArgumentException("A materialized source requires a key.", nameof(key));
            }

            if (contract.IsDefault)
            {
                throw new ArgumentException(
                    "A materialized source requires a non-default contract.", nameof(contract));
            }

            if (fields == null)
            {
                throw new ArgumentNullException(nameof(fields));
            }

            if (redactedFieldNames == null)
            {
                throw new ArgumentNullException(nameof(redactedFieldNames));
            }

            if (omission.HasValue &&
                omission.Value != CompletenessReason.SourceUnavailable &&
                omission.Value != CompletenessReason.Stale &&
                omission.Value != CompletenessReason.UnsupportedContract)
            {
                throw new ArgumentException(
                    "A source omission is SourceUnavailable, Stale, or UnsupportedContract.",
                    nameof(omission));
            }

            if (omission.HasValue && fields.Count > 0)
            {
                throw new ArgumentException(
                    "An omitted source carries no fields.", nameof(fields));
            }

            Key = key;
            Contract = contract;

            // Ordinal normalization: logically identical documents materialize
            // identically regardless of publication or schema order.
            var sortedFields = new List<NamedField>(fields);
            foreach (var field in sortedFields)
            {
                if (field.IsDefault)
                {
                    throw new ArgumentException("Fields must be non-default.", nameof(fields));
                }
            }

            sortedFields.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            for (var i = 1; i < sortedFields.Count; i++)
            {
                if (string.Equals(sortedFields[i - 1].Name, sortedFields[i].Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Duplicate field name.", nameof(fields));
                }
            }

            var sortedRedacted = new List<string>(redactedFieldNames);
            foreach (var name in sortedRedacted)
            {
                ContractGrammar.ValidateIdentifier(name, nameof(redactedFieldNames));
            }

            sortedRedacted.Sort(StringComparer.Ordinal);
            for (var i = 1; i < sortedRedacted.Count; i++)
            {
                if (string.Equals(sortedRedacted[i - 1], sortedRedacted[i], StringComparison.Ordinal))
                {
                    throw new ArgumentException("Duplicate redacted field name.", nameof(redactedFieldNames));
                }
            }

            // The four comparator states are disjoint: a field name is present with
            // a value, redacted, or absent — never present and redacted at once
            // (observation-state.md §3).
            foreach (var field in sortedFields)
            {
                if (sortedRedacted.BinarySearch(field.Name, StringComparer.Ordinal) >= 0)
                {
                    throw new ArgumentException(
                        $"Field '{field.Name}' cannot be both present and redacted.", nameof(fields));
                }
            }

            Fields = ValueList<NamedField>.From(sortedFields);
            RedactedFieldNames = ValueList<string>.From(sortedRedacted);
            Omission = omission;
        }

        public StateSourceKey Key { get; }

        public StateSourceContractRef Contract { get; }

        /// <summary>Post-redaction published fields (a sensitive value never appears here).</summary>
        public ValueList<NamedField> Fields { get; }

        /// <summary>Declared sensitive field names — presence without content.</summary>
        public ValueList<string> RedactedFieldNames { get; }

        public CompletenessReason? Omission { get; }

        public bool Equals(MaterializedSource? other) =>
            other != null &&
            Key.Equals(other.Key) &&
            Contract.Equals(other.Contract) &&
            Fields.Equals(other.Fields) &&
            RedactedFieldNames.Equals(other.RedactedFieldNames) &&
            Nullable.Equals(Omission, other.Omission);

        public override bool Equals(object? obj) => Equals(obj as MaterializedSource);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(Key.GetHashCode(), Contract.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, Fields.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, RedactedFieldNames.GetHashCode());
            return ContractGrammar.CombineHashes(hash, Omission.HasValue ? (int)Omission.Value + 1 : 0);
        }
    }

    /// <summary>
    /// One revision-consistent, post-visibility, post-redaction view materialization
    /// (observation-state.md §1–§3): the canonical BCL object graph the canonical-state
    /// codec encodes, replay comparison consumes, and <see cref="MaterializationLookup"/>
    /// answers from. Nodes and sources are ordinally sorted at construction.
    /// </summary>
    public sealed class ObservationMaterialization
    {
        public ObservationMaterialization(
            ObservationBasis basis,
            ValueList<MaterializedNode> nodes,
            ValueList<MaterializedSource> sources,
            CompletenessMap completeness)
        {
            Basis = basis ?? throw new ArgumentNullException(nameof(basis));
            if (nodes == null)
            {
                throw new ArgumentNullException(nameof(nodes));
            }

            if (sources == null)
            {
                throw new ArgumentNullException(nameof(sources));
            }

            Completeness = completeness ?? throw new ArgumentNullException(nameof(completeness));

            var sortedNodes = new List<MaterializedNode>(nodes);
            sortedNodes.Sort((left, right) => string.CompareOrdinal(left.Key.Value, right.Key.Value));
            for (var i = 1; i < sortedNodes.Count; i++)
            {
                if (sortedNodes[i - 1].Key.Equals(sortedNodes[i].Key))
                {
                    throw new ArgumentException("Duplicate node key.", nameof(nodes));
                }
            }

            var sortedSources = new List<MaterializedSource>(sources);
            sortedSources.Sort((left, right) => string.CompareOrdinal(left.Key.Value, right.Key.Value));
            for (var i = 1; i < sortedSources.Count; i++)
            {
                if (sortedSources[i - 1].Key.Equals(sortedSources[i].Key))
                {
                    throw new ArgumentException("Duplicate source key.", nameof(sources));
                }
            }

            Nodes = ValueList<MaterializedNode>.From(sortedNodes);
            Sources = ValueList<MaterializedSource>.From(sortedSources);
        }

        public ObservationBasis Basis { get; }

        public ValueList<MaterializedNode> Nodes { get; }

        public ValueList<MaterializedSource> Sources { get; }

        public CompletenessMap Completeness { get; }
    }
}
