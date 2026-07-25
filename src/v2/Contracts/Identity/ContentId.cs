using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// A content-addressed reference to a materialized observation blob:
    /// <c>(digestAlgorithmId, canonicalRepresentationVersion, digest)</c>
    /// (semantic-model.md §5). Equality implies semantic equality; inequality implies
    /// nothing — a differing ContentId routes to the typed semantic comparator, never
    /// directly to <c>Diverged</c>. Production lives in <c>Codec.CanonicalState</c>;
    /// this type owns only the reference and its equality.
    /// </summary>
    public readonly struct ContentId : IEquatable<ContentId>
    {
        private readonly string? digestAlgorithmId;

        public ContentId(string digestAlgorithmId, int canonicalRepresentationVersion, DigestValue digest)
        {
            this.digestAlgorithmId = ContractGrammar.ValidateIdentifier(
                digestAlgorithmId, nameof(digestAlgorithmId));
            if (canonicalRepresentationVersion < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(canonicalRepresentationVersion),
                    "Canonical representation version must be at least 1.");
            }

            if (digest.IsDefault)
            {
                throw new ArgumentException(
                    "ContentId requires a non-default digest.", nameof(digest));
            }

            CanonicalRepresentationVersion = canonicalRepresentationVersion;
            Digest = digest;
        }

        public bool IsDefault => digestAlgorithmId == null;

        public string DigestAlgorithmId => digestAlgorithmId ?? throw new InvalidOperationException(
            "A default ContentId carries no value.");

        public int CanonicalRepresentationVersion { get; }

        public DigestValue Digest { get; }

        public bool Equals(ContentId other) =>
            string.Equals(digestAlgorithmId, other.digestAlgorithmId, StringComparison.Ordinal) &&
            CanonicalRepresentationVersion == other.CanonicalRepresentationVersion &&
            Digest.Equals(other.Digest);

        public override bool Equals(object? obj) => obj is ContentId other && Equals(other);

        public override int GetHashCode()
        {
            var hash = digestAlgorithmId == null
                ? 0
                : StringComparer.Ordinal.GetHashCode(digestAlgorithmId);
            hash = ContractGrammar.CombineHashes(hash, CanonicalRepresentationVersion);
            return ContractGrammar.CombineHashes(hash, Digest.GetHashCode());
        }

        public override string ToString() =>
            IsDefault ? "(default)" : $"{digestAlgorithmId}@{CanonicalRepresentationVersion}:{Digest}";

        public static bool operator ==(ContentId left, ContentId right) => left.Equals(right);

        public static bool operator !=(ContentId left, ContentId right) => !left.Equals(right);
    }
}
