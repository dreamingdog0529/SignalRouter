using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// E5 — a contamination interval, not a point (guarantees.md §5.5): both
    /// endpoints are <see cref="EvidenceSequence"/> positions in this artifact's
    /// evidence stream. Strict replay pre-scans and stops before permitting the
    /// effect of the first contaminated interaction — not at E5's stream position.
    /// </summary>
    public sealed class ExternalMutationBarrier : EvidenceCut
    {
        public ExternalMutationBarrier(
            EvidenceSequence sequence,
            EvidenceSequence lastKnownCleanCut,
            EvidenceSequence firstObservedCut,
            SourceRevision revisionAtDetection,
            string sourceHint,
            ValueArray<RequestId> contaminatedRequests)
            : base(sequence)
        {
            if (firstObservedCut < lastKnownCleanCut)
            {
                throw new ArgumentException(
                    "A contamination interval cannot end before it begins.", nameof(firstObservedCut));
            }

            LastKnownCleanCut = lastKnownCleanCut;
            FirstObservedCut = firstObservedCut;
            RevisionAtDetection = revisionAtDetection;
            SourceHint = ContractGrammar.ValidateIdentifier(sourceHint, nameof(sourceHint));
            ContaminatedRequests = contaminatedRequests;
        }

        public override EvidenceCutKind Kind => EvidenceCutKind.ExternalMutationBarrier;

        public EvidenceSequence LastKnownCleanCut { get; }

        public EvidenceSequence FirstObservedCut { get; }

        public SourceRevision RevisionAtDetection { get; }

        public string SourceHint { get; }

        /// <summary>The interactions whose effect window overlaps the interval, marked contaminated.</summary>
        public ValueArray<RequestId> ContaminatedRequests { get; }
    }
}
