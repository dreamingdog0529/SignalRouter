using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// One recorded invocation argument: a typed post-redaction value, or — for a
    /// sensitive argument — a secret reference plus the keyed digest of the value
    /// (never the value itself). Exactly one leg is populated
    /// (guarantees.md §5.2, ADR 0015).
    /// </summary>
    public readonly struct RecordedArgument : IEquatable<RecordedArgument>
    {
        private readonly string? name;
        private readonly FieldValue value;
        private readonly SecretReference secret;
        private readonly ArgumentDigest secretDigest;

        private RecordedArgument(
            string name, FieldValue value, SecretReference secret, ArgumentDigest secretDigest)
        {
            this.name = name;
            this.value = value;
            this.secret = secret;
            this.secretDigest = secretDigest;
        }

        /// <summary>A non-sensitive argument carrying its typed value (Null included).</summary>
        public static RecordedArgument OfValue(string name, FieldValue value)
        {
            ContractGrammar.ValidateIdentifier(name, nameof(name));
            if (value.IsDefault)
            {
                throw new ArgumentException(
                    "A recorded value argument requires a non-default FieldValue (use Null explicitly).",
                    nameof(value));
            }

            return new RecordedArgument(name, value, default, default);
        }

        /// <summary>
        /// A sensitive argument: the contract-scoped reference the replay resolver
        /// answers, plus the keyed digest that validates the resolved value. The
        /// digest is keyed with the runtime's redaction material — replay-side
        /// validation therefore requires the host to supply the same material to
        /// the replay runtime (ADR 0015).
        /// </summary>
        public static RecordedArgument OfSecret(
            string name, SecretReference reference, ArgumentDigest valueDigest)
        {
            ContractGrammar.ValidateIdentifier(name, nameof(name));
            if (reference.IsDefault)
            {
                throw new ArgumentException(
                    "A recorded secret argument requires a non-default reference.", nameof(reference));
            }

            if (valueDigest.IsDefault)
            {
                throw new ArgumentException(
                    "A recorded secret argument requires the keyed value digest.", nameof(valueDigest));
            }

            return new RecordedArgument(name, default, reference, valueDigest);
        }

        public bool IsDefault => name == null;

        public string Name => name ?? throw new InvalidOperationException(
            "A default RecordedArgument carries no name.");

        public bool IsSecret => name != null && !secret.IsDefault;

        public FieldValue Value =>
            name != null && secret.IsDefault
                ? value
                : throw new InvalidOperationException("Only a value argument carries a FieldValue.");

        public SecretReference Secret =>
            IsSecret
                ? secret
                : throw new InvalidOperationException("Only a secret argument carries a reference.");

        public ArgumentDigest SecretValueDigest =>
            IsSecret
                ? secretDigest
                : throw new InvalidOperationException("Only a secret argument carries a value digest.");

        public bool Equals(RecordedArgument other) =>
            string.Equals(name, other.name, StringComparison.Ordinal) &&
            value.Equals(other.value) &&
            secret.Equals(other.secret) &&
            secretDigest.Equals(other.secretDigest);

        public override bool Equals(object? obj) => obj is RecordedArgument other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(
                name == null ? 0 : StringComparer.Ordinal.GetHashCode(name), value.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, secret.GetHashCode());
            return ContractGrammar.CombineHashes(hash, secretDigest.GetHashCode());
        }

        public override string ToString() =>
            name == null ? "(default)" : IsSecret ? name + "=(secret)" : name + "=" + value;

        public static bool operator ==(RecordedArgument left, RecordedArgument right) => left.Equals(right);

        public static bool operator !=(RecordedArgument left, RecordedArgument right) => !left.Equals(right);
    }

    /// <summary>
    /// The portable, replayable projection of an invocation's arguments: every
    /// field of the admitted payload in canonical (ordinal name) order, sensitive
    /// values as secret references. This is what E2 carries so a replay runtime
    /// can re-admit the invocation; the redacted argument digest remains the
    /// identity authority, and <see cref="InvocationCanonicalizer.DigestOf"/>
    /// recomputes it from this form (ADR 0015).
    /// </summary>
    public sealed class RecordedArguments : IEquatable<RecordedArguments>
    {
        public RecordedArguments(ValueArray<RecordedArgument> fields)
        {
            for (var index = 0; index < fields.Count; index++)
            {
                if (fields[index].IsDefault)
                {
                    throw new ArgumentException(
                        "Recorded arguments must not contain default entries.", nameof(fields));
                }

                if (index > 0)
                {
                    var comparison = string.CompareOrdinal(
                        fields[index - 1].Name, fields[index].Name);
                    if (comparison > 0)
                    {
                        throw new ArgumentException(
                            "Recorded arguments must be in ordinal name order.", nameof(fields));
                    }

                    if (comparison == 0)
                    {
                        throw new ArgumentException(
                            "Recorded argument names must be unique.", nameof(fields));
                    }
                }
            }

            Fields = fields;
        }

        public static RecordedArguments Empty { get; } =
            new RecordedArguments(ValueArray<RecordedArgument>.Empty);

        public ValueArray<RecordedArgument> Fields { get; }

        public bool Equals(RecordedArguments? other)
        {
            if (other == null || Fields.Count != other.Fields.Count)
            {
                return false;
            }

            for (var index = 0; index < Fields.Count; index++)
            {
                if (!Fields[index].Equals(other.Fields[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as RecordedArguments);

        public override int GetHashCode()
        {
            var hash = Fields.Count;
            for (var index = 0; index < Fields.Count; index++)
            {
                hash = ContractGrammar.CombineHashes(hash, Fields[index].GetHashCode());
            }

            return hash;
        }
    }
}
