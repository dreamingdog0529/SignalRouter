using System;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel
{
    /// <summary>
    /// The E1-pinned contract tables at one moment (guarantees.md §5.1), handed
    /// to the coordinator through <see cref="IRecordObservationServices.SnapshotCatalog"/>.
    /// Contract registration is bootstrap-only in this kernel, so the catalog
    /// cannot change under an active recording.
    /// </summary>
    public sealed class RecordingCatalog
    {
        public RecordingCatalog(
            ValueArray<CompletionBinding> completionBindings,
            ValueArray<StateSourceBinding> stateSourceContracts,
            ValueArray<PredicateContractRef> predicateContracts,
            int stateSourceTableVersion)
        {
            CompletionBindings = completionBindings;
            StateSourceContracts = stateSourceContracts;
            PredicateContracts = predicateContracts;
            StateSourceTableVersion = stateSourceTableVersion;
        }

        public ValueArray<CompletionBinding> CompletionBindings { get; }

        public ValueArray<StateSourceBinding> StateSourceContracts { get; }

        public ValueArray<PredicateContractRef> PredicateContracts { get; }

        public int StateSourceTableVersion { get; }
    }

    /// <summary>The E1 material (guarantees.md §5.1): the drained, based state a recording opens over.</summary>
    public sealed class OpenEvidence
    {
        public OpenEvidence(
            OperationId recording,
            ReplayComparisonProfileRef profile,
            ViewContractRef recordView,
            string scope,
            RedactionPolicyId redactionPolicy,
            RecordingCatalog catalog,
            RecordMaterialization baseSnapshot,
            RuntimeIncarnationId incarnation)
        {
            Recording = recording;
            Profile = profile;
            RecordView = recordView;
            Scope = scope;
            RedactionPolicy = redactionPolicy;
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            BaseSnapshot = baseSnapshot ?? throw new ArgumentNullException(nameof(baseSnapshot));
            Incarnation = incarnation;
        }

        public OperationId Recording { get; }

        public ReplayComparisonProfileRef Profile { get; }

        public ViewContractRef RecordView { get; }

        public string Scope { get; }

        public RedactionPolicyId RedactionPolicy { get; }

        public RecordingCatalog Catalog { get; }

        /// <summary>Already leased to the recording; the coordinator writes the blob and appends E1.</summary>
        public RecordMaterialization BaseSnapshot { get; }

        public RuntimeIncarnationId Incarnation { get; }
    }

    /// <summary>The E7 material (guarantees.md §5.9): the drained, final state a recording closes over.</summary>
    public sealed class CloseEvidence
    {
        public CloseEvidence(
            OperationId recording,
            RecordingCloseReason reason,
            RecordMaterialization finalSnapshot)
        {
            Recording = recording;
            Reason = reason;
            FinalSnapshot = finalSnapshot ?? throw new ArgumentNullException(nameof(finalSnapshot));
        }

        public OperationId Recording { get; }

        public RecordingCloseReason Reason { get; }

        public RecordMaterialization FinalSnapshot { get; }
    }

    /// <summary>How the kernel must enforce admissions while a recording is active (ADR 0015).</summary>
    public enum RecordingAdmissionPolicy
    {
        /// <summary>An invocation resolving to a keyless node is refused (Rejected(UnkeyedTarget)).</summary>
        RefuseUnkeyedTargets = 0,
    }

    /// <summary>
    /// The E5 material (guarantees.md §5.5): one coalesced pump run of external
    /// mutations. The interval endpoints are coordinator-assigned at append —
    /// the kernel supplies detection facts only.
    /// </summary>
    public sealed class BarrierEvidence
    {
        public BarrierEvidence(
            SourceRevision revisionAtDetection,
            string sourceHint,
            ValueArray<RequestId> contaminatedRequests)
        {
            RevisionAtDetection = revisionAtDetection;
            SourceHint = ContractGrammar.ValidateIdentifier(sourceHint, nameof(sourceHint));
            ContaminatedRequests = contaminatedRequests;
        }

        /// <summary>The SourceRevision after the latest coalesced mutation.</summary>
        public SourceRevision RevisionAtDetection { get; }

        /// <summary>The first coalesced mutation's source hint.</summary>
        public string SourceHint { get; }

        /// <summary>The interactions whose effect window overlapped the coalesced mutations.</summary>
        public ValueArray<RequestId> ContaminatedRequests { get; }
    }

    /// <summary>
    /// The E5 answer (ADR 0015): disposition and readiness compose independently.
    /// The disposition comes from the open policy and is valid immediately —
    /// under the terminate policy the close fence starts regardless of the cut's
    /// durability progress; the readiness leg covers only the barrier cut's append.
    /// </summary>
    public readonly struct BarrierAnswer
    {
        private BarrierAnswer(EvidenceReadiness readiness, IncompleteReason? requestedClose)
        {
            Readiness = readiness;
            RequestedClose = requestedClose;
        }

        public EvidenceReadiness Readiness { get; }

        /// <summary>Non-null asks the kernel to drive the ordinary close fence with this reason.</summary>
        public IncompleteReason? RequestedClose { get; }

        public static BarrierAnswer Continue(EvidenceReadiness readiness) =>
            new BarrierAnswer(readiness, null);

        public static BarrierAnswer RequestClose(EvidenceReadiness readiness, IncompleteReason reason)
        {
            if (reason.IsDefault)
            {
                throw new ArgumentException("A close request requires a reason.", nameof(reason));
            }

            return new BarrierAnswer(readiness, reason);
        }
    }

    /// <summary>
    /// A TimelineTrack observation (recording-replay.md §3): an armed wait was
    /// re-evaluated and stayed unsatisfied. Droppable diagnostics — never
    /// evidence, never an obligation.
    /// </summary>
    public sealed class WaitPollEvidence
    {
        public WaitPollEvidence(
            OperationId operation, PredicateContractRef predicate, SourceRevision revision)
        {
            if (operation.IsDefault)
            {
                throw new ArgumentException(
                    "A wait poll requires a non-default operation.", nameof(operation));
            }

            if (predicate.IsDefault)
            {
                throw new ArgumentException(
                    "A wait poll requires a non-default predicate reference.", nameof(predicate));
            }

            Operation = operation;
            Predicate = predicate;
            Revision = revision;
        }

        public OperationId Operation { get; }

        public PredicateContractRef Predicate { get; }

        public SourceRevision Revision { get; }
    }

    /// <summary>
    /// The E6a material (guarantees.md §5.6). No operand values are recorded:
    /// waits arm registered contracts only, and E1 pins the definition — the
    /// digest identifies it (ADR 0015). The view contract and observation scope
    /// come from the coordinator's own open state.
    /// </summary>
    public sealed class WaitArmedEvidence
    {
        public WaitArmedEvidence(
            OperationId operation,
            PredicateContractRef predicate,
            ArgumentDigest operands,
            SemanticFingerprint fingerprint,
            Causality causality,
            ViewSequence armedSequence)
        {
            if (operation.IsDefault)
            {
                throw new ArgumentException("E6 requires a non-default operation.", nameof(operation));
            }

            if (predicate.IsDefault)
            {
                throw new ArgumentException(
                    "E6 requires a non-default predicate reference.", nameof(predicate));
            }

            Operation = operation;
            Predicate = predicate;
            Operands = operands;
            Fingerprint = fingerprint;
            Causality = causality ?? throw new ArgumentNullException(nameof(causality));
            ArmedSequence = armedSequence;
        }

        public OperationId Operation { get; }

        public PredicateContractRef Predicate { get; }

        public ArgumentDigest Operands { get; }

        public SemanticFingerprint Fingerprint { get; }

        public Causality Causality { get; }

        public ViewSequence ArmedSequence { get; }
    }

    /// <summary>
    /// The E6b material (guarantees.md §5.6): the resolution with its witness
    /// (for Satisfied) or final observation — the record-view materialization at
    /// the resolution, already leased to the recording; the coordinator writes
    /// the blob, appends the cut, and releases the cut-level lease.
    /// </summary>
    public sealed class WaitResolvedEvidence
    {
        public WaitResolvedEvidence(
            OperationId operation,
            PredicateResolution resolution,
            RecordMaterialization observation,
            ViewSequence resolvedSequence)
        {
            if (operation.IsDefault)
            {
                throw new ArgumentException("E6 requires a non-default operation.", nameof(operation));
            }

            Operation = operation;
            Resolution = resolution;
            Observation = observation ?? throw new ArgumentNullException(nameof(observation));
            ResolvedSequence = resolvedSequence;
        }

        public OperationId Operation { get; }

        public PredicateResolution Resolution { get; }

        public RecordMaterialization Observation { get; }

        public ViewSequence ResolvedSequence { get; }
    }

    /// <summary>
    /// The E8 material (guarantees.md §5.10): one standalone assertion evaluated
    /// against the record-domain projection (verification.md §3.3), with the
    /// evaluated materialization already leased to the recording.
    /// </summary>
    public sealed class AssertionEvidence
    {
        public AssertionEvidence(
            PredicateContractRef predicate,
            ArgumentDigest operands,
            SecurityDomainId domain,
            int stateSourceTableVersion,
            RecordMaterialization snapshot,
            ValueArray<ClauseEvaluation> clauses,
            PredicateEvaluationOutcome outcome)
        {
            if (predicate.IsDefault)
            {
                throw new ArgumentException(
                    "E8 requires a non-default predicate reference.", nameof(predicate));
            }

            if (domain.IsDefault)
            {
                throw new ArgumentException("E8 requires a non-default domain.", nameof(domain));
            }

            Predicate = predicate;
            Operands = operands;
            Domain = domain;
            StateSourceTableVersion = stateSourceTableVersion;
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Clauses = clauses;
            Outcome = outcome;
        }

        public PredicateContractRef Predicate { get; }

        public ArgumentDigest Operands { get; }

        public SecurityDomainId Domain { get; }

        public int StateSourceTableVersion { get; }

        public RecordMaterialization Snapshot { get; }

        public ValueArray<ClauseEvaluation> Clauses { get; }

        public PredicateEvaluationOutcome Outcome { get; }
    }

    /// <summary>
    /// The recording lifecycle seam (ADR 0015), extending the unchanged
    /// E2/E3/E4 gate. One object owns the durability obligations; fence and
    /// membership truth stays in the kernel state machine — the coordinator
    /// answers durability and requests degradation only through
    /// <see cref="CloseRequested"/>, never by initiating a fence itself.
    /// E5/E6/E8 hooks join this interface with the kernel sites that feed them
    /// (staged; ADR 0015).
    /// </summary>
    public interface IRecordingCoordinator : IEvidenceCoordinator
    {
        /// <summary>
        /// Called by the kernel exactly once, during <c>Start</c>, before any
        /// other callback: the assembly seam for the pump-thread observation
        /// services the coordinator builds cuts with.
        /// </summary>
        void Bind(IRecordObservationServices services);

        /// <summary>E1. Pending is retried at later pumps; Fault answers the open operation OpenFailed.</summary>
        EvidenceReadiness PrepareOpenEvidence(OpenEvidence evidence);

        /// <summary>E7. Pending is retried; Fault answers the close operation Failed (reader: Interrupted).</summary>
        EvidenceReadiness CommitCloseEvidence(CloseEvidence evidence);

        /// <summary>
        /// E5. The kernel re-presents the (possibly grown) coalesced barrier at
        /// later turns while the readiness leg answers Pending; the coordinator
        /// appends exactly one cut per presented barrier when ready. A Fault
        /// raises <see cref="CloseRequested"/> coordinator-side.
        /// </summary>
        BarrierAnswer CommitExternalMutation(BarrierEvidence evidence);

        /// <summary>E6a. Pending parks the evidence kernel-side; Fault raises CloseRequested coordinator-side.</summary>
        EvidenceReadiness CommitWaitArmed(WaitArmedEvidence evidence);

        /// <summary>E6b. Same park-and-retry discipline; the witness lease releases when the cut is durable.</summary>
        EvidenceReadiness CommitWaitResolved(WaitResolvedEvidence evidence);

        /// <summary>E8. Same park-and-retry discipline; the snapshot lease releases with the final answer.</summary>
        EvidenceReadiness CommitAssertionEvidence(AssertionEvidence evidence);

        /// <summary>
        /// Teardown notification, delivered before the kernel clears its stores
        /// — observation services are still addressable, so the coordinator can
        /// attempt a durable Incomplete(IncarnationChanged) close.
        /// </summary>
        void NotifyTeardown();

        /// <summary>
        /// Polled at turn granularity while Active (and while ClosingDraining,
        /// where it downgrades an orderly close's reason to Incomplete, once).
        /// Non-null asks the kernel to drive the ordinary close fence with this
        /// reason (SizeLimit, SinkFault, ExternalMutation under the terminate
        /// policy). The kernel owns the fence; the coordinator never writes E7
        /// on its own initiative, and it clears the request when its recording
        /// ends — a standing value would close the next recording.
        /// </summary>
        IncompleteReason? CloseRequested { get; }

        /// <summary>The admission policy the kernel enforces before E2 while recording.</summary>
        RecordingAdmissionPolicy AdmissionPolicy { get; }

        /// <summary>
        /// TimelineTrack (recording-replay.md §3): offered while Active for an
        /// armed, recorded wait that re-evaluated unsatisfied. Droppable — the
        /// coordinator may coalesce, sample, cap, or ignore it freely; there is
        /// no readiness protocol and no obligation queue involvement.
        /// </summary>
        void OfferWaitPoll(WaitPollEvidence evidence);
    }

    /// <summary>Answers of the split-phase recording control operations. Failed is never an artifact state.</summary>
    public interface IRecordingObserver
    {
        void OnOpened(OperationId recording);

        void OnOpenRefused(OperationId recording, string reasonCode);

        void OnClosed(OperationId recording, RecordingCloseReason reason);

        void OnFailed(OperationId recording, string reasonCode);
    }

    /// <summary>
    /// The split-phase recording control surface (ADR 0015): one recording per
    /// runtime; opening drains in-flight work behind a dedicated admission
    /// freeze, then bases and commits E1; closing drains, resolves armed waits,
    /// and commits E7. Thread-safe entry points; answers arrive on the pump.
    /// </summary>
    public interface IRecordingControl
    {
        /// <summary>Thread-safe: begin opening. Refused immediately when a recording is already open or opening.</summary>
        OperationId OpenRecording(RecordingOpenRequest request, IRecordingObserver observer);

        /// <summary>Thread-safe: begin an orderly close (reason: Completed).</summary>
        void CloseRecording(OperationId recording, IRecordingObserver observer);
    }

    /// <summary>What an open declares (ADR 0015; policies land with the durable coordinator).</summary>
    public sealed class RecordingOpenRequest
    {
        public RecordingOpenRequest(
            ReplayComparisonProfileRef profile,
            ViewContractRef recordView,
            string scope,
            RedactionPolicyId redactionPolicy)
        {
            if (profile.IsDefault)
            {
                throw new ArgumentException(
                    "An open requires a non-default profile reference.", nameof(profile));
            }

            if (recordView.IsDefault)
            {
                throw new ArgumentException(
                    "An open requires a non-default record view.", nameof(recordView));
            }

            if (redactionPolicy.IsDefault)
            {
                throw new ArgumentException(
                    "An open requires a non-default redaction policy.", nameof(redactionPolicy));
            }

            Profile = profile;
            RecordView = recordView;
            Scope = ContractGrammar.ValidateIdentifier(scope, nameof(scope));
            RedactionPolicy = redactionPolicy;
        }

        public ReplayComparisonProfileRef Profile { get; }

        public ViewContractRef RecordView { get; }

        public string Scope { get; }

        public RedactionPolicyId RedactionPolicy { get; }
    }
}
