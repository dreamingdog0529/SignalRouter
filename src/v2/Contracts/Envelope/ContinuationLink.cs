using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The causal binding a continuation's admission carries:
    /// <c>ParentRequestId + ContinuationOrdinal + fingerprint</c>
    /// (guarantees.md §5.2, §5.8). Replay binds live continuations to recorded
    /// children by <c>(ParentRequestId, ContinuationOrdinal)</c>.
    /// </summary>
    public readonly struct ContinuationLink : IEquatable<ContinuationLink>
    {
        public ContinuationLink(RequestId parentRequestId, int continuationOrdinal, SemanticFingerprint fingerprint)
        {
            if (parentRequestId.IsDefault)
            {
                throw new ArgumentException(
                    "ContinuationLink requires a non-default parent RequestId.", nameof(parentRequestId));
            }

            if (continuationOrdinal < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(continuationOrdinal), "Continuation ordinal must not be negative.");
            }

            if (fingerprint.IsDefault)
            {
                throw new ArgumentException(
                    "ContinuationLink requires a non-default fingerprint.", nameof(fingerprint));
            }

            ParentRequestId = parentRequestId;
            ContinuationOrdinal = continuationOrdinal;
            Fingerprint = fingerprint;
        }

        public RequestId ParentRequestId { get; }

        public int ContinuationOrdinal { get; }

        public SemanticFingerprint Fingerprint { get; }

        public bool IsDefault => ParentRequestId.IsDefault;

        public bool Equals(ContinuationLink other) =>
            ParentRequestId.Equals(other.ParentRequestId) &&
            ContinuationOrdinal == other.ContinuationOrdinal &&
            Fingerprint.Equals(other.Fingerprint);

        public override bool Equals(object? obj) => obj is ContinuationLink other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(
                ParentRequestId.GetHashCode(), ContinuationOrdinal);
            return ContractGrammar.CombineHashes(hash, Fingerprint.GetHashCode());
        }

        public override string ToString() =>
            IsDefault ? "(default)" : $"{ParentRequestId}#{ContinuationOrdinal}";

        public static bool operator ==(ContinuationLink left, ContinuationLink right) => left.Equals(right);

        public static bool operator !=(ContinuationLink left, ContinuationLink right) => !left.Equals(right);
    }
}
