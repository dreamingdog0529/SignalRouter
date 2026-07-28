using System;

namespace SignalRouter.Contracts
{
    /// <summary>A stable clause identifier within a predicate definition (verification.md §2.2).</summary>
    public readonly struct ClauseId : IEquatable<ClauseId>
    {
        private readonly string? value;

        public ClauseId(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default ClauseId carries no value.");

        public bool Equals(ClauseId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ClauseId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(ClauseId left, ClauseId right) => left.Equals(right);

        public static bool operator !=(ClauseId left, ClauseId right) => !left.Equals(right);
    }

    /// <summary>
    /// A reference to a secret operand value. The reference is what recordings and
    /// evidence carry; resolution happens only in memory
    /// (security-resources.md §3). An unresolved reference makes the evaluation
    /// <c>Unevaluable(Redacted)</c> — never <c>False</c>.
    /// </summary>
    public readonly struct SecretReference : IEquatable<SecretReference>
    {
        private readonly string? value;

        public SecretReference(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default SecretReference carries no value.");

        public bool Equals(SecretReference other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SecretReference other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => "(secret)";

        public static bool operator ==(SecretReference left, SecretReference right) => left.Equals(right);

        public static bool operator !=(SecretReference left, SecretReference right) => !left.Equals(right);
    }

    /// <summary>The kind of a predicate operand.</summary>
    public enum PredicateOperandKind
    {
        String,
        Integer,
        Boolean,
        Float,

        /// <summary>A secret reference; the value resolves in memory only.</summary>
        SecretReference,
    }

    /// <summary>
    /// A literal operand of a predicate expression. Secret operands travel as
    /// references (verification.md §2.2); in item 2 no resolver exists, so a secret
    /// operand always evaluates <c>Unevaluable(Redacted)</c>.
    /// </summary>
    public readonly struct PredicateOperand : IEquatable<PredicateOperand>
    {
        private readonly FieldValue literal;
        private readonly SecretReference secret;

        private PredicateOperand(PredicateOperandKind kind, FieldValue literal, SecretReference secret)
        {
            Kind = kind;
            this.literal = literal;
            this.secret = secret;
        }

        public static PredicateOperand Of(string value) =>
            new PredicateOperand(PredicateOperandKind.String, FieldValue.Of(value), default);

        public static PredicateOperand Of(long value) =>
            new PredicateOperand(PredicateOperandKind.Integer, FieldValue.Of(value), default);

        public static PredicateOperand Of(bool value) =>
            new PredicateOperand(PredicateOperandKind.Boolean, FieldValue.Of(value), default);

        public static PredicateOperand Of(double value) =>
            new PredicateOperand(PredicateOperandKind.Float, FieldValue.Of(value), default);

        public static PredicateOperand OfSecret(SecretReference reference)
        {
            if (reference.IsDefault)
            {
                throw new ArgumentException(
                    "A secret operand requires a non-default reference.", nameof(reference));
            }

            return new PredicateOperand(PredicateOperandKind.SecretReference, default, reference);
        }

        public PredicateOperandKind Kind { get; }

        public bool IsDefault => literal.IsDefault && secret.IsDefault;

        public FieldValue Literal =>
            Kind != PredicateOperandKind.SecretReference && !literal.IsDefault
                ? literal
                : throw new InvalidOperationException("Only a literal operand carries a value.");

        public SecretReference Secret =>
            Kind == PredicateOperandKind.SecretReference
                ? secret
                : throw new InvalidOperationException("Only a secret operand carries a reference.");

        public bool Equals(PredicateOperand other) =>
            Kind == other.Kind && literal.Equals(other.literal) && secret.Equals(other.secret);

        public override bool Equals(object? obj) => obj is PredicateOperand other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes((int)Kind, literal.GetHashCode());
            return ContractGrammar.CombineHashes(hash, secret.GetHashCode());
        }

        public override string ToString() =>
            Kind == PredicateOperandKind.SecretReference ? "(secret)" : literal.ToString();

        public static bool operator ==(PredicateOperand left, PredicateOperand right) => left.Equals(right);

        public static bool operator !=(PredicateOperand left, PredicateOperand right) => !left.Equals(right);
    }
}
