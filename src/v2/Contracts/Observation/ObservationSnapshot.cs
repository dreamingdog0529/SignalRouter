using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The snapshot identity tuple (observation-state.md §2): basis (incarnation,
    /// pinned revision, view contract, domain, scope), `ContentId`, and
    /// completeness. On a runtime without the canonical-state codec the snapshot is
    /// <em>unaddressed</em>: the `ContentId` leg is explicitly absent
    /// (<see cref="IsAddressed"/> is false) — honest absence, never a placeholder.
    /// Unaddressed snapshots serve live pinned reads and assertion answers but MUST
    /// NOT participate in recording, the timeline, or any surface that references
    /// state by `ContentId`.
    /// </summary>
    public sealed class ObservationSnapshot : IEquatable<ObservationSnapshot>
    {
        public ObservationSnapshot(ObservationBasis basis, ContentId contentId, CompletenessMap completeness)
        {
            Basis = basis ?? throw new ArgumentNullException(nameof(basis));
            ContentId = contentId;
            Completeness = completeness ?? throw new ArgumentNullException(nameof(completeness));
        }

        public ObservationBasis Basis { get; }

        /// <summary>Default when the snapshot is unaddressed; check <see cref="IsAddressed"/> first.</summary>
        public ContentId ContentId { get; }

        public bool IsAddressed => !ContentId.IsDefault;

        public CompletenessMap Completeness { get; }

        public bool Equals(ObservationSnapshot? other) =>
            other != null &&
            Basis.Equals(other.Basis) &&
            ContentId.Equals(other.ContentId) &&
            Completeness.Equals(other.Completeness);

        public override bool Equals(object? obj) => Equals(obj as ObservationSnapshot);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(Basis.GetHashCode(), ContentId.GetHashCode());
            return ContractGrammar.CombineHashes(hash, Completeness.GetHashCode());
        }

        public override string ToString() =>
            IsAddressed ? $"{Basis} [{ContentId}]" : $"{Basis} [unaddressed]";
    }
}
