using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// One entry of a parent E4's ordered commitment list: the child it committed to
    /// spawn (guarantees.md §5.8). A child is admitted only after the parent's E4 is
    /// durable; replay binds by <c>(ParentRequestId, ContinuationOrdinal)</c>.
    /// </summary>
    public readonly struct ContinuationCommitment : IEquatable<ContinuationCommitment>
    {
        public ContinuationCommitment(int ordinal, SemanticFingerprint fingerprint)
        {
            if (ordinal < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ordinal), "Continuation ordinal must not be negative.");
            }

            if (fingerprint.IsDefault)
            {
                throw new ArgumentException(
                    "ContinuationCommitment requires a non-default fingerprint.", nameof(fingerprint));
            }

            Ordinal = ordinal;
            Fingerprint = fingerprint;
        }

        public int Ordinal { get; }

        public SemanticFingerprint Fingerprint { get; }

        public bool Equals(ContinuationCommitment other) =>
            Ordinal == other.Ordinal && Fingerprint.Equals(other.Fingerprint);

        public override bool Equals(object? obj) => obj is ContinuationCommitment other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(Ordinal, Fingerprint.GetHashCode());

        public override string ToString() => $"#{Ordinal}";

        public static bool operator ==(ContinuationCommitment left, ContinuationCommitment right) => left.Equals(right);

        public static bool operator !=(ContinuationCommitment left, ContinuationCommitment right) => !left.Equals(right);
    }
}
