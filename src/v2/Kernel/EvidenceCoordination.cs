using System;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>The answer of an evidence-coordination step (ADR 0010).</summary>
    public enum EvidenceReadiness
    {
        /// <summary>The obligation is durable (or vacuous); execution may proceed.</summary>
        Ready,

        /// <summary>Durability is in flight; retry at a later turn. Execution MUST NOT proceed.</summary>
        Pending,

        /// <summary>The obligation cannot be met (e.g. sink fault). The interaction path answers per the failure matrix.</summary>
        Fault,
    }

    /// <summary>The E2 material a durable coordinator needs (guarantees.md §5.2).</summary>
    public sealed class AdmissionEvidence
    {
        public AdmissionEvidence(
            RequestId request,
            LogicalOrder order,
            SemanticFingerprint fingerprint,
            CapabilityInvocation invocation,
            ResolvedTarget resolvedTarget,
            IdentityEnvelope envelope)
        {
            Request = request;
            Order = order;
            Fingerprint = fingerprint;
            Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
            ResolvedTarget = resolvedTarget;
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        }

        public RequestId Request { get; }

        public LogicalOrder Order { get; }

        public SemanticFingerprint Fingerprint { get; }

        public CapabilityInvocation Invocation { get; }

        public ResolvedTarget ResolvedTarget { get; }

        public IdentityEnvelope Envelope { get; }
    }

    /// <summary>
    /// The E3 material (guarantees.md §5.3): the observation basis the permit fixes.
    /// The before record-view materialization and its ContentId are produced by the
    /// durable coordinator itself (item 5) from this basis.
    /// </summary>
    public sealed class PermitEvidence
    {
        public PermitEvidence(RequestId request, LogicalOrder order, SourceRevision watermark)
        {
            Request = request;
            Order = order;
            Watermark = watermark;
        }

        public RequestId Request { get; }

        public LogicalOrder Order { get; }

        public SourceRevision Watermark { get; }
    }

    /// <summary>The E4 material (guarantees.md §5.4).</summary>
    public sealed class TerminalEvidence
    {
        public TerminalEvidence(
            RequestId request,
            LogicalOrder order,
            InteractionOutcome outcome,
            bool effectPermitted,
            bool effectStarted,
            RejectionReason? rejectionReason,
            FaultCode? faultCode,
            CancellationPhase? cancellationPhase,
            PostconditionResult? postcondition,
            SourceRevision afterWatermark,
            ValueList<ContinuationRequest> continuations)
        {
            Request = request;
            Order = order;
            Outcome = outcome;
            EffectPermitted = effectPermitted;
            EffectStarted = effectStarted;
            RejectionReason = rejectionReason;
            FaultCode = faultCode;
            CancellationPhase = cancellationPhase;
            Postcondition = postcondition;
            AfterWatermark = afterWatermark;
            Continuations = continuations ?? throw new ArgumentNullException(nameof(continuations));
        }

        public RequestId Request { get; }

        public LogicalOrder Order { get; }

        public InteractionOutcome Outcome { get; }

        public bool EffectPermitted { get; }

        public bool EffectStarted { get; }

        public RejectionReason? RejectionReason { get; }

        public FaultCode? FaultCode { get; }

        public CancellationPhase? CancellationPhase { get; }

        public PostconditionResult? Postcondition { get; }

        public SourceRevision AfterWatermark { get; }

        public ValueList<ContinuationRequest> Continuations { get; }
    }

    /// <summary>
    /// The recording seam (ADR 0010): the kernel prepares and commits its evidence
    /// obligations through this coordinator and never through fire-and-forget
    /// notification, because E3 is a durability gate — the permit token is minted
    /// only after <see cref="PrepareEffectPermit"/> answers <see cref="EvidenceReadiness.Ready"/> —
    /// and an E4 commit fault fails the recording alone while the true terminal
    /// stays in the RecoveryIndex (guarantees.md §7). A Pending terminal commit is
    /// retried at later turns; continuations are released only after it answers
    /// Ready. With no recording active the coordinator is the explicit no-op that
    /// answers Ready immediately; the recording module (item 5) supplies the durable
    /// implementation. No placeholder ContentIds exist at this seam — the durable
    /// coordinator owns the cut payloads it builds from these materials.
    /// </summary>
    public interface IEvidenceCoordinator
    {
        /// <summary>The E2 obligation: durable before any UI effect of the interaction (guarantees.md §5.2).</summary>
        EvidenceReadiness PrepareAdmissionEvidence(AdmissionEvidence evidence);

        /// <summary>The E3 obligation: the durable permit that gates the adapter invocation (guarantees.md §5.3).</summary>
        EvidenceReadiness PrepareEffectPermit(PermitEvidence evidence);

        /// <summary>The E4 obligation. A fault here fails the recording alone; the terminal is already committed.</summary>
        EvidenceReadiness CommitTerminalEvidence(TerminalEvidence evidence);
    }

    /// <summary>The explicit recording-off coordinator: every obligation is vacuous and immediately ready.</summary>
    public sealed class NoOpEvidenceCoordinator : IEvidenceCoordinator
    {
        public static NoOpEvidenceCoordinator Instance { get; } = new NoOpEvidenceCoordinator();

        private NoOpEvidenceCoordinator()
        {
        }

        public EvidenceReadiness PrepareAdmissionEvidence(AdmissionEvidence evidence) => EvidenceReadiness.Ready;

        public EvidenceReadiness PrepareEffectPermit(PermitEvidence evidence) => EvidenceReadiness.Ready;

        public EvidenceReadiness CommitTerminalEvidence(TerminalEvidence evidence) => EvidenceReadiness.Ready;
    }
}
