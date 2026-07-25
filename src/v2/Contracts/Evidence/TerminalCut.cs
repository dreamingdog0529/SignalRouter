using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// E4 — one per interaction that reaches a provable terminal while the recording
    /// is active (guarantees.md §5.4). The constructor enforces the single-cut field
    /// rules: an E4 never records <c>OutcomeUnknown</c> (that is the reader's answer
    /// for a missing E4); <c>Rejected</c> implies no permitted effect and a reason;
    /// <c>Faulted</c> requires a fault code, and only the pre-effect evidence failure
    /// (<c>EvidenceUnavailable</c>) may combine <c>Faulted</c> with
    /// <c>effectPermitted = false</c> (guarantees.md §3.1); <c>Succeeded</c> requires
    /// completion evidence; <c>Cancelled</c> requires cancellation evidence
    /// consistent with the permit flag; the after view is present in every E4.
    /// </summary>
    public sealed class TerminalCut : EvidenceCut
    {
        public TerminalCut(
            EvidenceSequence sequence,
            RequestId requestId,
            LogicalOrder logicalOrder,
            InteractionOutcome outcome,
            bool effectPermitted,
            ContentId afterView,
            RejectionReason? rejectionReason = null,
            FaultCode? faultCode = null,
            CompletionEvidence? completionEvidence = null,
            PostconditionResult? postcondition = null,
            CancellationEvidence? cancellation = null,
            ValueList<ContinuationCommitment>? continuations = null)
            : base(sequence)
        {
            if (requestId.IsDefault)
            {
                throw new ArgumentException("E4 requires a non-default RequestId.", nameof(requestId));
            }

            if (outcome == InteractionOutcome.OutcomeUnknown)
            {
                throw new ArgumentException(
                    "An E4 never records OutcomeUnknown; that terminal is the reader's answer for a missing E4.",
                    nameof(outcome));
            }

            if (afterView.IsDefault)
            {
                throw new ArgumentException(
                    "E4 requires a non-default after-view ContentId (guarantees.md §5.4).", nameof(afterView));
            }

            if (outcome == InteractionOutcome.Rejected)
            {
                if (effectPermitted)
                {
                    throw new ArgumentException(
                        "Rejected MUST imply effectPermitted = false (guarantees.md §3.1).", nameof(effectPermitted));
                }

                if (rejectionReason == null || rejectionReason.Value.IsDefault)
                {
                    throw new ArgumentException(
                        "A Rejected terminal requires a rejection reason.", nameof(rejectionReason));
                }
            }
            else if (rejectionReason != null)
            {
                throw new ArgumentException(
                    "Only a Rejected terminal carries a rejection reason.", nameof(rejectionReason));
            }

            if (outcome == InteractionOutcome.Faulted)
            {
                if (faultCode == null || faultCode.Value.IsDefault)
                {
                    throw new ArgumentException(
                        "A Faulted terminal requires a stable fault code.", nameof(faultCode));
                }

                if (!effectPermitted && faultCode.Value != Contracts.FaultCode.EvidenceUnavailable)
                {
                    throw new ArgumentException(
                        "Only the pre-effect evidence failure (EvidenceUnavailable) may fault without a permitted effect (guarantees.md §3.1).",
                        nameof(faultCode));
                }

                if (effectPermitted && faultCode.Value == Contracts.FaultCode.EvidenceUnavailable)
                {
                    throw new ArgumentException(
                        "EvidenceUnavailable names the pre-effect evidence failure; it implies no permitted effect.",
                        nameof(faultCode));
                }
            }
            else if (faultCode != null)
            {
                throw new ArgumentException(
                    "Only a Faulted terminal carries a fault code.", nameof(faultCode));
            }

            if (outcome == InteractionOutcome.Succeeded)
            {
                if (!effectPermitted)
                {
                    throw new ArgumentException(
                        "A Succeeded terminal implies a permitted effect.", nameof(effectPermitted));
                }

                if (completionEvidence == null)
                {
                    throw new ArgumentException(
                        "A Succeeded terminal requires completion evidence (guarantees.md §5.4).",
                        nameof(completionEvidence));
                }
            }
            else if (completionEvidence != null)
            {
                throw new ArgumentException(
                    "Completion evidence is required exactly for Succeeded (guarantees.md §5.4).",
                    nameof(completionEvidence));
            }

            if (outcome == InteractionOutcome.Cancelled)
            {
                if (cancellation == null)
                {
                    throw new ArgumentException(
                        "A Cancelled terminal requires cancellation evidence (guarantees.md §5.7).",
                        nameof(cancellation));
                }
            }

            if (cancellation != null && cancellation.EffectPermitted != effectPermitted)
            {
                throw new ArgumentException(
                    "Cancellation evidence must agree with the terminal's permit flag.", nameof(cancellation));
            }

            RequestId = requestId;
            LogicalOrder = logicalOrder;
            Outcome = outcome;
            EffectPermitted = effectPermitted;
            AfterView = afterView;
            RejectionReason = rejectionReason;
            FaultCode = faultCode;
            CompletionEvidence = completionEvidence;
            Postcondition = postcondition;
            Cancellation = cancellation;
            Continuations = continuations ?? ValueList<ContinuationCommitment>.Empty;
        }

        public override EvidenceCutKind Kind => EvidenceCutKind.TerminalCut;

        public RequestId RequestId { get; }

        public LogicalOrder LogicalOrder { get; }

        public InteractionOutcome Outcome { get; }

        public bool EffectPermitted { get; }

        /// <summary>
        /// The after record-view: the fresh after-effect materialization for a
        /// permitted effect, or the observation state a zero-effect terminal was
        /// decided against (guarantees.md §5.4).
        /// </summary>
        public ContentId AfterView { get; }

        public RejectionReason? RejectionReason { get; }

        public FaultCode? FaultCode { get; }

        public CompletionEvidence? CompletionEvidence { get; }

        /// <summary>The final capability-postcondition evaluation, when one contributed to the terminal.</summary>
        public PostconditionResult? Postcondition { get; }

        public CancellationEvidence? Cancellation { get; }

        /// <summary>The ordered continuation commitments (guarantees.md §5.8).</summary>
        public ValueList<ContinuationCommitment> Continuations { get; }
    }
}
