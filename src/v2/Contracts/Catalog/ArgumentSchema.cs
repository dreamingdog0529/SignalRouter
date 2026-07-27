using System;
using System.Collections.Generic;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// Sensitivity of an argument or attribute (semantic-model.md §7). A node may
    /// raise sensitivity relative to its contract but never lower it — the ratchet
    /// is enforced where node meets contract.
    /// </summary>
    public enum Sensitivity
    {
        Standard,
        Sensitive,
    }

    /// <summary>One declared argument field of a capability contract (semantic-model.md §2.2).</summary>
    public readonly struct ArgumentField : IEquatable<ArgumentField>
    {
        public ArgumentField(string name, FieldType type, bool required, Sensitivity sensitivity)
        {
            if (type == FieldType.KeyedCollection)
            {
                throw new ArgumentException(
                    "Arguments are scalar; keyed collections are observation fields.", nameof(type));
            }

            Name = ContractGrammar.ValidateIdentifier(name, nameof(name));
            Type = type;
            Required = required;
            Sensitivity = sensitivity;
        }

        public string Name { get; }

        public FieldType Type { get; }

        public bool Required { get; }

        public Sensitivity Sensitivity { get; }

        public bool Equals(ArgumentField other) =>
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            Type == other.Type &&
            Required == other.Required &&
            Sensitivity == other.Sensitivity;

        public override bool Equals(object? obj) => obj is ArgumentField other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(StringComparer.Ordinal.GetHashCode(Name), (int)Type);
            hash = ContractGrammar.CombineHashes(hash, Required ? 1 : 0);
            return ContractGrammar.CombineHashes(hash, (int)Sensitivity);
        }

        public static bool operator ==(ArgumentField left, ArgumentField right) => left.Equals(right);

        public static bool operator !=(ArgumentField left, ArgumentField right) => !left.Equals(right);
    }

    /// <summary>The typed argument schema of a capability contract; field names are unique.</summary>
    public sealed class ArgumentSchema
    {
        private readonly Dictionary<string, ArgumentField> byName;

        public ArgumentSchema(ValueArray<ArgumentField> fields)
        {
            byName = new Dictionary<string, ArgumentField>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (byName.ContainsKey(field.Name))
                {
                    throw new ArgumentException("Argument names must be unique.", nameof(fields));
                }

                byName.Add(field.Name, field);
            }

            Fields = fields;
        }

        public static ArgumentSchema Empty { get; } = new ArgumentSchema(ValueArray<ArgumentField>.Empty);

        public ValueArray<ArgumentField> Fields { get; }

        public bool TryGetField(string name, out ArgumentField field)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            return byName.TryGetValue(name, out field);
        }
    }
}
