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
        /// <summary>
        /// The canonical capability order — exactly ordinal comparison of the
        /// historical <c>id@major.minor</c> key, computed piecewise without
        /// building the key (the identifier grammar permits '@' and digits, so
        /// the virtual concatenation must be compared character-faithfully;
        /// order-parity with the string key is pinned by a differential test).
        /// </summary>
        public static int CompareCanonical(MaterializedCapability left, MaterializedCapability right)
        {
            if (left == null)
            {
                throw new ArgumentNullException(nameof(left));
            }

            if (right == null)
            {
                throw new ArgumentNullException(nameof(right));
            }

            Span<char> leftTail = stackalloc char[24];
            Span<char> rightTail = stackalloc char[24];
            var leftLength = FormatVersionTail(left.Contract.Version, leftTail);
            var rightLength = FormatVersionTail(right.Contract.Version, rightTail);
            return CompareConcat(
                left.Contract.Id.Value.AsSpan(), leftTail.Slice(0, leftLength),
                right.Contract.Id.Value.AsSpan(), rightTail.Slice(0, rightLength));
        }

        /// <summary>Writes <c>@major.minor</c>; digits render culture-independently for non-negative parts.</summary>
        private static int FormatVersionTail(ContractVersion version, Span<char> destination)
        {
            destination[0] = '@';
            var offset = 1;
            version.Major.TryFormat(
                destination.Slice(offset), out var written,
                provider: System.Globalization.CultureInfo.InvariantCulture);
            offset += written;
            destination[offset++] = '.';
            version.Minor.TryFormat(
                destination.Slice(offset), out written,
                provider: System.Globalization.CultureInfo.InvariantCulture);
            return offset + written;
        }

        /// <summary>Ordinal comparison of two virtual concatenations <c>head + tail</c>.</summary>
        private static int CompareConcat(
            ReadOnlySpan<char> leftHead, ReadOnlySpan<char> leftTail,
            ReadOnlySpan<char> rightHead, ReadOnlySpan<char> rightTail)
        {
            var leftLength = leftHead.Length + leftTail.Length;
            var rightLength = rightHead.Length + rightTail.Length;
            var shared = Math.Min(leftLength, rightLength);
            for (var i = 0; i < shared; i++)
            {
                var leftChar = i < leftHead.Length ? leftHead[i] : leftTail[i - leftHead.Length];
                var rightChar = i < rightHead.Length ? rightHead[i] : rightTail[i - rightHead.Length];
                if (leftChar != rightChar)
                {
                    return leftChar - rightChar;
                }
            }

            return leftLength - rightLength;
        }

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
            ValueArray<MaterializedAttribute> attributes,
            ValueArray<MaterializedCapability> capabilities,
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
            Attributes = SortUnique(attributes, AttributeOrder, nameof(attributes));
            Capabilities = SortUnique(capabilities, CapabilityOrder, nameof(capabilities));
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

        public ValueArray<MaterializedAttribute> Attributes { get; }

        public ValueArray<MaterializedCapability> Capabilities { get; }

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

        // Cached delegates: a method-group conversion allocates per use on this
        // language level, and construction is the hot path.
        private static readonly Comparison<MaterializedAttribute> AttributeOrder =
            static (left, right) => string.CompareOrdinal(left.Name, right.Name);

        private static readonly Comparison<MaterializedCapability> CapabilityOrder =
            MaterializedCapability.CompareCanonical;

        /// <summary>
        /// Normalization without copying: the input is an immutable
        /// <see cref="ValueArray{T}"/>, so an already-sorted, duplicate-free list
        /// is kept as-is (one O(n) verification scan — the projector constructs
        /// in canonical order, so this is the hot path); only unordered input
        /// pays the copy-and-sort, and duplicates throw either way.
        /// </summary>
        internal static ValueArray<T> SortUnique<T>(
            ValueArray<T> items, Comparison<T> comparison, string parameterName)
        {
            var span = items.AsSpan();
            var sortedAlready = true;
            for (var i = 1; i < span.Length; i++)
            {
                var order = comparison(span[i - 1], span[i]);
                if (order == 0)
                {
                    throw new ArgumentException("Duplicate entry.", parameterName);
                }

                if (order > 0)
                {
                    sortedAlready = false;
                    break;
                }
            }

            if (sortedAlready)
            {
                return items;
            }

            var copy = new T[span.Length];
            span.CopyTo(copy);
            Array.Sort(copy, comparison);
            for (var i = 1; i < copy.Length; i++)
            {
                if (comparison(copy[i - 1], copy[i]) == 0)
                {
                    throw new ArgumentException("Duplicate entry.", parameterName);
                }
            }

            return ValueArray<T>.From(copy);
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
            ValueArray<NamedField> fields,
            ValueArray<string> redactedFieldNames,
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

            // Ordinal normalization without copying (logically identical
            // documents materialize identically regardless of publication or
            // schema order): validate elements, keep already-sorted immutable
            // input as-is, copy-and-sort only unordered input.
            foreach (var field in fields)
            {
                if (field.IsDefault)
                {
                    throw new ArgumentException("Fields must be non-default.", nameof(fields));
                }
            }

            Fields = MaterializedNode.SortUnique(
                fields,
                static (left, right) => string.CompareOrdinal(left.Name, right.Name),
                nameof(fields));

            foreach (var name in redactedFieldNames)
            {
                ContractGrammar.ValidateIdentifier(name, nameof(redactedFieldNames));
            }

            RedactedFieldNames = MaterializedNode.SortUnique(
                redactedFieldNames,
                static (left, right) => string.CompareOrdinal(left, right),
                nameof(redactedFieldNames));

            // The four comparator states are disjoint: a field name is present
            // with a value, redacted, or absent — never present and redacted at
            // once (observation-state.md §3). Both lists are sorted: merge-walk.
            var fieldSpan = Fields.AsSpan();
            var redactedSpan = RedactedFieldNames.AsSpan();
            var fieldIndex = 0;
            var redactedIndex = 0;
            while (fieldIndex < fieldSpan.Length && redactedIndex < redactedSpan.Length)
            {
                var order = string.CompareOrdinal(fieldSpan[fieldIndex].Name, redactedSpan[redactedIndex]);
                if (order == 0)
                {
                    throw new ArgumentException(
                        $"Field '{fieldSpan[fieldIndex].Name}' cannot be both present and redacted.",
                        nameof(fields));
                }

                if (order < 0)
                {
                    fieldIndex++;
                }
                else
                {
                    redactedIndex++;
                }
            }

            Omission = omission;
        }

        public StateSourceKey Key { get; }

        public StateSourceContractRef Contract { get; }

        /// <summary>Post-redaction published fields (a sensitive value never appears here).</summary>
        public ValueArray<NamedField> Fields { get; }

        /// <summary>Declared sensitive field names — presence without content.</summary>
        public ValueArray<string> RedactedFieldNames { get; }

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
            ValueArray<MaterializedNode> nodes,
            ValueArray<MaterializedSource> sources,
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

            // Already-sorted immutable input is kept as-is (the projector emits
            // canonical order); only unordered input pays a copy-and-sort.
            Nodes = MaterializedNode.SortUnique(
                nodes,
                static (left, right) => string.CompareOrdinal(left.Key.Value, right.Key.Value),
                nameof(nodes));
            Sources = MaterializedNode.SortUnique(
                sources,
                static (left, right) => string.CompareOrdinal(left.Key.Value, right.Key.Value),
                nameof(sources));
        }

        public ObservationBasis Basis { get; }

        public ValueArray<MaterializedNode> Nodes { get; }

        public ValueArray<MaterializedSource> Sources { get; }

        public CompletenessMap Completeness { get; }
    }
}
