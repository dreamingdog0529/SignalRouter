using System;
using SignalRouter.AdapterSdk;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel
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
            RecordedArguments arguments,
            ResolvedTarget resolvedTarget,
            IdentityEnvelope envelope)
        {
            Request = request;
            Order = order;
            Fingerprint = fingerprint;
            Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
            Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
            ResolvedTarget = resolvedTarget;
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        }

        public RequestId Request { get; }

        public LogicalOrder Order { get; }

        public SemanticFingerprint Fingerprint { get; }

        public CapabilityInvocation Invocation { get; }

        /// <summary>
        /// The portable replay input (ADR 0015): the admitted arguments in
        /// recorded form, projected by the kernel at admission from the live
        /// payload. Re-digesting this form yields
        /// <see cref="CapabilityInvocation.Arguments"/>.
        /// </summary>
        public RecordedArguments Arguments { get; }

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

    /// <summary>
    /// The E4 material (guarantees.md §5.4): everything a durable coordinator
    /// needs to build a valid <see cref="TerminalCut"/> — the completion evidence
    /// and cancellation detail the adapter reported, and the continuation
    /// commitments (ordinal + fingerprint) computed at the terminal decision. The
    /// after-view ContentId deliberately stays out: the coordinator fetches the
    /// retained after-basis through <c>TryGetAfterMaterialization</c>.
    /// </summary>
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
            CancellationEvidence? cancellation,
            PostconditionResult? postcondition,
            CompletionEvidence? completion,
            SourceRevision afterWatermark,
            ValueArray<ContinuationRequest> continuations,
            ValueArray<ContinuationCommitment> commitments)
        {
            if (continuations.Count != commitments.Count)
            {
                throw new ArgumentException(
                    "Commitments and continuations are all-or-nothing and must agree.",
                    nameof(commitments));
            }

            Request = request;
            Order = order;
            Outcome = outcome;
            EffectPermitted = effectPermitted;
            EffectStarted = effectStarted;
            RejectionReason = rejectionReason;
            FaultCode = faultCode;
            Cancellation = cancellation;
            Postcondition = postcondition;
            Completion = completion;
            AfterWatermark = afterWatermark;
            Continuations = continuations;
            Commitments = commitments;
        }

        public RequestId Request { get; }

        public LogicalOrder Order { get; }

        public InteractionOutcome Outcome { get; }

        public bool EffectPermitted { get; }

        public bool EffectStarted { get; }

        public RejectionReason? RejectionReason { get; }

        public FaultCode? FaultCode { get; }

        /// <summary>Full cancellation evidence (requested/observed orders, phase, disposition).</summary>
        public CancellationEvidence? Cancellation { get; }

        public PostconditionResult? Postcondition { get; }

        /// <summary>The adapter's completion evidence; present exactly for Succeeded.</summary>
        public CompletionEvidence? Completion { get; }

        public SourceRevision AfterWatermark { get; }

        /// <summary>
        /// The live continuation batch, admission-side only — it carries raw
        /// invocation payloads and MUST NOT be persisted; the artifact records
        /// <see cref="Commitments"/> alone (guarantees.md §5.8).
        /// </summary>
        public ValueArray<ContinuationRequest> Continuations { get; }

        /// <summary>
        /// The E4 commitments, index-aligned with <see cref="Continuations"/> —
        /// computed before the terminal commits so commitments and later
        /// admissions can never disagree (guarantees.md §5.8).
        /// </summary>
        public ValueArray<ContinuationCommitment> Commitments { get; }
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
