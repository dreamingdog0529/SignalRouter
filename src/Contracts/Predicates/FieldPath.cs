using System;
using System.Collections.Generic;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// A validated, segmented field path into an observation materialization —
    /// e.g. <c>nodes/save-button/attributes/label</c> or
    /// <c>sources/inventory/items</c>. Segments are non-empty, contain no control
    /// characters or separators, and compare ordinally; paths are never normalized.
    /// </summary>
    public readonly struct FieldPath : IEquatable<FieldPath>
    {
        private const char Separator = '/';
        private readonly string? value;

        public FieldPath(string value)
        {
            ContractGrammar.ValidateIdentifier(value, nameof(value));
            if (value[0] == Separator || value[value.Length - 1] == Separator)
            {
                throw new ArgumentException(
                    "A field path must not start or end with a separator.", nameof(value));
            }

            if (value.Contains("//"))
            {
                throw new ArgumentException(
                    "A field path must not contain empty segments.", nameof(value));
            }

            this.value = value;
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default FieldPath carries no value.");

        /// <summary>
        /// The path's segments as spans over the underlying string — no per-access
        /// splitting, no allocation. <c>foreach (var segment in path.Segments)</c>
        /// yields <see cref="ReadOnlySpan{T}"/> slices.
        /// </summary>
        public SegmentEnumerable Segments => new SegmentEnumerable(Value);

        /// <summary>The number of segments (an O(length) scan, no allocation).</summary>
        public int SegmentCount
        {
            get
            {
                var text = Value;
                var count = 1;
                foreach (var character in text)
                {
                    if (character == Separator)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Segment-wise prefix test (observation-state.md §3): <c>nodes/save</c>
        /// covers <c>nodes/save/attributes/label</c>, never <c>nodes/save2</c>.
        /// Because paths contain no empty segments, this is an ordinal string
        /// prefix plus a segment boundary — no splitting.
        /// </summary>
        public bool IsSegmentPrefixOf(FieldPath path)
        {
            var prefixValue = Value;
            var pathValue = path.Value;
            if (prefixValue.Length > pathValue.Length)
            {
                return false;
            }

            if (!pathValue.AsSpan().StartsWith(prefixValue.AsSpan(), StringComparison.Ordinal))
            {
                return false;
            }

            return pathValue.Length == prefixValue.Length || pathValue[prefixValue.Length] == Separator;
        }

        /// <summary>Allocation-free segment enumeration over a validated path.</summary>
        public readonly ref struct SegmentEnumerable
        {
            private readonly ReadOnlySpan<char> value;

            internal SegmentEnumerable(string value)
            {
                this.value = value.AsSpan();
            }

            public SegmentEnumerator GetEnumerator() => new SegmentEnumerator(value);
        }

        public ref struct SegmentEnumerator
        {
            private ReadOnlySpan<char> remaining;
            private ReadOnlySpan<char> current;
            private bool finished;

            internal SegmentEnumerator(ReadOnlySpan<char> value)
            {
                remaining = value;
                current = default;
                finished = false;
            }

            public ReadOnlySpan<char> Current => current;

            public bool MoveNext()
            {
                if (finished)
                {
                    return false;
                }

                var separator = remaining.IndexOf(Separator);
                if (separator < 0)
                {
                    current = remaining;
                    remaining = default;
                    finished = true;
                    return true;
                }

                current = remaining.Slice(0, separator);
                remaining = remaining.Slice(separator + 1);
                return true;
            }
        }

        public bool Equals(FieldPath other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is FieldPath other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(FieldPath left, FieldPath right) => left.Equals(right);

        public static bool operator !=(FieldPath left, FieldPath right) => !left.Equals(right);
    }
}
