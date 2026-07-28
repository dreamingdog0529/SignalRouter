using System;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
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
