using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// The opaque semantic fingerprint of an admission: it covers the capability
    /// contract, the resolved target identity, and the redacted-argument digest
    /// (semantic-model.md §2.2). Its production algorithm is not part of this
    /// contract; only stable ordinal equality is.
    /// </summary>
    public readonly struct SemanticFingerprint : IEquatable<SemanticFingerprint>
    {
        private readonly string? value;

        public SemanticFingerprint(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default SemanticFingerprint carries no value.");

        public bool Equals(SemanticFingerprint other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SemanticFingerprint other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(SemanticFingerprint left, SemanticFingerprint right) => left.Equals(right);

        public static bool operator !=(SemanticFingerprint left, SemanticFingerprint right) => !left.Equals(right);
    }

    /// <summary>
    /// The opaque digest of a redacted argument or operand set. Sensitive values never
    /// appear here — production precedes every store (semantic-model.md §7).
    /// </summary>
    public readonly struct ArgumentDigest : IEquatable<ArgumentDigest>
    {
        private readonly string? value;

        public ArgumentDigest(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default ArgumentDigest carries no value.");

        public bool Equals(ArgumentDigest other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ArgumentDigest other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(ArgumentDigest left, ArgumentDigest right) => left.Equals(right);

        public static bool operator !=(ArgumentDigest left, ArgumentDigest right) => !left.Equals(right);
    }

    /// <summary>Identifies the redaction policy pinned into a recording (guarantees.md §5.1).</summary>
    public readonly struct RedactionPolicyId : IEquatable<RedactionPolicyId>
    {
        private readonly string? value;

        public RedactionPolicyId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default RedactionPolicyId carries no value.");

        public bool Equals(RedactionPolicyId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is RedactionPolicyId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(RedactionPolicyId left, RedactionPolicyId right) => left.Equals(right);

        public static bool operator !=(RedactionPolicyId left, RedactionPolicyId right) => !left.Equals(right);
    }

    /// <summary>
    /// Names the security domain an observation was produced for; content addresses
    /// are namespaced per domain (observation-state.md §5).
    /// </summary>
    public readonly struct SecurityDomainId : IEquatable<SecurityDomainId>
    {
        private readonly string? value;

        public SecurityDomainId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default SecurityDomainId carries no value.");

        public bool Equals(SecurityDomainId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SecurityDomainId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(SecurityDomainId left, SecurityDomainId right) => left.Equals(right);

        public static bool operator !=(SecurityDomainId left, SecurityDomainId right) => !left.Equals(right);
    }
}
