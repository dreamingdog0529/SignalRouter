using System;
using System.Collections.Generic;

namespace SignalRouter.V2.Contracts
{
    /// <summary>The declared type of a field, as the catalog exposes it (verification.md §4).</summary>
    public enum FieldType
    {
        String,
        Integer,
        Boolean,
        Float,

        /// <summary>A keyed collection; predicates may count it, never iterate it.</summary>
        KeyedCollection,
    }

    /// <summary>One catalog row: a stable field path and its declared type.</summary>
    public readonly struct FieldSchema : IEquatable<FieldSchema>
    {
        public FieldSchema(FieldPath path, FieldType type)
        {
            if (path.IsDefault)
            {
                throw new ArgumentException("FieldSchema requires a non-default path.", nameof(path));
            }

            Path = path;
            Type = type;
        }

        public FieldPath Path { get; }

        public FieldType Type { get; }

        public bool Equals(FieldSchema other) => Path.Equals(other.Path) && Type == other.Type;

        public override bool Equals(object? obj) => obj is FieldSchema other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes(Path.GetHashCode(), (int)Type);

        public override string ToString() => $"{Path}: {Type}";

        public static bool operator ==(FieldSchema left, FieldSchema right) => left.Equals(right);

        public static bool operator !=(FieldSchema left, FieldSchema right) => !left.Equals(right);
    }

    /// <summary>
    /// The type-check basis for predicate validation: the stable field paths and
    /// types visible to the authoring domain (verification.md §4). Validation against
    /// the catalog is free of observation cost and side effects.
    /// </summary>
    public sealed class PredicateCatalog
    {
        private readonly Dictionary<FieldPath, FieldType> fields;

        public PredicateCatalog(ValueArray<FieldSchema> fields)
        {
            if (fields == null)
            {
                throw new ArgumentNullException(nameof(fields));
            }

            this.fields = new Dictionary<FieldPath, FieldType>();
            foreach (var field in fields)
            {
                if (this.fields.ContainsKey(field.Path))
                {
                    throw new ArgumentException(
                        "Catalog field paths must be unique.", nameof(fields));
                }

                this.fields.Add(field.Path, field.Type);
            }

            Fields = fields;
        }

        public ValueArray<FieldSchema> Fields { get; }

        public bool TryGetType(FieldPath path, out FieldType type)
        {
            return fields.TryGetValue(path, out type);
        }
    }
}
