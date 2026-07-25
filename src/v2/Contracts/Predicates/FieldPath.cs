using System;
using System.Collections.Generic;

namespace SignalRouter.V2.Contracts
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

        public IReadOnlyList<string> Segments =>
            value == null
                ? throw new InvalidOperationException("A default FieldPath carries no value.")
                : value.Split(Separator);

        public bool Equals(FieldPath other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is FieldPath other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(FieldPath left, FieldPath right) => left.Equals(right);

        public static bool operator !=(FieldPath left, FieldPath right) => !left.Equals(right);
    }
}
