using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>
    /// The single-owner kernel (kernel-execution.md, ADR 0001/0010): one logical
    /// owner mutates interaction state; every input crosses the mailbox; the host
    /// pumps it. This is one runtime incarnation — teardown ends it.
    /// </summary>
    public sealed class KernelRuntime : IPumpable
    {
        private const string KernelViewId = "kernel-raw";

        private readonly KernelOptions options;
        private readonly IEvidenceCoordinator coordinator;
        private readonly Mailbox mailbox;
        private readonly NodeStore nodeStore;
        private readonly SourceSlotTable sourceTable;
        private readonly RecoveryIndex recoveryIndex;
        private readonly StatusBoard statusBoard;
        private readonly KernelTraceRing trace;
        private readonly ViewContractRef kernelView;

        private readonly Dictionary<CapabilityContractRef, CapabilityContractDescriptor> capabilityContracts =
            new Dictionary<CapabilityContractRef, CapabilityContractDescriptor>();

        private readonly Dictionary<PredicateContractRef, PredicateDefinition> predicateContracts =
            new Dictionary<PredicateContractRef, PredicateDefinition>();

        private readonly Dictionary<ViewContractRef, ViewContractDescriptor> viewContracts =
            new Dictionary<ViewContractRef, ViewContractDescriptor>();

        private readonly Dictionary<OperationId, PinnedSnapshot> pinnedSnapshots =
            new Dictionary<OperationId, PinnedSnapshot>();

        private readonly Queue<SnapshotRequestMessage> deferredSnapshots =
            new Queue<SnapshotRequestMessage>();

        private readonly StateStore stateStore;
        private readonly TimelineIndex timeline;

        private readonly Dictionary<RequestId, RecordMaterialization> afterBases =
            new Dictionary<RequestId, RecordMaterialization>();

        private readonly Queue<Interaction> admitted = new Queue<Interaction>();
        private readonly List<Interaction> committingEvidence = new List<Interaction>();
        private readonly Dictionary<OperationId, WaitEntry> waits = new Dictionary<OperationId, WaitEntry>();

        // Timeout ordering for the armed waits: due entries pop in O(log n)
        // instead of a per-pump scan, and every resolution path removes its
        // entry for real, so a wait armed without a timeout can never linger as
        // a tombstone (performance-track finding A4).
        private readonly DeadlineIndex<OperationId> waitDeadlines = new DeadlineIndex<OperationId>();

        private Interaction? active;
        private SubmissionMessage? stalledAdmission;
        private bool started;
        private readonly ProjectionScratch projectionScratch = new ProjectionScratch();
        private bool sampledVisibleToAgent;
        private bool sampledVisibleToRecord;
        private ViewContractDescriptor? kernelRawRecordView;
        private ViewContractDescriptor? kernelRawAgentView;
        private bool tornDown;
        private int pumping;
        private long lastMonotonic = long.MinValue;
        private ulong logicalOrderCounter;
        private ulong nonceCounter;
        private long waitCounter;
        private ulong lastWaitEvaluationRevision;
        private long currentLogicalNow;
        private int pumpObservationBytesRemaining;
        private int pumpObservationNodesRemaining;
        private bool deferredSnapshotServed;
        private ulong timelineObservedRevision;
        private bool timelineGapPending;
        private ulong timelineSequence;
        private ulong lastCauseRevision;
        private LogicalOrder? lastCause;
        private SecurityDomainId gateHolder;
        private bool gated;
        private IEffectExecutor? executor;
        private volatile bool executingEffect;

        public KernelRuntime(
            RuntimeIncarnationId incarnation,
            KernelOptions options,
            IEvidenceCoordinator? evidenceCoordinator = null)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            coordinator = evidenceCoordinator ?? NoOpEvidenceCoordinator.Instance;
            mailbox = new Mailbox(options);
            nodeStore = new NodeStore(incarnation);
            sourceTable = new SourceSlotTable();
            recoveryIndex = new RecoveryIndex(
                options.RecoveryIndexPendingCapacity,
                options.RecoveryIndexTerminalCapacity,
                options.RecoveryIndexTerminalRetentionLogicalTime);
            statusBoard = new StatusBoard();
            trace = new KernelTraceRing(options.TraceRingCapacity, options.TraceRingByteCapacity);
            kernelView = new ViewContractRef(new ViewContractId(KernelViewId), new ContractVersion(1, 0));
            stateStore = new StateStore(options.StateStoreMaxBlobBytes, options.StateStoreMaxTotalBytes);
            timeline = new TimelineIndex(stateStore, options);
            Bootstrap = new BootstrapRegistry(this);
            Registry = new NodeRegistryFacade(this);
            Ingress = new IngressSink(this);
            Completions = new CompletionSink(this);
            Control = new ControlFacade(this);
            RecordObservation = new RecordObservationFacade(this);
        }

        public RuntimeIncarnationId Incarnation => nodeStore.Incarnation;

        public IBootstrapRegistry Bootstrap { get; }

        public INodeRegistry Registry { get; }

        public IIngressSink Ingress { get; }

        public IEffectCompletionSink Completions { get; }

        public IKernelQueries Queries => statusBoard;

        public IKernelControl Control { get; }

        public KernelTraceRing Trace => trace;

        /// <summary>The diagnostic state timeline (observation-state.md §8); reading is principal-bound default-deny.</summary>
        public TimelineIndex Timeline => timeline;

        /// <summary>The StateStore-first capability the recording module consumes (ADR 0011). Pump-thread only.</summary>
        public IRecordObservationServices RecordObservation { get; }

        /// <summary>Wires the adapter surfaces and freezes the bootstrap registry.</summary>
        public void Start(IEffectExecutor effectExecutor)
        {
            if (started)
            {
                throw new KernelFaultException("The runtime is already started.");
            }

            executor = effectExecutor ?? throw new ArgumentNullException(nameof(effectExecutor));
            executor.Attach(Completions);

            // Source registration is bootstrap-only, so the sampled-exposure
            // answer per family is frozen from here on — cache it instead of
            // rescanning the registry per armed wait (review finding on P1d).
            sampledVisibleToAgent = sourceTable.HasSampledVisibleTo(ViewFamily.Agent);
            sampledVisibleToRecord = sourceTable.HasSampledVisibleTo(ViewFamily.Record);
            started = true;
        }

        // ── Pump ─────────────────────────────────────────────────────────────

        public PumpReport Pump(PumpBudget budget)
        {
            if (Interlocked.CompareExchange(ref pumping, 1, 0) != 0)
            {
                throw new KernelFaultException("Concurrent pumping: the kernel has a single consumer.");
            }

            try
            {
                if (!started)
                {
                    throw new KernelFaultException("Pump before Start.");
                }

                CheckMonotonic();
                currentLogicalNow = budget.LogicalNow.Value;
                pumpObservationBytesRemaining = options.ObservationBudgetBytes;
                pumpObservationNodesRemaining = options.ObservationBudgetNodes;
                deferredSnapshotServed = false;

                if (!tornDown)
                {
                    // The status snapshot republishes on change only: admission,
                    // terminal commit, and teardown publish at their mutation
                    // sites, and retention expiry publishes here exactly when it
                    // removed entries. A quiescent pump rebuilds nothing — the
                    // idle cost no longer scales with retained terminals
                    // (performance-track finding A1).
                    if (recoveryIndex.ExpireTerminals(currentLogicalNow))
                    {
                        PublishStatus();
                    }

                    ResolveTimedOutWaits();
                }

                var turns = 0;
                while (turns < budget.MaxTurns && !DeadlinePassed(budget))
                {
                    if (!RunOneTurn())
                    {
                        break;
                    }

                    turns++;
                    ReevaluateWaitsIfRevisionAdvanced();
                }

                if (!tornDown)
                {
                    RunCheckpointFeed(budget);
                }

                return BuildReport(turns);
            }
            finally
            {
                Volatile.Write(ref pumping, 0);
            }
        }

        private bool RunOneTurn()
        {
            // A deferred snapshot restarts against the fresh pump budget before any
            // newly adopted control work — oldest first, once per pump, so
            // continuous control traffic can never starve it (ADR 0011).
            if (!tornDown && !deferredSnapshotServed && deferredSnapshots.Count > 0)
            {
                deferredSnapshotServed = true;
                ProcessSnapshotRequest(deferredSnapshots.Dequeue());
                return true;
            }

            if (mailbox.TryDequeueControl(out var control))
            {
                ProcessControl(control);
                return true;
            }

            if (TryProgressCommitting())
            {
                return true;
            }

            if (tornDown)
            {
                // The incarnation is over: fence stale submissions and drop stale
                // publications, always with an explicit answer or trace.
                if (mailbox.TryDequeueSubmission(out var staleSubmission))
                {
                    Reject(staleSubmission.Submission, RejectionReason.IncarnationMismatch);
                    return true;
                }

                if (mailbox.TryDequeuePublication(out var stalePublication))
                {
                    Emit(
                        new EventKind("PublicationRejected"),
                        stalePublication.Publication.Causation,
                        detailCode: "TornDown");
                    return true;
                }

                return false;
            }

            if (mailbox.TryDequeuePublication(out var publication))
            {
                AdoptPublication(publication);
                return true;
            }

            if (stalledAdmission != null)
            {
                var stalled = stalledAdmission;
                stalledAdmission = null;
                ProcessSubmission(stalled);
                return true;
            }

            if (mailbox.TryDequeueSubmission(out var submission))
            {
                ProcessSubmission(submission);
                return true;
            }

            if (active == null && admitted.Count > 0)
            {
                active = admitted.Dequeue();
                return true;
            }

            if (active != null && StepActive())
            {
                return true;
            }

            return false;
        }

        private void CheckMonotonic()
        {
            var now = options.MonotonicClock.Now;
            if (now < lastMonotonic)
            {
                throw new KernelFaultException("The injected monotonic clock moved backwards.");
            }

            lastMonotonic = now;
        }

        private bool DeadlinePassed(PumpBudget budget)
        {
            var now = options.MonotonicClock.Now;
            if (now < lastMonotonic)
            {
                throw new KernelFaultException("The injected monotonic clock moved backwards.");
            }

            lastMonotonic = now;
            return now >= budget.Deadline;
        }

        private PumpReport BuildReport(int turns)
        {
            var awaitingCompletion = active != null &&
                active.State == InteractionState.WaitingCompletion;
            // One lock acquisition for all three depths (they were previously
            // read twice each, six lock round-trips per pump).
            mailbox.ReadDepths(out var controlDepth, out var publicationDepth, out var submissionDepth);
            // A lane blocked on the adapter makes queued mutation work
            // non-processable; the report never claims otherwise.
            var workRemaining =
                controlDepth > 0 ||
                submissionDepth > 0 ||
                (!tornDown && (
                    publicationDepth > 0 ||
                    stalledAdmission != null ||
                    committingEvidence.Count > 0 ||
                    deferredSnapshots.Count > 0 ||
                    (!awaitingCompletion && (admitted.Count > 0 || active != null))));
            return new PumpReport(
                turns,
                workRemaining,
                controlDepth,
                publicationDepth,
                submissionDepth,
                awaitingCompletion,
                awaitingFramePhase: null);
        }

        // ── Admission (kernel-execution.md §3) ───────────────────────────────

        private void ProcessSubmission(SubmissionMessage message)
        {
            var submission = message.Submission;
            if (tornDown)
            {
                Reject(submission, RejectionReason.IncarnationMismatch);
                return;
            }

            // Resolve the target for this principal; unregistered, unresolvable,
            // and unexposed answer identically (guarantees.md §3.5).
            if (!options.TryResolveDomain(submission.Envelope.Principal, out var domain) ||
                !TryResolveVisibleTarget(submission.Target, domain, out var record))
            {
                Reject(submission, RejectionReason.TargetNotFound);
                return;
            }

            if (!capabilityContracts.TryGetValue(submission.Capability, out var descriptor) ||
                !record.Availability.ContainsKey(submission.Capability))
            {
                // Undeclared, unregistered, and unexposed capability on a visible
                // node merge into one code (guarantees.md §3.5).
                Reject(submission, RejectionReason.CapabilityUnavailable);
                return;
            }

            if (gated && !domain.Equals(gateHolder))
            {
                if (submission.Envelope.Provenance == Provenance.HumanDirected)
                {
                    Emit(EventKind.HumanIntentBlocked, EventCausation.None, request: submission.Request);
                }

                Reject(submission, new RejectionReason("AdmissionGated"));
                return;
            }

            CanonicalInvocation canonical;
            try
            {
                canonical = InvocationCanonicalizer.Canonicalize(
                    submission.Capability,
                    new ResolvedTarget(record.Reference, record.Registration.AuthorKey),
                    submission.Payload,
                    descriptor.Arguments,
                    options.RedactionKey);
            }
            catch (ArgumentException)
            {
                Reject(submission, new RejectionReason("MalformedArguments"));
                return;
            }

            // Dedup by (incarnation, RequestId) with fingerprint verification.
            if (recoveryIndex.TryGetPending(submission.Request, out var pendingEntry))
            {
                if (pendingEntry.Fingerprint.Equals(canonical.Fingerprint))
                {
                    submission.Observer?.OnAccepted(submission.Request);
                }
                else
                {
                    Reject(submission, RejectionReason.RequestIdConflict);
                }

                return;
            }

            if (recoveryIndex.TryGetTerminal(submission.Request, out var terminalEntry))
            {
                if (terminalEntry.Fingerprint.Equals(canonical.Fingerprint))
                {
                    submission.Observer?.OnAccepted(submission.Request);
                }
                else
                {
                    Reject(submission, RejectionReason.RequestIdConflict);
                }

                return;
            }

            if (recoveryIndex.AtCapacity)
            {
                Reject(submission, RejectionReason.CapacityExhausted);
                return;
            }

            var resolvedTarget = new ResolvedTarget(record.Reference, record.Registration.AuthorKey);
            var invocation = new CapabilityInvocation(
                submission.Capability, submission.Target, canonical.Arguments);
            var order = new LogicalOrder(logicalOrderCounter + 1);
            switch (coordinator.PrepareAdmissionEvidence(new AdmissionEvidence(
                submission.Request, order, canonical.Fingerprint, invocation,
                resolvedTarget, submission.Envelope)))
            {
                case EvidenceReadiness.Pending:
                    stalledAdmission = message;
                    return;
                case EvidenceReadiness.Fault:
                    Reject(submission, new RejectionReason("EvidenceUnavailable"));
                    return;
            }

            logicalOrderCounter++;
            recoveryIndex.RegisterPending(
                submission.Request, canonical.Fingerprint, submission.Envelope.Principal, order);
            var interaction = new Interaction(
                submission.Request,
                order,
                canonical.Fingerprint,
                invocation,
                submission.Payload,
                submission.Envelope,
                domain,
                record.Reference,
                descriptor);
            admitted.Enqueue(interaction);
            PublishStatus();
            submission.Observer?.OnAccepted(submission.Request);
            Emit(
                EventKind.Admitted,
                CausationOf(submission.Envelope),
                request: submission.Request,
                order: order);
        }

        private void Reject(IntentSubmission submission, RejectionReason reason)
        {
            submission.Observer?.OnRejected(submission.Request, reason);
        }

        private bool TryResolveVisibleTarget(
            TargetReference target, SecurityDomainId domain, out NodeRecord record)
        {
            var resolved =
                target.Kind == TargetReferenceKind.AuthorKey
                    ? nodeStore.TryResolveByKey(target.Key, out record)
                    : nodeStore.TryResolveLive(target.Node, out record);
            if (!resolved)
            {
                record = null!;
                return false;
            }

            if (!record.Registration.Exposure.IsVisibleTo(domain))
            {
                record = null!;
                return false;
            }

            return true;
        }

        private static EventCausation CausationOf(IdentityEnvelope envelope)
        {
            var continuation = envelope.Causality.Continuation;
            return continuation.HasValue
                ? EventCausation.OfRequest(continuation.Value.ParentRequestId)
                : EventCausation.None;
        }

        // ── Interaction state machine (kernel-execution.md §5) ───────────────

        private bool StepActive()
        {
            var interaction = active!;
            switch (interaction.State)
            {
                case InteractionState.Validating:
                    return StepValidating(interaction);
                case InteractionState.Invoking:
                    return StepInvoking(interaction);
                case InteractionState.WaitingCompletion:
                    return StepWaitingCompletion(interaction);
                case InteractionState.Observing:
                    return StepObserving(interaction);
                case InteractionState.CommittingEvidence:
                    return TryCommitEvidence(interaction);
                default:
                    return false;
            }
        }

        private bool StepValidating(Interaction interaction)
        {
            if (interaction.CancellationRequested)
            {
                Terminate(interaction, TerminalDetails.Cancelled(CancellationPhase.BeforeEffect));
                return true;
            }

            // Re-resolve: the node may have changed while queued.
            if (!nodeStore.TryResolveLive(interaction.Target, out var record) ||
                !record.Registration.Exposure.IsVisibleTo(interaction.Domain))
            {
                Terminate(interaction, TerminalDetails.Rejected(RejectionReason.TargetNotFound));
                return true;
            }

            if (!record.Availability.TryGetValue(interaction.Descriptor.Contract, out var available) ||
                !available)
            {
                Terminate(interaction, TerminalDetails.Rejected(RejectionReason.CapabilityUnavailable));
                return true;
            }

            if (interaction.Descriptor.Precondition != null)
            {
                var reader = PinReader(interaction.Domain);
                var result = PredicateEvaluator.Evaluate(
                    interaction.Descriptor.Precondition, reader, PredicateStructuralBounds.Default);
                if (result.Outcome.Kind != PredicateEvaluationKind.Satisfied)
                {
                    Terminate(interaction, TerminalDetails.Rejected(RejectionReason.PreconditionFailed));
                    return true;
                }
            }

            interaction.State = InteractionState.Invoking;
            Emit(
                EventKind.StateTransition, EventCausation.OfRequest(interaction.Request),
                request: interaction.Request, order: interaction.Order, detailCode: "Invoking");
            return true;
        }

        private bool StepInvoking(Interaction interaction)
        {
            if (interaction.CancellationRequested)
            {
                Terminate(interaction, TerminalDetails.Cancelled(CancellationPhase.BeforeEffect));
                return true;
            }

            switch (coordinator.PrepareEffectPermit(new PermitEvidence(
                interaction.Request, interaction.Order, nodeStore.Revision)))
            {
                case EvidenceReadiness.Pending:
                    return false;
                case EvidenceReadiness.Fault:
                    // The pre-effect evidence failure (guarantees.md §3.1).
                    Terminate(interaction, TerminalDetails.Faulted(
                        FaultCode.EvidenceUnavailable, effectPermitted: false, effectStarted: false));
                    return true;
            }

            var permit = new EffectPermitToken(
                interaction.Request, Incarnation, ++nonceCounter);
            interaction.Permit = permit;
            interaction.EffectPermitted = true;
            Emit(
                EventKind.EffectPermitted, EventCausation.OfRequest(interaction.Request),
                request: interaction.Request, order: interaction.Order);

            EffectAdoption adoption;
            executingEffect = true;
            try
            {
                adoption = executor!.Execute(new EffectRequest(
                    interaction.Invocation,
                    interaction.Payload!,
                    interaction.Target,
                    permit,
                    interaction.Descriptor.CompletionProfile));
            }
            catch (Exception)
            {
                // A throw cannot prove the no-effect-before-adoption rule was
                // honored: possibly effected, redacted stable code
                // (kernel-execution.md §5).
                interaction.EffectStarted = true;
                Terminate(interaction, TerminalDetails.Faulted(
                    new FaultCode("ExecutorFault"), effectPermitted: true, effectStarted: true));
                return true;
            }
            finally
            {
                executingEffect = false;
            }

            if (!adoption.IsAdopted)
            {
                Terminate(interaction, TerminalDetails.Faulted(
                    adoption.RefusalCode, effectPermitted: true, effectStarted: false));
                return true;
            }

            interaction.EffectStarted = true;
            interaction.State = InteractionState.WaitingCompletion;
            return true;
        }

        private bool StepWaitingCompletion(Interaction interaction)
        {
            // The after basis is taken only once the fence is real: for
            // AdapterAcknowledged the completion never implies it (ADR 0010).
            if (interaction.Completion != null && interaction.Fenced)
            {
                interaction.State = InteractionState.Observing;
                return true;
            }

            if (interaction.CancellationRequested && !interaction.CancellationDelivered)
            {
                interaction.CancellationDelivered = true;
                executor!.RequestCancel(interaction.Permit!.Value);
                return true;
            }

            return false;
        }

        private bool StepObserving(Interaction interaction)
        {
            var completion = interaction.Completion!;
            switch (completion.Resolution.Kind)
            {
                case EffectResolutionKind.Faulted:
                    Terminate(interaction, TerminalDetails.Faulted(
                        completion.Resolution.FaultCode, effectPermitted: true, effectStarted: true));
                    return true;

                case EffectResolutionKind.Cancelled:
                    Terminate(interaction, TerminalDetails.Cancelled(
                        completion.Resolution.CancellationPhase,
                        completion.Resolution.CancellationDisposition));
                    return true;
            }

            // Succeeded: pin the after basis and evaluate the contract
            // postcondition against it (verification.md §2.1). A single evaluation
            // against the pinned basis: watch-style postconditions arrive with
            // frame-fenced observation in later items.
            if (interaction.Descriptor.Postcondition != null)
            {
                var reader = PinReader(options.RecordDomain);
                var result = PredicateEvaluator.Evaluate(
                    interaction.Descriptor.Postcondition, reader, PredicateStructuralBounds.Default);
                switch (result.Outcome.Kind)
                {
                    case PredicateEvaluationKind.False:
                        Terminate(interaction, TerminalDetails.Faulted(
                            FaultCode.CompletionPostconditionNotSatisfied,
                            effectPermitted: true,
                            effectStarted: true,
                            postcondition: PostconditionResult.False));
                        return true;
                    case PredicateEvaluationKind.Unevaluable:
                        Terminate(interaction, TerminalDetails.Faulted(
                            FaultCode.CompletionPostconditionNotSatisfied,
                            effectPermitted: true,
                            effectStarted: true,
                            postcondition: PostconditionResult.Unknown));
                        return true;
                }
            }

            Terminate(interaction, TerminalDetails.Succeeded(
                interaction.Descriptor.Postcondition != null
                    ? PostconditionResult.Satisfied
                    : (PostconditionResult?)null));
            return true;
        }

        private void Terminate(Interaction interaction, TerminalDetails details)
        {
            // The true terminal is committed to the RecoveryIndex first: a recording
            // failure never rewrites an interaction's real result (guarantees.md §7).
            recoveryIndex.CommitTerminal(interaction.Request, details.Outcome, currentLogicalNow);
            PublishStatus();

            // The exact after-basis (kernel-execution.md §5, ADR 0011): captured at
            // the terminal decision — after the true terminal is committed, so a
            // codec failure degrades the recording alone — and retained until the
            // terminal evidence commits. E4 uses this, never a fresh materialization.
            CaptureAfterBasis(interaction);

            // The ephemeral payload's lifetime ends at the terminal
            // (kernel-execution.md §3).
            interaction.Payload = null;
            interaction.PendingTerminal = new TerminalEvidence(
                interaction.Request,
                interaction.Order,
                details.Outcome,
                interaction.EffectPermitted,
                interaction.EffectStarted,
                details.RejectionReason,
                details.FaultCode,
                details.CancellationPhase,
                details.Postcondition,
                nodeStore.Revision,
                interaction.Completion?.Continuations ?? ValueArray<ContinuationRequest>.Empty);
            interaction.State = InteractionState.CommittingEvidence;
            if (!TryCommitEvidence(interaction) && !ReferenceEquals(active, interaction))
            {
                committingEvidence.Add(interaction);
            }
        }

        /// <summary>
        /// Retries the E4 obligation. Pending keeps the interaction (and, when
        /// active, the mutation lane) held; continuations are released only after a
        /// final answer — children are never admitted before the parent's terminal
        /// evidence is durable (kernel-execution.md §9).
        /// </summary>
        private bool TryCommitEvidence(Interaction interaction)
        {
            var answer = coordinator.CommitTerminalEvidence(interaction.PendingTerminal!);
            if (answer == EvidenceReadiness.Pending)
            {
                return false;
            }

            if (answer == EvidenceReadiness.Fault)
            {
                // The recording alone fails; the terminal already stands.
                Emit(
                    EventKind.TerminalCommitted, EventCausation.OfRequest(interaction.Request),
                    request: interaction.Request, order: interaction.Order,
                    detailCode: "RecordingCommitFault");
            }

            FinishTerminal(interaction);
            return true;
        }

        private bool TryProgressCommitting()
        {
            for (var i = 0; i < committingEvidence.Count; i++)
            {
                var interaction = committingEvidence[i];
                if (TryCommitEvidence(interaction))
                {
                    // Swap-remove: retry order among parked commits is
                    // unspecified (cross-request evidence interleave is normal,
                    // guarantees.md §6.2) and this stays deterministic.
                    var last = committingEvidence.Count - 1;
                    committingEvidence[i] = committingEvidence[last];
                    committingEvidence.RemoveAt(last);
                    return true;
                }
            }

            return false;
        }

        private void FinishTerminal(Interaction interaction)
        {
            // The terminal evidence has its final answer (Ready or Fault): the
            // retained after-basis is released; a Pending retry keeps it held.
            if (afterBases.TryGetValue(interaction.Request, out var retainedBasis))
            {
                afterBases.Remove(interaction.Request);
                stateStore.Release(
                    options.RecordDomain,
                    retainedBasis.Snapshot.ContentId,
                    LeaseOwner.OfRequest(interaction.Request));
            }

            var evidence = interaction.PendingTerminal!;
            Emit(
                EventKind.TerminalCommitted, EventCausation.OfRequest(interaction.Request),
                request: interaction.Request, order: interaction.Order,
                detailCode: evidence.Outcome.ToString());
            interaction.State = InteractionState.Terminal;
            if (ReferenceEquals(active, interaction))
            {
                active = null;
            }

            if (evidence.Continuations.Count > 0 &&
                evidence.Outcome != InteractionOutcome.Rejected)
            {
                AdmitContinuations(interaction, evidence.Continuations);
            }
        }

        private void AdmitContinuations(Interaction parent, ValueArray<ContinuationRequest> continuations)
        {
            if (continuations.Count > options.MaxContinuationsPerParent)
            {
                // Adapter protocol violation: traced, never partially honored.
                Emit(
                    EventKind.TerminalCommitted, EventCausation.OfRequest(parent.Request),
                    request: parent.Request, detailCode: "ContinuationLimitExceeded");
                return;
            }

            for (var ordinal = 0; ordinal < continuations.Count; ordinal++)
            {
                var continuation = continuations[ordinal];
                // Kernel-namespaced by the parent's LogicalOrder (unique and short
                // within the incarnation), never derived from the caller-chosen id:
                // a caller cannot exhaust the identifier length bound, and a dedup
                // hit on this id is a traced conflict, not a silent swallow.
                var childRequest = new RequestId(
                    "continuation-" + parent.Order.Value.ToString(CultureInfo.InvariantCulture) +
                    "-" + ordinal.ToString(CultureInfo.InvariantCulture));

                SemanticFingerprint childFingerprint;
                try
                {
                    if (!options.TryResolveDomain(parent.Envelope.Principal, out var domain) ||
                        !TryResolveVisibleTarget(continuation.Target, domain, out var childRecord) ||
                        !capabilityContracts.TryGetValue(continuation.Capability, out var childDescriptor))
                    {
                        continue; // child admission fails like any admission; traced below
                    }

                    childFingerprint = InvocationCanonicalizer.Canonicalize(
                        continuation.Capability,
                        new ResolvedTarget(childRecord.Reference, childRecord.Registration.AuthorKey),
                        continuation.Payload,
                        childDescriptor.Arguments,
                        options.RedactionKey).Fingerprint;
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var envelope = new IdentityEnvelope(
                    parent.Envelope.Principal,
                    parent.Envelope.Ingress,
                    Provenance.Automation,
                    Causality.OfContinuation(new ContinuationLink(
                        parent.Request, ordinal, childFingerprint)));
                var submission = new IntentSubmission(
                    childRequest,
                    continuation.Capability,
                    continuation.Target,
                    continuation.Payload,
                    envelope,
                    observer: null);
                ProcessSubmission(new SubmissionMessage(submission, humanPriority: false));
            }
        }

        // ── Control processing ───────────────────────────────────────────────

        private void ProcessControl(ControlMessage message)
        {
            if (tornDown)
            {
                HandleControlAfterTeardown(message);
                return;
            }

            switch (message)
            {
                case CancelMessage cancel:
                    ProcessCancel(cancel.Request);
                    break;
                case FenceMessage fence:
                    ProcessFence(fence.Permit);
                    break;
                case CompletionMessage completion:
                    ProcessCompletion(completion.Completion);
                    break;
                case ObservedExternalMessage observed:
                    ProcessObservedExternal(observed.Report);
                    break;
                case RegistrationMessage registration:
                    ProcessRegistration(registration);
                    break;
                case ArmWaitMessage arm:
                    ProcessArmWait(arm);
                    break;
                case CancelWaitMessage cancelWait:
                    ResolveWait(cancelWait.Operation, PredicateResolution.Cancelled);
                    break;
                case AssertionMessage assertion:
                    ProcessAssertions(assertion.Batch);
                    break;
                case GateMessage gate:
                    gated = gate.Acquire;
                    gateHolder = gate.Acquire ? gate.Holder : default;
                    break;
                case SnapshotRequestMessage snapshotRequest:
                    ProcessSnapshotRequest(snapshotRequest);
                    break;
                case ReleaseSnapshotMessage release:
                    ReleasePinnedSnapshot(release.Operation);
                    break;
                case TeardownMessage:
                    ProcessTeardown();
                    break;
            }
        }

        private void ProcessSnapshotRequest(SnapshotRequestMessage message)
        {
            if (tornDown)
            {
                message.Observer.OnRefused(message.Operation, "TornDown");
                return;
            }

            // Unbound principal, unregistered view, and family/domain mismatch are
            // observationally identical (existence concealment, guarantees.md §3.5).
            if (!options.TryResolveDomain(message.Principal, out var domain) ||
                !viewContracts.TryGetValue(message.View, out var descriptor) ||
                (descriptor.Family == ViewFamily.Record) != domain.Equals(options.RecordDomain))
            {
                message.Observer.OnRefused(message.Operation, "ViewUnavailable");
                return;
            }

            // A narrower request must stay inside the registered contract's scope:
            // the requested scope is either the contract's own, or a registered,
            // domain-visible descendant of it. Unknown, invisible, and
            // out-of-contract scopes all answer the same code — no descendant or
            // existence oracle.
            if (!IsRequestableScope(message.Scope, descriptor, domain))
            {
                message.Observer.OnRefused(message.Operation, "ViewUnavailable");
                return;
            }

            // Deferred and active pins count together toward the bound.
            if (pinnedSnapshots.Count + deferredSnapshots.Count >= options.MaxPinnedSnapshots)
            {
                message.Observer.OnRefused(message.Operation, "CapacityExhausted");
                return;
            }

            var effective = string.Equals(message.Scope, descriptor.Scope, StringComparison.Ordinal)
                ? descriptor
                : new ViewContractDescriptor(
                    descriptor.Contract, descriptor.Family, message.Scope,
                    descriptor.MaxNodes, descriptor.MaxFieldBytes, descriptor.IncludeKeylessNodes);
            var budget = new ObservationBudget(
                Math.Min(pumpObservationBytesRemaining, options.MaxMaterializationBytes),
                Math.Min(pumpObservationNodesRemaining, options.MaxMaterializationNodes));
            var partialBudget =
                pumpObservationBytesRemaining < options.ObservationBudgetBytes ||
                pumpObservationNodesRemaining < options.ObservationBudgetNodes;
            var result = ObservationProjector.Materialize(
                nodeStore, sourceTable, effective, domain, currentLogicalNow, budget,
                options.MaxObservationFieldBytes, options.MaxCompletenessEntries,
                projectionScratch);
            if (result.Truncated && partialBudget)
            {
                // Mid-pump budget pressure: restart against a fresh pump budget and
                // a fresh revision rather than deliver an avoidably truncated
                // snapshot (observation-state.md §4, ADR 0011 restart policy).
                deferredSnapshots.Enqueue(message);
                return;
            }

            pumpObservationBytesRemaining =
                Math.Max(0, pumpObservationBytesRemaining - result.ApproximateBytes);
            pumpObservationNodesRemaining =
                Math.Max(0, pumpObservationNodesRemaining - result.Materialization.Nodes.Count);

            // With a codec, ordinary pinned snapshots are addressed and retained
            // under the operation's lease; without one — or when the cache cannot
            // hold the blob — the snapshot is honestly unaddressed.
            var contentId = default(ContentId);
            if (options.CanonicalStateCodec != null)
            {
                try
                {
                    var canonical = options.CanonicalStateCodec.Encode(result.Materialization);
                    if (stateStore.TryPut(
                            domain, canonical.Id, result.Materialization, canonical.Length) ==
                        PutAnswer.Retained)
                    {
                        stateStore.TryPin(domain, canonical.Id, LeaseOwner.Of(message.Operation));
                        contentId = canonical.Id;
                    }
                }
                catch (Exception)
                {
                    // A throwing codec degrades addressing alone, never the read.
                    Emit(EventKind.StateTransition, EventCausation.None, detailCode: "SnapshotUnaddressed");
                }
            }

            var pinned = new PinnedSnapshot(
                new ObservationSnapshot(
                    result.Materialization.Basis, contentId, result.Materialization.Completeness),
                result.Materialization);
            pinnedSnapshots[message.Operation] = pinned;
            message.Observer.OnPinned(message.Operation, pinned);
        }

        private void ReleasePinnedSnapshot(OperationId operation)
        {
            if (!pinnedSnapshots.TryGetValue(operation, out var pinned))
            {
                return;
            }

            pinnedSnapshots.Remove(operation);
            if (pinned.Snapshot.IsAddressed)
            {
                stateStore.Release(
                    pinned.Snapshot.Basis.Domain, pinned.Snapshot.ContentId, LeaseOwner.Of(operation));
            }
        }

        // ── Record materialization, the checkpoint feed, and the after-basis ──

        /// <summary>Materializes and canonically encodes one Record-family view at the current revision.</summary>
        private RecordMaterialization MaterializeRecord(
            ViewContractDescriptor descriptor, ObservationBudget budget, out ProjectionResult projection)
        {
            projection = ObservationProjector.Materialize(
                nodeStore, sourceTable, descriptor, options.RecordDomain, currentLogicalNow,
                budget, options.MaxObservationFieldBytes, options.MaxCompletenessEntries,
                projectionScratch);
            var canonical = options.CanonicalStateCodec!.Encode(projection.Materialization);
            return new RecordMaterialization(
                new ObservationSnapshot(
                    projection.Materialization.Basis, canonical.Id, projection.Materialization.Completeness),
                projection.Materialization,
                canonical);
        }

        private ObservationBudget MaterializationCeiling() =>
            new ObservationBudget(options.MaxMaterializationBytes, options.MaxMaterializationNodes);

        private ViewContractDescriptor KernelRecordDescriptor() => new ViewContractDescriptor(
            kernelView,
            ViewFamily.Record,
            ViewContractDescriptor.RootScope,
            options.MaxMaterializationNodes,
            options.MaxObservationFieldBytes,
            includeKeylessNodes: false);

        private void CaptureAfterBasis(Interaction interaction)
        {
            if (options.CanonicalStateCodec == null || afterBases.ContainsKey(interaction.Request))
            {
                return;
            }

            RecordMaterialization materialization;
            try
            {
                materialization = MaterializeRecord(
                    KernelRecordDescriptor(), MaterializationCeiling(), out _);
            }
            catch (Exception)
            {
                // A throwing codec is a recording-side failure: the true terminal
                // is already committed and stands; the E4 obligation will fail on
                // its own path (guarantees.md §7) — never the interaction.
                Emit(
                    EventKind.StateTransition, EventCausation.OfRequest(interaction.Request),
                    request: interaction.Request, detailCode: "AfterBasisUnavailable");
                return;
            }

            afterBases[interaction.Request] = materialization;

            // Best-effort cache retention: the reference above is the authority;
            // a refused put only means the blob is not shareable by address yet.
            if (stateStore.TryPut(
                    options.RecordDomain,
                    materialization.Canonical.Id,
                    materialization.Materialization,
                    materialization.Canonical.Length) == PutAnswer.Retained)
            {
                stateStore.TryPin(
                    options.RecordDomain,
                    materialization.Canonical.Id,
                    LeaseOwner.OfRequest(interaction.Request));
            }
        }

        /// <summary>The best-known cause of the latest revision advance — checkpoint metadata, never authority.</summary>
        private void NoteRevisionCause(LogicalOrder? cause)
        {
            lastCauseRevision = nodeStore.Revision.Value;
            lastCause = cause;
        }

        /// <summary>The active interaction's order when its effect window owns the mutation, else nothing.</summary>
        private LogicalOrder? EffectWindowOrder() =>
            active != null && active.EffectStarted ? active.Order : (LogicalOrder?)null;

        /// <summary>
        /// The pump-boundary heartbeat (observation-state.md §4/§8, ADR 0011): a
        /// revision advance without a retained checkpoint is a gap, and the next
        /// successful checkpoint carries the gap mark. Transient shortfalls (pump
        /// budget) retry at the next pump's fresh budget; conditions permanent at
        /// this revision (an unstorable or unretainable blob) skip it with the gap
        /// recorded. Runs after the turn loop, never past the deadline.
        /// </summary>
        private void RunCheckpointFeed(PumpBudget budget)
        {
            if (options.CanonicalStateCodec == null || DeadlinePassed(budget))
            {
                return;
            }

            var current = nodeStore.Revision;
            if (current.Value == timelineObservedRevision)
            {
                return;
            }

            if (pumpObservationBytesRemaining <= 0 || pumpObservationNodesRemaining <= 0)
            {
                // Transient: retry against the next pump's fresh budget.
                timelineGapPending = true;
                return;
            }

            RecordMaterialization materialization;
            ProjectionResult projection;
            try
            {
                materialization = MaterializeRecord(
                    KernelRecordDescriptor(),
                    new ObservationBudget(
                        Math.Min(pumpObservationBytesRemaining, options.MaxMaterializationBytes),
                        Math.Min(pumpObservationNodesRemaining, options.MaxMaterializationNodes)),
                    out projection);
            }
            catch (Exception)
            {
                // A throwing codec fails the diagnostic lane alone: the gap is
                // recorded and the revision is skipped (permanent at this revision).
                Emit(EventKind.StateTransition, EventCausation.None, detailCode: "CheckpointUnavailable");
                timelineGapPending = true;
                timelineObservedRevision = current.Value;
                return;
            }

            pumpObservationBytesRemaining =
                Math.Max(0, pumpObservationBytesRemaining - projection.ApproximateBytes);
            pumpObservationNodesRemaining =
                Math.Max(0, pumpObservationNodesRemaining - projection.Materialization.Nodes.Count);
            if (projection.Truncated)
            {
                // The remaining pump budget cut the materialization short: transient.
                timelineGapPending = true;
                return;
            }

            // The timeline is diagnostic: a refused put records a gap and never
            // triggers the evidence-priority eviction (observation-state.md §5.1).
            if (stateStore.TryPut(
                    options.RecordDomain,
                    materialization.Canonical.Id,
                    materialization.Materialization,
                    materialization.Canonical.Length) != PutAnswer.Retained)
            {
                timelineGapPending = true;
                timelineObservedRevision = current.Value;
                return;
            }

            stateStore.TryPin(options.RecordDomain, materialization.Canonical.Id, LeaseOwner.Timeline);

            // Causation metadata (observation-state.md §8): the checkpoint cites
            // the recorded cause of the latest advance to this exact revision —
            // source-only and external advances cite nothing (ADR 0009).
            var causingOrder = lastCauseRevision == current.Value ? lastCause : null;
            if (!timeline.Append(
                    new TimelineEntry(
                        current,
                        materialization.Canonical.Id,
                        causingOrder,
                        timelineGapPending,
                        currentLogicalNow,
                        timelineSequence++),
                    materialization.Canonical.Length))
            {
                // The entry could not be retained (it exceeds the retention bound
                // by itself): permanent at this revision, and the gap stands.
                timelineGapPending = true;
                timelineObservedRevision = current.Value;
                return;
            }

            timelineGapPending = false;
            timelineObservedRevision = current.Value;
        }

        private bool IsRequestableScope(
            string requestedScope, ViewContractDescriptor descriptor, SecurityDomainId domain)
        {
            if (string.Equals(requestedScope, descriptor.Scope, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(requestedScope, ViewContractDescriptor.RootScope, StringComparison.Ordinal))
            {
                // 'root' widens any non-root contract scope; never permitted.
                return false;
            }

            return nodeStore.TryResolveByKey(new AuthorKey(requestedScope), out var record) &&
                record.Registration.Exposure.IsVisibleTo(domain) &&
                ObservationProjector.IsInScope(nodeStore, record, descriptor.Scope);
        }

        /// <summary>The incarnation is over: every late control operation gets an explicit answer or trace, never silent state changes.</summary>
        private void HandleControlAfterTeardown(ControlMessage message)
        {
            switch (message)
            {
                case RegistrationMessage registration:
                    registration.Observer?.OnCompleted(RegistrationReceipt.Failure("TornDown"));
                    break;
                case ArmWaitMessage arm:
                    arm.Observer.OnResolved(arm.Operation, PredicateResolution.Faulted);
                    break;
                case AssertionMessage assertion:
                {
                    var results = new List<PredicateEvaluationResult>();
                    foreach (var _ in assertion.Batch.Predicates)
                    {
                        results.Add(new PredicateEvaluationResult(
                            PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Incompleteness),
                            ValueArray<ClauseEvaluation>.Empty));
                    }

                    assertion.Batch.Observer.OnEvaluated(
                        ValueArray<PredicateEvaluationResult>.From(results));
                    break;
                }

                case FenceMessage fence:
                    EmitProtocolViolation(fence.Permit, "FenceRejected");
                    break;
                case CompletionMessage completion:
                    EmitProtocolViolation(completion.Completion.Permit, "CompletionRejected");
                    break;
                case SnapshotRequestMessage snapshotRequest:
                    snapshotRequest.Observer.OnRefused(snapshotRequest.Operation, "TornDown");
                    break;
            }
        }

        private void ProcessCancel(RequestId request)
        {
            if (active != null && active.Request.Equals(request))
            {
                active.CancellationRequested = true;
                if (active.State == InteractionState.Validating ||
                    (active.State == InteractionState.Invoking && !active.EffectPermitted))
                {
                    Terminate(active, TerminalDetails.Cancelled(CancellationPhase.BeforeEffect));
                }

                return;
            }

            foreach (var queued in admitted)
            {
                if (queued.Request.Equals(request))
                {
                    // Cancellation of a queued interaction is always BeforeEffect;
                    // it terminates when it reaches Validating.
                    queued.CancellationRequested = true;
                    return;
                }
            }
        }

        private void ProcessFence(EffectPermitToken permit)
        {
            if (!IsLivePermit(permit) || active!.Fenced)
            {
                EmitProtocolViolation(permit, "FenceRejected");
                return;
            }

            active.Fenced = true;
            Emit(
                EventKind.EffectFenceReached, EventCausation.OfRequest(active.Request),
                request: active.Request, order: active.Order);
        }

        private void ProcessCompletion(EffectCompletion completion)
        {
            if (!IsLivePermit(completion.Permit) || active!.Completion != null)
            {
                EmitProtocolViolation(completion.Permit, "CompletionRejected");
                return;
            }

            // A successful completion must carry the evidence of the profile bound
            // at admission; for the standard profiles the evidence kind is the
            // profile's own (adapter-conformance.md §3-§4). Anything else is a
            // protocol violation, never a state change.
            if (completion.Resolution.Kind == EffectResolutionKind.Succeeded)
            {
                var evidence = completion.Resolution.CompletionEvidence;
                if (!evidence.Profile.Equals(active.Descriptor.CompletionProfile) ||
                    (IsStandardProfile(evidence.Profile) &&
                        evidence.Kind.Value != evidence.Profile.Id.Value))
                {
                    EmitProtocolViolation(completion.Permit, "CompletionRejected");
                    return;
                }
            }

            active.Completion = completion;
            if (FenceEntailedBy(active.Descriptor.CompletionProfile))
            {
                active.Fenced = true;
            }
        }

        /// <summary>
        /// Completion implies an unreported fence only for profiles whose evidence
        /// entails "no further mutation after completion"; AdapterAcknowledged@1
        /// never does (adapter-conformance.md §3/§4, ADR 0010).
        /// </summary>
        private static bool FenceEntailedBy(CompletionProfileRef profile)
        {
            var id = profile.Id.Value;
            return id == "Applied" || id == "FrameCommitted" || id == "PostconditionSatisfied";
        }

        private static bool IsStandardProfile(CompletionProfileRef profile)
        {
            var id = profile.Id.Value;
            return id == "Applied" || id == "FrameCommitted" ||
                id == "PostconditionSatisfied" || id == "AdapterAcknowledged";
        }

        private bool IsLivePermit(EffectPermitToken permit)
        {
            return !tornDown &&
                active != null &&
                active.Permit.HasValue &&
                active.Permit.Value.Equals(permit) &&
                permit.Incarnation.Equals(Incarnation);
        }

        private void EmitProtocolViolation(EffectPermitToken permit, string detail)
        {
            Emit(
                EventKind.StateTransition,
                permit.IsDefault ? EventCausation.None : EventCausation.OfRequest(permit.Request),
                detailCode: detail);
        }

        private void ProcessObservedExternal(ObservedExternalReport report)
        {
            var intersecting = active != null && active.EffectStarted &&
                active.State != InteractionState.Terminal;
            if (intersecting)
            {
                active!.Contaminated = true;
            }

            nodeStore.AdvanceRevision(); // an external effect is an observable mutation
            NoteRevisionCause(null); // external: no admission order exists to cite (ADR 0009)
            Emit(
                intersecting ? EventKind.ContaminationObserved : new EventKind("ObservedExternal"),
                EventCausation.OfExternal(report.SourceHint),
                request: intersecting ? active!.Request : (RequestId?)null,
                revision: nodeStore.Revision);
        }

        private void ProcessRegistration(RegistrationMessage message)
        {
            var revisionBefore = nodeStore.Revision.Value;
            RegistrationReceipt receipt;
            switch (message.Kind)
            {
                case RegistrationMessage.Operation.Register:
                    try
                    {
                        var reference = nodeStore.Register(message.Registration!);
                        receipt = RegistrationReceipt.Success(reference);
                    }
                    catch (ArgumentException)
                    {
                        receipt = RegistrationReceipt.Failure("DuplicateAuthorKey");
                    }

                    break;
                case RegistrationMessage.Operation.Unregister:
                    receipt = nodeStore.TryUnregister(message.Node)
                        ? RegistrationReceipt.Success(null)
                        : RegistrationReceipt.Failure("UnknownNode");
                    break;
                case RegistrationMessage.Operation.UpdateAttributes:
                    receipt = nodeStore.TryUpdateAttributes(message.Node, message.Updates!.Value)
                        ? RegistrationReceipt.Success(null)
                        : RegistrationReceipt.Failure("UpdateRefused");
                    break;
                default:
                    receipt = nodeStore.TrySetAvailability(
                        message.Node, message.Capability, message.Available)
                        ? RegistrationReceipt.Success(null)
                        : RegistrationReceipt.Failure("UnknownCapability");
                    break;
            }

            if (nodeStore.Revision.Value != revisionBefore)
            {
                // A registry mutation inside an effect window is that interaction's
                // own mutation; outside one, no admission order exists to cite.
                NoteRevisionCause(EffectWindowOrder());
            }

            message.Observer?.OnCompleted(receipt);
        }

        private void ProcessArmWait(ArmWaitMessage message)
        {
            if (waits.Count >= options.MaxArmedWaits ||
                !predicateContracts.TryGetValue(message.Predicate, out var definition) ||
                !options.TryResolveDomain(message.Principal, out var domain))
            {
                message.Observer.OnResolved(message.Operation, PredicateResolution.Faulted);
                return;
            }

            Emit(EventKind.PredicateArmed, EventCausation.None, operation: message.Operation);
            var entry = new WaitEntry(message.Operation, definition, domain, message.TimeoutAtLogicalTime, message.Observer);
            var result = PredicateEvaluator.Evaluate(
                definition, PinReader(domain), PredicateStructuralBounds.Default);
            if (result.Outcome.Kind == PredicateEvaluationKind.Satisfied)
            {
                Emit(EventKind.PredicateResolved, EventCausation.None, operation: message.Operation);
                message.Observer.OnResolved(message.Operation, PredicateResolution.Satisfied);
                return;
            }

            // A wait armed with an already-passed deadline resolves immediately;
            // storing it would leave the observer unresolved on an idle kernel.
            if (message.TimeoutAtLogicalTime <= currentLogicalNow)
            {
                Emit(EventKind.PredicateResolved, EventCausation.None, operation: message.Operation);
                message.Observer.OnResolved(message.Operation, PredicateResolution.TimedOut);
                return;
            }

            waits.Add(message.Operation, entry);
            waitDeadlines.Add(message.Operation, message.TimeoutAtLogicalTime);
        }

        private void ResolveWait(OperationId operation, PredicateResolution resolution)
        {
            if (waits.TryGetValue(operation, out var entry))
            {
                waits.Remove(operation);
                waitDeadlines.Remove(operation);
                Emit(EventKind.PredicateResolved, EventCausation.None, operation: operation);
                entry.Observer.OnResolved(operation, resolution);
            }
        }

        private void ResolveTimedOutWaits()
        {
            // Pop-and-resolve is iteration-safe (no dictionary enumeration to
            // invalidate) and deterministic: deadline order, arm order on ties.
            while (waitDeadlines.TryPopExpired(currentLogicalNow, out var operation))
            {
                ResolveWait(operation, PredicateResolution.TimedOut);
            }
        }

        private void ReevaluateWaitsIfRevisionAdvanced()
        {
            var revision = nodeStore.Revision.Value;
            if (revision == lastWaitEvaluationRevision || waits.Count == 0)
            {
                lastWaitEvaluationRevision = revision;
                return;
            }

            lastWaitEvaluationRevision = revision;

            // One evaluation read per domain, shared across every wait of that
            // domain (performance-track finding A3): without sampled sources a
            // materialization is a pure function of the revision, so the reads
            // are interchangeable — the batch-assertion path always worked this
            // way. Domains exposing sampled sources keep a fresh read per wait
            // (observation-state.md §7: sampled sources read at materialization
            // time), exactly the historical behavior.
            Dictionary<SecurityDomainId, MaterializationLookup>? sharedReaders = null;
            List<OperationId>? satisfied = null;
            foreach (var pair in waits)
            {
                var result = PredicateEvaluator.Evaluate(
                    pair.Value.Definition,
                    WaitEvaluationReader(pair.Value.Domain, ref sharedReaders),
                    PredicateStructuralBounds.Default);
                if (result.Outcome.Kind == PredicateEvaluationKind.Satisfied)
                {
                    (satisfied ??= new List<OperationId>()).Add(pair.Key);
                }
            }

            if (satisfied != null)
            {
                foreach (var operation in satisfied)
                {
                    ResolveWait(operation, PredicateResolution.Satisfied);
                }
            }
        }

        /// <summary>
        /// The evaluation read for one armed wait: shared per domain when the
        /// materialization is revision-pure, fresh per wait when a sampled source
        /// is exposed to the domain's family.
        /// </summary>
        private MaterializationLookup WaitEvaluationReader(
            SecurityDomainId domain,
            ref Dictionary<SecurityDomainId, MaterializationLookup>? sharedReaders)
        {
            var sampledExposed = domain.Equals(options.RecordDomain)
                ? sampledVisibleToRecord
                : sampledVisibleToAgent;
            if (sampledExposed)
            {
                return PinReader(domain);
            }

            sharedReaders ??= new Dictionary<SecurityDomainId, MaterializationLookup>();
            if (!sharedReaders.TryGetValue(domain, out var reader))
            {
                reader = PinReader(domain);
                sharedReaders.Add(domain, reader);
            }

            return reader;
        }

        private void ProcessAssertions(AssertionBatch batch)
        {
            var results = new List<PredicateEvaluationResult>();
            if (!options.TryResolveDomain(batch.Principal, out var domain))
            {
                foreach (var _ in batch.Predicates)
                {
                    results.Add(new PredicateEvaluationResult(
                        PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.OutOfScope),
                        ValueArray<ClauseEvaluation>.Empty));
                }
            }
            else
            {
                // One pinned read for the whole batch (verification.md §3.2).
                var reader = PinReader(domain);
                foreach (var predicate in batch.Predicates)
                {
                    if (!predicateContracts.TryGetValue(predicate, out var definition))
                    {
                        results.Add(new PredicateEvaluationResult(
                            PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.UnsupportedContract),
                            ValueArray<ClauseEvaluation>.Empty));
                        continue;
                    }

                    results.Add(PredicateEvaluator.Evaluate(
                        definition, reader, PredicateStructuralBounds.Default));
                    Emit(EventKind.AssertionEvaluated, EventCausation.None);
                }
            }

            batch.Observer.OnEvaluated(ValueArray<PredicateEvaluationResult>.From(results));
        }

        private void ProcessTeardown()
        {
            if (tornDown)
            {
                return;
            }

            tornDown = true;
            active = null;
            admitted.Clear();
            committingEvidence.Clear();
            stalledAdmission = null;
            executor?.Detach();

            foreach (var stranded in recoveryIndex.DrainPending())
            {
                Emit(
                    EventKind.IncarnationLifecycle, EventCausation.None,
                    request: stranded, detailCode: "Stranded");
            }

            var operations = new List<OperationId>(waits.Keys);
            foreach (var operation in operations)
            {
                ResolveWait(operation, PredicateResolution.Cancelled);
            }

            // Every deferred snapshot observer is answered exactly once; active
            // pins, retained after-bases, the timeline, and the cache are released
            // with the incarnation.
            foreach (var deferred in deferredSnapshots)
            {
                deferred.Observer.OnRefused(deferred.Operation, "TornDown");
            }

            deferredSnapshots.Clear();
            pinnedSnapshots.Clear();
            afterBases.Clear();
            timeline.Clear();
            stateStore.Clear();

            PublishStatus();
            Emit(EventKind.IncarnationLifecycle, EventCausation.None, detailCode: "TornDown");
        }

        private void AdoptPublication(SourcePublicationMessage message)
        {
            var publication = message.Publication;
            if (!sourceTable.TryAdopt(
                publication.Source, publication.Document, message.ApproximateBytes, nodeStore))
            {
                Emit(
                    new EventKind("PublicationRejected"),
                    publication.Causation,
                    detailCode: "ContractViolationOrUnknownSource");
                return;
            }

            // A source-only advance carries no admission order to cite (ADR 0009).
            NoteRevisionCause(null);
            Emit(
                EventKind.SourcePublicationAdopted,
                publication.Causation,
                revision: nodeStore.Revision);

            // A publication caused outside the active controlled work that lands
            // during a recorded interaction's effect window participates in
            // contamination (observation-state.md §7.2). Only the active request's
            // own causation is exempt — another request's causation is still
            // external to THIS controlled work.
            if (active != null && active.EffectStarted &&
                !(publication.Causation.Kind == EventCausationKind.Request &&
                    publication.Causation.Request.Equals(active.Request)))
            {
                active.Contaminated = true;
                Emit(
                    EventKind.ContaminationObserved,
                    publication.Causation,
                    request: active.Request,
                    revision: nodeStore.Revision);
            }
        }

        // ── Observation ──────────────────────────────────────────────────────

        /// <summary>
        /// Pins the kernel's internal evaluation read: one materialization under the
        /// reserved kernel-raw view — Record family for the record domain, Agent
        /// family otherwise, preserving the per-domain source-exposure selection.
        /// Evaluation reads are bounded by the materialization ceilings, not the
        /// per-pump snapshot budget (kernel-execution.md §6); overflow surfaces as
        /// completeness and evaluates as Unevaluable, never a partial answer.
        /// </summary>
        private MaterializationLookup PinReader(SecurityDomainId domain)
        {
            // The two kernel-raw descriptors are parameterized by options alone —
            // built once, reused for every evaluation read.
            var record = domain.Equals(options.RecordDomain);
            var descriptor = record
                ? kernelRawRecordView ??= BuildKernelRawView(ViewFamily.Record)
                : kernelRawAgentView ??= BuildKernelRawView(ViewFamily.Agent);
            var result = ObservationProjector.Materialize(
                nodeStore, sourceTable, descriptor, domain, currentLogicalNow,
                new ObservationBudget(options.MaxMaterializationBytes, options.MaxMaterializationNodes),
                options.MaxObservationFieldBytes,
                options.MaxCompletenessEntries,
                projectionScratch);
            return new MaterializationLookup(result.Materialization);
        }

        private ViewContractDescriptor BuildKernelRawView(ViewFamily family) =>
            new ViewContractDescriptor(
                kernelView,
                family,
                ViewContractDescriptor.RootScope,
                options.MaxMaterializationNodes,
                options.MaxObservationFieldBytes,
                includeKeylessNodes: false);

        private void PublishStatus()
        {
            var entries = new List<KeyValuePair<RequestId, StatusBoard.Entry>>();
            foreach (var pair in PendingEntries())
            {
                entries.Add(pair);
            }

            foreach (var pair in TerminalEntries())
            {
                entries.Add(pair);
            }

            statusBoard.Publish(entries);
        }

        private IEnumerable<KeyValuePair<RequestId, StatusBoard.Entry>> PendingEntries()
        {
            foreach (var pair in recoveryIndex.Pendings)
            {
                yield return new KeyValuePair<RequestId, StatusBoard.Entry>(
                    pair.Key, new StatusBoard.Entry(pair.Value.Principal, QueryAnswer.Pending));
            }
        }

        private IEnumerable<KeyValuePair<RequestId, StatusBoard.Entry>> TerminalEntries()
        {
            foreach (var pair in recoveryIndex.Terminals)
            {
                yield return new KeyValuePair<RequestId, StatusBoard.Entry>(
                    pair.Key,
                    new StatusBoard.Entry(pair.Value.Principal, QueryAnswer.Terminal(pair.Value.Outcome)));
            }
        }

        private void Emit(
            EventKind kind,
            EventCausation causation,
            RequestId? request = null,
            OperationId? operation = null,
            LogicalOrder? order = null,
            SourceRevision? revision = null,
            string? detailCode = null)
        {
            // Field-wise emission: no SemanticEvent allocation per trace event
            // (the ring materializes public events only when snapshotted).
            trace.Emit(kind, Incarnation, causation, request, operation, order, revision, detailCode);
        }

        // ── Interaction record ───────────────────────────────────────────────

        internal enum InteractionState
        {
            Validating,
            Invoking,
            WaitingCompletion,
            Observing,
            CommittingEvidence,
            Terminal,
        }

        private sealed class Interaction
        {
            internal Interaction(
                RequestId request,
                LogicalOrder order,
                SemanticFingerprint fingerprint,
                CapabilityInvocation invocation,
                InvocationPayload payload,
                IdentityEnvelope envelope,
                SecurityDomainId domain,
                NodeRef target,
                CapabilityContractDescriptor descriptor)
            {
                Request = request;
                Order = order;
                Fingerprint = fingerprint;
                Invocation = invocation;
                Payload = payload;
                Envelope = envelope;
                Domain = domain;
                Target = target;
                Descriptor = descriptor;
                State = InteractionState.Validating;
            }

            internal RequestId Request { get; }

            internal LogicalOrder Order { get; }

            internal SemanticFingerprint Fingerprint { get; }

            internal CapabilityInvocation Invocation { get; }

            internal InvocationPayload? Payload { get; set; }

            internal IdentityEnvelope Envelope { get; }

            internal SecurityDomainId Domain { get; }

            internal NodeRef Target { get; }

            internal CapabilityContractDescriptor Descriptor { get; }

            internal InteractionState State { get; set; }

            internal EffectPermitToken? Permit { get; set; }

            internal bool EffectPermitted { get; set; }

            internal bool EffectStarted { get; set; }

            internal bool Fenced { get; set; }

            internal EffectCompletion? Completion { get; set; }

            internal bool CancellationRequested { get; set; }

            internal bool CancellationDelivered { get; set; }

            internal bool Contaminated { get; set; }

            internal TerminalEvidence? PendingTerminal { get; set; }
        }

        private sealed class WaitEntry
        {
            internal WaitEntry(
                OperationId operation,
                PredicateDefinition definition,
                SecurityDomainId domain,
                long timeoutAtLogicalTime,
                IWaitObserver observer)
            {
                Operation = operation;
                Definition = definition;
                Domain = domain;
                TimeoutAtLogicalTime = timeoutAtLogicalTime;
                Observer = observer;
            }

            internal OperationId Operation { get; }

            internal PredicateDefinition Definition { get; }

            internal SecurityDomainId Domain { get; }

            internal long TimeoutAtLogicalTime { get; }

            internal IWaitObserver Observer { get; }
        }

        private sealed class TerminalDetails
        {
            private TerminalDetails(InteractionOutcome outcome)
            {
                Outcome = outcome;
            }

            internal InteractionOutcome Outcome { get; }

            internal RejectionReason? RejectionReason { get; private set; }

            internal FaultCode? FaultCode { get; private set; }

            internal CancellationPhase? CancellationPhase { get; private set; }

            internal PostconditionResult? Postcondition { get; private set; }

            internal static TerminalDetails Succeeded(PostconditionResult? postcondition) =>
                new TerminalDetails(InteractionOutcome.Succeeded) { Postcondition = postcondition };

            internal static TerminalDetails Rejected(RejectionReason reason) =>
                new TerminalDetails(InteractionOutcome.Rejected) { RejectionReason = reason };

            internal static TerminalDetails Faulted(
                FaultCode code,
                bool effectPermitted,
                bool effectStarted,
                PostconditionResult? postcondition = null) =>
                new TerminalDetails(InteractionOutcome.Faulted)
                {
                    FaultCode = code,
                    Postcondition = postcondition,
                };

            internal static TerminalDetails Cancelled(
                CancellationPhase phase, string? disposition = null) =>
                new TerminalDetails(InteractionOutcome.Cancelled) { CancellationPhase = phase };
        }

        // ── Facades ──────────────────────────────────────────────────────────

        private sealed class BootstrapRegistry : IBootstrapRegistry
        {
            private readonly KernelRuntime runtime;

            internal BootstrapRegistry(KernelRuntime runtime)
            {
                this.runtime = runtime;
            }

            public NodeRef RegisterNode(NodeRegistration registration)
            {
                RequireBootstrapPhase();
                return runtime.nodeStore.Register(registration);
            }

            public void RegisterCapabilityContract(CapabilityContractDescriptor descriptor)
            {
                RequireBootstrapPhase();
                if (descriptor == null)
                {
                    throw new ArgumentNullException(nameof(descriptor));
                }

                if (runtime.capabilityContracts.ContainsKey(descriptor.Contract))
                {
                    throw new ArgumentException(
                        "Duplicate capability contract.", nameof(descriptor));
                }

                runtime.capabilityContracts.Add(descriptor.Contract, descriptor);
            }

            public void RegisterStateSource(StateSourceRegistration registration)
            {
                RequireBootstrapPhase();
                runtime.sourceTable.Register(registration);
            }

            public void RegisterViewContract(ViewContractDescriptor descriptor)
            {
                RequireBootstrapPhase();
                if (descriptor == null)
                {
                    throw new ArgumentNullException(nameof(descriptor));
                }

                if (descriptor.Contract.Id.Value.StartsWith("kernel-raw", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The kernel-raw identifier family is reserved (observation-state.md 1).",
                        nameof(descriptor));
                }

                if (runtime.viewContracts.Count >= runtime.options.MaxRegisteredViewContracts)
                {
                    throw new ArgumentException(
                        "Registered view contracts are at capacity.", nameof(descriptor));
                }

                if (runtime.viewContracts.ContainsKey(descriptor.Contract))
                {
                    throw new ArgumentException("Duplicate view contract.", nameof(descriptor));
                }

                runtime.viewContracts.Add(descriptor.Contract, descriptor);
            }

            public void RegisterPredicateContract(PredicateContractRef contract, PredicateDefinition definition)
            {
                RequireBootstrapPhase();
                if (contract.IsDefault)
                {
                    throw new ArgumentException("A non-default contract is required.", nameof(contract));
                }

                if (definition == null)
                {
                    throw new ArgumentNullException(nameof(definition));
                }

                if (runtime.predicateContracts.Count >= runtime.options.MaxRegisteredPredicateContracts)
                {
                    throw new ArgumentException(
                        "Registered predicate contracts are at capacity.", nameof(contract));
                }

                if (runtime.predicateContracts.ContainsKey(contract))
                {
                    throw new ArgumentException("Duplicate predicate contract.", nameof(contract));
                }

                runtime.predicateContracts.Add(contract, definition);
            }

            private void RequireBootstrapPhase()
            {
                if (runtime.started)
                {
                    throw new KernelFaultException(
                        "Bootstrap registration is only valid before Start (ADR 0010).");
                }
            }
        }

        private sealed class NodeRegistryFacade : INodeRegistry
        {
            private readonly KernelRuntime runtime;

            internal NodeRegistryFacade(KernelRuntime runtime)
            {
                this.runtime = runtime;
            }

            public void Register(NodeRegistration registration, IRegistrationObserver? observer)
            {
                if (registration == null)
                {
                    throw new ArgumentNullException(nameof(registration));
                }

                runtime.mailbox.EnqueueControl(new RegistrationMessage(
                    RegistrationMessage.Operation.Register, registration,
                    default, null, default, false, observer));
            }

            public void Unregister(NodeRef node, IRegistrationObserver? observer)
            {
                runtime.mailbox.EnqueueControl(new RegistrationMessage(
                    RegistrationMessage.Operation.Unregister, null,
                    node, null, default, false, observer));
            }

            public void UpdateAttributes(
                NodeRef node, ValueArray<NodeAttribute> updates, IRegistrationObserver? observer)
            {
                if (updates == null)
                {
                    throw new ArgumentNullException(nameof(updates));
                }

                runtime.mailbox.EnqueueControl(new RegistrationMessage(
                    RegistrationMessage.Operation.UpdateAttributes, null,
                    node, updates, default, false, observer));
            }

            public void SetCapabilityAvailability(
                NodeRef node, CapabilityContractRef capability, bool available, IRegistrationObserver? observer)
            {
                runtime.mailbox.EnqueueControl(new RegistrationMessage(
                    RegistrationMessage.Operation.SetAvailability, null,
                    node, null, capability, available, observer));
            }
        }

        private sealed class IngressSink : IIngressSink
        {
            private readonly KernelRuntime runtime;

            internal IngressSink(KernelRuntime runtime)
            {
                this.runtime = runtime;
            }

            public void Submit(IntentSubmission submission)
            {
                if (submission == null)
                {
                    throw new ArgumentNullException(nameof(submission));
                }

                // Nested submission during the synchronous executor call is refused
                // on every thread (kernel-execution.md §5); follow-ups are
                // continuations.
                if (runtime.executingEffect)
                {
                    submission.Observer?.OnRejected(
                        submission.Request, RejectionReason.ReentrantDispatch);
                    return;
                }

                var humanPriority = submission.Envelope.Provenance == Provenance.HumanDirected;
                if (!runtime.mailbox.TryEnqueueSubmission(new SubmissionMessage(submission, humanPriority)))
                {
                    submission.Observer?.OnRejected(
                        submission.Request, RejectionReason.CapacityExhausted);
                }
            }

            public void ReportObservedExternal(ObservedExternalReport report)
            {
                if (report == null)
                {
                    throw new ArgumentNullException(nameof(report));
                }

                runtime.mailbox.EnqueueControl(new ObservedExternalMessage(report));
            }

            public PublicationAnswer PublishSourceDocument(SourcePublication publication)
            {
                if (publication == null)
                {
                    throw new ArgumentNullException(nameof(publication));
                }

                var bytes = EstimateBytes(publication.Document);
                return runtime.mailbox.EnqueuePublication(
                    new SourcePublicationMessage(publication, bytes));
            }

            private static int EstimateBytes(SourceDocument document)
            {
                var bytes = 64;
                foreach (var field in document.Fields)
                {
                    bytes += 32 + (field.Name.Length * 2) + (RenderedLength(field.Value) * 2);
                }

                return bytes;
            }

            /// <summary>
            /// The length <see cref="FieldValue.ToString"/> would render, without
            /// building the string — byte accounting stays exactly what it was.
            /// Floats keep the real rendering: "R" length is not computable
            /// arithmetically, and float-bearing publications are rare.
            /// </summary>
            private static int RenderedLength(FieldValue value)
            {
                switch (value.Kind)
                {
                    case FieldValueKind.String:
                        return value.AsString.Length;
                    case FieldValueKind.Integer:
                        return DecimalDigits(value.AsInteger);
                    case FieldValueKind.Boolean:
                        return value.AsBoolean ? 4 : 5;
                    case FieldValueKind.Float:
                        return value.ToString().Length;
                    default:
                        return 4; // "null"
                }
            }

            private static int DecimalDigits(long value)
            {
                if (value == long.MinValue)
                {
                    return 20; // |-9223372036854775808|
                }

                var digits = value < 0 ? 2 : 1;
                for (var magnitude = value < 0 ? -value : value; magnitude >= 10; magnitude /= 10)
                {
                    digits++;
                }

                return digits;
            }
        }

        private sealed class CompletionSink : IEffectCompletionSink
        {
            private readonly KernelRuntime runtime;

            internal CompletionSink(KernelRuntime runtime)
            {
                this.runtime = runtime;
            }

            public void ReportFenceReached(EffectPermitToken permit)
            {
                runtime.mailbox.EnqueuePostFence(new FenceMessage(permit));
            }

            public void ReportCompletion(EffectCompletion completion)
            {
                if (completion == null)
                {
                    throw new ArgumentNullException(nameof(completion));
                }

                runtime.mailbox.EnqueuePostFence(new CompletionMessage(completion));
            }
        }

        private sealed class ControlFacade : IKernelControl
        {
            private readonly KernelRuntime runtime;

            internal ControlFacade(KernelRuntime runtime)
            {
                this.runtime = runtime;
            }

            public void RequestCancel(RequestId request)
            {
                if (request.IsDefault)
                {
                    throw new ArgumentException("Cancel requires a non-default RequestId.", nameof(request));
                }

                runtime.mailbox.EnqueueControl(new CancelMessage(request));
            }

            public OperationId ArmWait(
                PredicateContractRef predicate,
                Principal principal,
                long timeoutAtLogicalTime,
                IWaitObserver observer)
            {
                if (predicate.IsDefault)
                {
                    throw new ArgumentException("A non-default predicate is required.", nameof(predicate));
                }

                if (principal == null)
                {
                    throw new ArgumentNullException(nameof(principal));
                }

                if (observer == null)
                {
                    throw new ArgumentNullException(nameof(observer));
                }

                var operation = new OperationId(
                    "wait-" + Interlocked.Increment(ref runtime.waitCounter)
                        .ToString(CultureInfo.InvariantCulture));
                runtime.mailbox.EnqueueControl(new ArmWaitMessage(
                    operation, predicate, principal, timeoutAtLogicalTime, observer));
                return operation;
            }

            public void CancelWait(OperationId operation)
            {
                if (operation.IsDefault)
                {
                    throw new ArgumentException("A non-default operation is required.", nameof(operation));
                }

                runtime.mailbox.EnqueueControl(new CancelWaitMessage(operation));
            }

            public void EvaluateAssertions(AssertionBatch batch)
            {
                if (batch == null)
                {
                    throw new ArgumentNullException(nameof(batch));
                }

                if (batch.Predicates.Count > PredicateStructuralBounds.Default.MaxBatchSize)
                {
                    throw new ArgumentException(
                        "The batch exceeds the configured batch-size bound.", nameof(batch));
                }

                runtime.mailbox.EnqueueControl(new AssertionMessage(batch));
            }

            public void AcquireExclusiveControl(SecurityDomainId holder)
            {
                if (holder.IsDefault)
                {
                    throw new ArgumentException("A non-default holder is required.", nameof(holder));
                }

                runtime.mailbox.EnqueueControl(new GateMessage(holder, acquire: true));
            }

            public void ReleaseExclusiveControl()
            {
                runtime.mailbox.EnqueueControl(new GateMessage(default, acquire: false));
            }

            public void TearDownIncarnation()
            {
                runtime.mailbox.EnqueueControl(new TeardownMessage());
            }

            public OperationId RequestSnapshot(
                ViewContractRef view, Principal principal, string scope, ISnapshotObserver observer)
            {
                if (view.IsDefault)
                {
                    throw new ArgumentException("A non-default view is required.", nameof(view));
                }

                if (principal == null)
                {
                    throw new ArgumentNullException(nameof(principal));
                }

                ContractGrammar.ValidateIdentifier(scope, nameof(scope));
                if (observer == null)
                {
                    throw new ArgumentNullException(nameof(observer));
                }

                var operation = new OperationId(
                    "snapshot-" + Interlocked.Increment(ref runtime.waitCounter)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture));
                runtime.mailbox.EnqueueControl(new SnapshotRequestMessage(
                    operation, view, principal, scope, observer));
                return operation;
            }

            public void ReleaseSnapshot(OperationId operation)
            {
                if (operation.IsDefault)
                {
                    throw new ArgumentException("A non-default operation is required.", nameof(operation));
                }

                runtime.mailbox.EnqueueControl(new ReleaseSnapshotMessage(operation));
            }
        }

        private sealed class RecordObservationFacade : IRecordObservationServices
        {
            private readonly KernelRuntime runtime;

            internal RecordObservationFacade(KernelRuntime runtime)
            {
                this.runtime = runtime;
            }

            public bool CanAddress =>
                runtime.options.CanonicalStateCodec != null && !runtime.tornDown;

            public bool TryMaterializeView(
                ViewContractRef view,
                string scope,
                SourceRevision? expectedBasis,
                out RecordMaterialization? materialization,
                out bool basisMismatch)
            {
                if (runtime.options.CanonicalStateCodec == null)
                {
                    throw new KernelFaultException(
                        "Record materialization requires the canonical-state codec (ADR 0011).");
                }

                if (!runtime.viewContracts.TryGetValue(view, out var descriptor) ||
                    descriptor.Family != ViewFamily.Record)
                {
                    throw new KernelFaultException(
                        "Record materialization requires a registered Record-family view.");
                }

                ContractGrammar.ValidateIdentifier(scope, nameof(scope));
                if (!runtime.IsRequestableScope(scope, descriptor, runtime.options.RecordDomain))
                {
                    throw new KernelFaultException(
                        "The requested scope is outside the registered view contract's scope.");
                }

                if (expectedBasis.HasValue && !expectedBasis.Value.Equals(runtime.nodeStore.Revision))
                {
                    // The basis moved: E3 re-materializes at the new revision; a
                    // silently different-revision materialization is prohibited.
                    materialization = null;
                    basisMismatch = true;
                    return false;
                }

                var effective = string.Equals(scope, descriptor.Scope, StringComparison.Ordinal)
                    ? descriptor
                    : new ViewContractDescriptor(
                        descriptor.Contract, descriptor.Family, scope,
                        descriptor.MaxNodes, descriptor.MaxFieldBytes, descriptor.IncludeKeylessNodes);
                materialization = runtime.MaterializeRecord(
                    effective, runtime.MaterializationCeiling(), out _);
                basisMismatch = false;
                return true;
            }

            public LeaseAnswer TryLease(RecordMaterialization materialization, OperationId recording)
            {
                if (materialization == null)
                {
                    throw new ArgumentNullException(nameof(materialization));
                }

                if (recording.IsDefault)
                {
                    throw new ArgumentException(
                        "A lease requires a non-default recording operation.", nameof(recording));
                }

                if (runtime.options.CanonicalStateCodec == null)
                {
                    return LeaseAnswer.Unaddressable;
                }

                while (true)
                {
                    var answer = runtime.stateStore.TryPut(
                        runtime.options.RecordDomain,
                        materialization.Canonical.Id,
                        materialization.Materialization,
                        materialization.Canonical.Length);
                    switch (answer)
                    {
                        case PutAnswer.Retained:
                            runtime.stateStore.TryPin(
                                runtime.options.RecordDomain,
                                materialization.Canonical.Id,
                                LeaseOwner.Of(recording));
                            return LeaseAnswer.Retained;
                        case PutAnswer.OverBlobBound:
                            return LeaseAnswer.OverBlobBound;
                        default:
                            // Diagnostic retention never fails evidence: release
                            // timeline pins oldest-first and retry before refusing.
                            if (!runtime.timeline.TryEvictOldest())
                            {
                                return LeaseAnswer.OverBudget;
                            }

                            break;
                    }
                }
            }

            public bool TryGetAfterMaterialization(
                RequestId request, out RecordMaterialization? materialization)
            {
                if (request.IsDefault)
                {
                    throw new ArgumentException("A non-default request is required.", nameof(request));
                }

                if (runtime.afterBases.TryGetValue(request, out var retained))
                {
                    materialization = retained;
                    return true;
                }

                materialization = null;
                return false;
            }

            public void ReleaseRecording(OperationId recording)
            {
                if (recording.IsDefault)
                {
                    throw new ArgumentException(
                        "A non-default recording operation is required.", nameof(recording));
                }

                runtime.stateStore.ReleaseOwner(LeaseOwner.Of(recording));
            }
        }
    }
}
