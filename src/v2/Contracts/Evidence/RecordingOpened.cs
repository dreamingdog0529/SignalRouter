using System;

namespace SignalRouter.V2.Contracts
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
            ValueList<CompletionBinding> completionBindings,
            ValueList<StateSourceBinding> stateSourceContracts,
            ValueList<PredicateContractRef> predicateContracts,
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
            CompletionBindings = completionBindings ?? throw new ArgumentNullException(nameof(completionBindings));
            StateSourceContracts = stateSourceContracts ?? throw new ArgumentNullException(nameof(stateSourceContracts));
            PredicateContracts = predicateContracts ?? throw new ArgumentNullException(nameof(predicateContracts));
            Incarnation = incarnation;
            BaseSnapshot = baseSnapshot;
        }

        public override EvidenceCutKind Kind => EvidenceCutKind.RecordingOpened;

        public ReplayComparisonProfileRef Profile { get; }

        public ViewContractRef RecordView { get; }

        public RedactionPolicyId RedactionPolicy { get; }

        public ValueList<CompletionBinding> CompletionBindings { get; }

        public ValueList<StateSourceBinding> StateSourceContracts { get; }

        public ValueList<PredicateContractRef> PredicateContracts { get; }

        public RuntimeIncarnationId Incarnation { get; }

        public ContentId BaseSnapshot { get; }
    }
}
