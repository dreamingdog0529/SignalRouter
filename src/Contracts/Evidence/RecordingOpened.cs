using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// E1 — the manifest header and open fence (guarantees.md §5.1). The contract
    /// tables are immutable for the artifact's lifetime; commit order is
    /// StateStore-first (base snapshot durable and pinned, then E1).
    /// </summary>
    public sealed class RecordingOpened : EvidenceCut
    {
        public RecordingOpened(
            EvidenceSequence sequence,
            ReplayComparisonProfileRef profile,
            ViewContractRef recordView,
            RedactionPolicyId redactionPolicy,
            ValueArray<CompletionBinding> completionBindings,
            ValueArray<StateSourceBinding> stateSourceContracts,
            ValueArray<PredicateContractRef> predicateContracts,
            RuntimeIncarnationId incarnation,
            ContentId baseSnapshot)
            : base(sequence)
        {
            if (profile.IsDefault)
            {
                throw new ArgumentException(
                    "E1 requires a non-default comparison profile.", nameof(profile));
            }

            if (recordView.IsDefault)
            {
                throw new ArgumentException(
                    "E1 requires a non-default record view contract.", nameof(recordView));
            }

            if (redactionPolicy.IsDefault)
            {
                throw new ArgumentException(
                    "E1 requires a non-default redaction policy id.", nameof(redactionPolicy));
            }

            if (incarnation.IsDefault)
            {
                throw new ArgumentException(
                    "E1 requires a non-default incarnation id.", nameof(incarnation));
            }

            if (baseSnapshot.IsDefault)
            {
                throw new ArgumentException(
                    "E1 requires a non-default base snapshot ContentId.", nameof(baseSnapshot));
            }

            Profile = profile;
            RecordView = recordView;
            RedactionPolicy = redactionPolicy;
            CompletionBindings = completionBindings;
            StateSourceContracts = stateSourceContracts;
            PredicateContracts = predicateContracts;
            Incarnation = incarnation;
            BaseSnapshot = baseSnapshot;
        }

        public override EvidenceCutKind Kind => EvidenceCutKind.RecordingOpened;

        public ReplayComparisonProfileRef Profile { get; }

        public ViewContractRef RecordView { get; }

        public RedactionPolicyId RedactionPolicy { get; }

        public ValueArray<CompletionBinding> CompletionBindings { get; }

        public ValueArray<StateSourceBinding> StateSourceContracts { get; }

        public ValueArray<PredicateContractRef> PredicateContracts { get; }

        public RuntimeIncarnationId Incarnation { get; }

        public ContentId BaseSnapshot { get; }
    }
}
