using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// E6, armed half — an explicit wait (<c>wait_for</c>) was armed
    /// (guarantees.md §5.6). Capability postconditions are NOT E6 cuts; their final
    /// evaluation is embedded in E4.
    /// </summary>
    public sealed class PredicateArmed : EvidenceCut
    {
        public PredicateArmed(
            EvidenceSequence sequence,
            OperationId operationId,
            PredicateContractRef predicate,
            ArgumentDigest operands,
            SemanticFingerprint fingerprint,
            ViewContractRef scope,
            string observationScope,
            Causality causality,
            ViewSequence armedSequence)
            : base(sequence)
        {
            ContractGrammar.ValidateIdentifier(observationScope, nameof(observationScope));
            if (operationId.IsDefault)
            {
                throw new ArgumentException("E6 requires a non-default OperationId.", nameof(operationId));
            }

            if (predicate.IsDefault)
            {
                throw new ArgumentException("E6 requires a non-default predicate reference.", nameof(predicate));
            }

            if (operands.IsDefault)
            {
                throw new ArgumentException("E6 requires a non-default operand digest.", nameof(operands));
            }

            if (fingerprint.IsDefault)
            {
                throw new ArgumentException("E6 requires a non-default fingerprint.", nameof(fingerprint));
            }

            if (scope.IsDefault)
            {
                throw new ArgumentException("E6 requires a non-default view scope.", nameof(scope));
            }

            OperationId = operationId;
            Predicate = predicate;
            Operands = operands;
            Fingerprint = fingerprint;
            Scope = scope;
            ObservationScope = observationScope;
            Causality = causality ?? throw new ArgumentNullException(nameof(causality));
            ArmedSequence = armedSequence;
        }

        public override EvidenceCutKind Kind => EvidenceCutKind.PredicateArmed;

        public OperationId OperationId { get; }

        public PredicateContractRef Predicate { get; }

        public ArgumentDigest Operands { get; }

        public SemanticFingerprint Fingerprint { get; }

        public ViewContractRef Scope { get; }

        /// <summary>
        /// The observation scope the wait evaluates over — with <see cref="Scope"/>
        /// this identifies the materialization replay re-evaluates against
        /// (guarantees.md §5.6; E8 already carries its scope).
        /// </summary>
        public string ObservationScope { get; }

        public Causality Causality { get; }

        /// <summary>The armed evidence sequence within the wait's view subscription.</summary>
        public ViewSequence ArmedSequence { get; }
    }

    /// <summary>
    /// E6, resolved half (guarantees.md §5.6). A <c>Satisfied</c> resolution carries
    /// its witness; every other resolution carries the final observation.
    /// </summary>
    public sealed class PredicateResolved : EvidenceCut
    {
        public PredicateResolved(
            EvidenceSequence sequence,
            OperationId operationId,
            PredicateResolution outcome,
            ContentId witnessOrFinalObservation,
            ViewSequence resolvedSequence)
            : base(sequence)
        {
            if (operationId.IsDefault)
            {
                throw new ArgumentException("E6 requires a non-default OperationId.", nameof(operationId));
            }

            if (witnessOrFinalObservation.IsDefault)
            {
                throw new ArgumentException(
                    "E6 resolution requires the witness or final-observation ContentId.",
                    nameof(witnessOrFinalObservation));
            }

            OperationId = operationId;
            Outcome = outcome;
            WitnessOrFinalObservation = witnessOrFinalObservation;
            ResolvedSequence = resolvedSequence;
        }

        public override EvidenceCutKind Kind => EvidenceCutKind.PredicateResolved;

        public OperationId OperationId { get; }

        public PredicateResolution Outcome { get; }

        /// <summary>The witness (for Satisfied) or the final-observation ContentId.</summary>
        public ContentId WitnessOrFinalObservation { get; }

        /// <summary>The resolved evidence sequence within the wait's view subscription.</summary>
        public ViewSequence ResolvedSequence { get; }
    }
}
