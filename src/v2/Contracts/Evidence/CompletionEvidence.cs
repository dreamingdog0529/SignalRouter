using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The kind of evidence a completion profile demands (semantic-model.md §2.2,
    /// adapter-conformance.md §4). Open code vocabulary with the four standard kinds
    /// as well-known values.
    /// </summary>
    public readonly struct CompletionEvidenceKind : IEquatable<CompletionEvidenceKind>
    {
        private readonly string? value;

        public CompletionEvidenceKind(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        public static CompletionEvidenceKind Applied => new CompletionEvidenceKind("Applied");

        public static CompletionEvidenceKind FrameCommitted => new CompletionEvidenceKind("FrameCommitted");

        public static CompletionEvidenceKind PostconditionSatisfied => new CompletionEvidenceKind("PostconditionSatisfied");

        public static CompletionEvidenceKind AdapterAcknowledged => new CompletionEvidenceKind("AdapterAcknowledged");

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default CompletionEvidenceKind carries no value.");

        public bool Equals(CompletionEvidenceKind other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is CompletionEvidenceKind other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(CompletionEvidenceKind left, CompletionEvidenceKind right) => left.Equals(right);

        public static bool operator !=(CompletionEvidenceKind left, CompletionEvidenceKind right) => !left.Equals(right);
    }

    /// <summary>
    /// The completion evidence a <c>Succeeded</c> terminal carries: the evidence
    /// itself — the material the bound profile demands — not merely the profile
    /// reference (guarantees.md §5.4). The per-profile payload structure is deferred
    /// to adapter-conformance modeling; here it is an opaque digest.
    /// </summary>
    public sealed class CompletionEvidence : IEquatable<CompletionEvidence>
    {
        public CompletionEvidence(CompletionProfileRef profile, CompletionEvidenceKind kind, DigestValue payloadDigest)
        {
            if (profile.IsDefault)
            {
                throw new ArgumentException(
                    "CompletionEvidence requires a non-default profile reference.", nameof(profile));
            }

            if (kind.IsDefault)
            {
                throw new ArgumentException(
                    "CompletionEvidence requires a non-default evidence kind.", nameof(kind));
            }

            Profile = profile;
            Kind = kind;
            PayloadDigest = payloadDigest;
        }

        public CompletionProfileRef Profile { get; }

        public CompletionEvidenceKind Kind { get; }

        /// <summary>Digest of the profile-specific payload; default when the kind alone is the evidence.</summary>
        public DigestValue PayloadDigest { get; }

        public bool Equals(CompletionEvidence? other) =>
            other != null &&
            Profile.Equals(other.Profile) &&
            Kind.Equals(other.Kind) &&
            PayloadDigest.Equals(other.PayloadDigest);

        public override bool Equals(object? obj) => Equals(obj as CompletionEvidence);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(Profile.GetHashCode(), Kind.GetHashCode());
            return ContractGrammar.CombineHashes(hash, PayloadDigest.GetHashCode());
        }

        public override string ToString() => $"{Kind} per {Profile}";
    }
}
