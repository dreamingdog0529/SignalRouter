using System;
using System.Globalization;

namespace SignalRouter.V2.Contracts
{
    /// <summary>The concrete type of a field value.</summary>
    public enum FieldValueKind
    {
        String,
        Integer,
        Boolean,
        Float,

        /// <summary>The field is present and explicitly null — distinct from absent (recording-replay.md §5.2).</summary>
        Null,
    }

    /// <summary>
    /// One typed field value. Redaction and scope are not value kinds — they are
    /// lookup answers (<see cref="FieldLookup"/>); a value that exists here is one
    /// the reader was entitled to produce.
    /// </summary>
    public readonly struct FieldValue : IEquatable<FieldValue>
    {
        private readonly string? stringValue;
        private readonly long integerValue;
        private readonly bool booleanValue;
        private readonly double floatValue;
        private readonly bool initialized;

        private FieldValue(FieldValueKind kind, string? s, long i, bool b, double f)
        {
            Kind = kind;
            stringValue = s;
            integerValue = i;
            booleanValue = b;
            floatValue = f;
            initialized = true;
        }

        public FieldValueKind Kind { get; }

        public bool IsDefault => !initialized;

        public static FieldValue Null => new FieldValue(FieldValueKind.Null, null, 0, false, 0);

        public static FieldValue Of(string value)
        {
            ContractGrammar.ValidateScalarText(value, nameof(value));
            return new FieldValue(FieldValueKind.String, value, 0, false, 0);
        }

        public static FieldValue Of(long value) => new FieldValue(FieldValueKind.Integer, null, value, false, 0);

        public static FieldValue Of(bool value) => new FieldValue(FieldValueKind.Boolean, null, 0, value, 0);

        public static FieldValue Of(double value)
        {
            if (double.IsNaN(value))
            {
                throw new ArgumentException("Field values must not be NaN.", nameof(value));
            }

            return new FieldValue(FieldValueKind.Float, null, 0, false, value);
        }

        public string AsString =>
            Kind == FieldValueKind.String
                ? stringValue!
                : throw new InvalidOperationException("Not a string value.");

        public long AsInteger =>
            Kind == FieldValueKind.Integer
                ? integerValue
                : throw new InvalidOperationException("Not an integer value.");

        public bool AsBoolean =>
            Kind == FieldValueKind.Boolean
                ? booleanValue
                : throw new InvalidOperationException("Not a boolean value.");

        public double AsFloat =>
            Kind == FieldValueKind.Float
                ? floatValue
                : throw new InvalidOperationException("Not a float value.");

        public bool Equals(FieldValue other) =>
            initialized == other.initialized &&
            Kind == other.Kind &&
            string.Equals(stringValue, other.stringValue, StringComparison.Ordinal) &&
            integerValue == other.integerValue &&
            booleanValue == other.booleanValue &&
            floatValue.Equals(other.floatValue);

        public override bool Equals(object? obj) => obj is FieldValue other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes((int)Kind, initialized ? 1 : 0);
            hash = ContractGrammar.CombineHashes(
                hash, stringValue == null ? 0 : StringComparer.Ordinal.GetHashCode(stringValue));
            hash = ContractGrammar.CombineHashes(hash, integerValue.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, booleanValue ? 1 : 0);
            return ContractGrammar.CombineHashes(hash, floatValue.GetHashCode());
        }

        /// <summary>A canonical, culture-invariant rendering used by canonicalization and clause reports.</summary>
        public override string ToString()
        {
            if (!initialized)
            {
                return "(default)";
            }

            switch (Kind)
            {
                case FieldValueKind.String:
                    return stringValue!;
                case FieldValueKind.Integer:
                    return integerValue.ToString(CultureInfo.InvariantCulture);
                case FieldValueKind.Boolean:
                    return booleanValue ? "true" : "false";
                case FieldValueKind.Float:
                    return floatValue.ToString("R", CultureInfo.InvariantCulture);
                default:
                    return "null";
            }
        }

        public static bool operator ==(FieldValue left, FieldValue right) => left.Equals(right);

        public static bool operator !=(FieldValue left, FieldValue right) => !left.Equals(right);
    }

    /// <summary>A named field value — the building block of payloads and source documents.</summary>
    public readonly struct NamedField : IEquatable<NamedField>
    {
        public NamedField(string name, FieldValue value)
        {
            Name = ContractGrammar.ValidateIdentifier(name, nameof(name));
            if (value.IsDefault)
            {
                throw new ArgumentException("NamedField requires a non-default value.", nameof(value));
            }

            Value = value;
        }

        public string Name { get; }

        public FieldValue Value { get; }

        /// <summary>True for `default(NamedField)`, which bypasses the constructor.</summary>
        public bool IsDefault => Name == null;

        public bool Equals(NamedField other) =>
            string.Equals(Name, other.Name, StringComparison.Ordinal) && Value.Equals(other.Value);

        public override bool Equals(object? obj) => obj is NamedField other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(StringComparer.Ordinal.GetHashCode(Name), Value.GetHashCode());

        public override string ToString() => $"{Name}={Value}";

        public static bool operator ==(NamedField left, NamedField right) => left.Equals(right);

        public static bool operator !=(NamedField left, NamedField right) => !left.Equals(right);
    }
}
