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

        private readonly Queue<Interaction> admitted = new Queue<Interaction>();
        private readonly List<Interaction> committingEvidence = new List<Interaction>();
        private readonly Dictionary<OperationId, WaitEntry> waits = new Dictionary<OperationId, WaitEntry>();

        private Interaction? active;
        private SubmissionMessage? stalledAdmission;
        private bool started;
        private bool tornDown;
        private int pumping;
        private long lastMonotonic = long.MinValue;
        private ulong logicalOrderCounter;
        private ulong nonceCounter;
        private long waitCounter;
        private ulong lastWaitEvaluationRevision;
        private long currentLogicalNow;
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
            Bootstrap = new BootstrapRegistry(this);
            Registry = new NodeRegistryFacade(this);
            Ingress = new IngressSink(this);
            Completions = new CompletionSink(this);
            Control = new ControlFacade(this);
        }

        public RuntimeIncarnationId Incarnation => nodeStore.Incarnation;

        public IBootstrapRegistry Bootstrap { get; }

        public INodeRegistry Registry { get; }

        public IIngressSink Ingress { get; }

        public IEffectCompletionSink Completions { get; }

        public IKernelQueries Queries => statusBoard;

        public IKernelControl Control { get; }

        public KernelTraceRing Trace => trace;

        /// <summary>Wires the adapter surfaces and freezes the bootstrap registry.</summary>
        public void Start(IEffectExecutor effectExecutor)
        {
            if (started)
            {
                throw new KernelFaultException("The runtime is already started.");
            }

            executor = effectExecutor ?? throw new ArgumentNullException(nameof(effectExecutor));
            executor.Attach(Completions);
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

                if (!tornDown)
                {
                    recoveryIndex.ExpireTerminals(currentLogicalNow);
                    ResolveTimedOutWaits();
                    PublishStatus();
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

                return BuildReport(turns);
            }
            finally
            {
                Volatile.Write(ref pumping, 0);
            }
        }

        private bool RunOneTurn()
        {
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
            // A lane blocked on the adapter makes queued mutation work
            // non-processable; the report never claims otherwise.
            var workRemaining =
                mailbox.ControlDepth > 0 ||
                mailbox.SubmissionDepth > 0 ||
                (!tornDown && (
                    mailbox.PublicationDepth > 0 ||
                    stalledAdmission != null ||
                    committingEvidence.Count > 0 ||
                    (!awaitingCompletion && (admitted.Count > 0 || active != null))));
            return new PumpReport(
                turns,
                workRemaining,
                mailbox.ControlDepth,
                mailbox.PublicationDepth,
                mailbox.SubmissionDepth,
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
                interaction.Completion?.Continuations ?? ValueList<ContinuationRequest>.Empty);
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
                    committingEvidence.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        private void FinishTerminal(Interaction interaction)
        {
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

        private void AdmitContinuations(Interaction parent, ValueList<ContinuationRequest> continuations)
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
                case TeardownMessage:
                    ProcessTeardown();
                    break;
            }
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
                            ValueList<ClauseEvaluation>.Empty));
                    }

                    assertion.Batch.Observer.OnEvaluated(
                        ValueList<PredicateEvaluationResult>.From(results));
                    break;
                }

                case FenceMessage fence:
                    EmitProtocolViolation(fence.Permit, "FenceRejected");
                    break;
                case CompletionMessage completion:
                    EmitProtocolViolation(completion.Completion.Permit, "CompletionRejected");
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
            Emit(
                intersecting ? EventKind.ContaminationObserved : new EventKind("ObservedExternal"),
                EventCausation.OfExternal(report.SourceHint),
                request: intersecting ? active!.Request : (RequestId?)null,
                revision: nodeStore.Revision);
        }

        private void ProcessRegistration(RegistrationMessage message)
        {
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
                    receipt = nodeStore.TryUpdateAttributes(message.Node, message.Updates!)
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
        }

        private void ResolveWait(OperationId operation, PredicateResolution resolution)
        {
            if (waits.TryGetValue(operation, out var entry))
            {
                waits.Remove(operation);
                Emit(EventKind.PredicateResolved, EventCausation.None, operation: operation);
                entry.Observer.OnResolved(operation, resolution);
            }
        }

        private void ResolveTimedOutWaits()
        {
            List<OperationId>? timedOut = null;
            foreach (var pair in waits)
            {
                if (pair.Value.TimeoutAtLogicalTime <= currentLogicalNow)
                {
                    (timedOut ??= new List<OperationId>()).Add(pair.Key);
                }
            }

            if (timedOut != null)
            {
                foreach (var operation in timedOut)
                {
                    ResolveWait(operation, PredicateResolution.TimedOut);
                }
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
            List<OperationId>? satisfied = null;
            foreach (var pair in waits)
            {
                var result = PredicateEvaluator.Evaluate(
                    pair.Value.Definition, PinReader(pair.Value.Domain), PredicateStructuralBounds.Default);
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

        private void ProcessAssertions(AssertionBatch batch)
        {
            var results = new List<PredicateEvaluationResult>();
            if (!options.TryResolveDomain(batch.Principal, out var domain))
            {
                foreach (var _ in batch.Predicates)
                {
                    results.Add(new PredicateEvaluationResult(
                        PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.OutOfScope),
                        ValueList<ClauseEvaluation>.Empty));
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
                            ValueList<ClauseEvaluation>.Empty));
                        continue;
                    }

                    results.Add(PredicateEvaluator.Evaluate(
                        definition, reader, PredicateStructuralBounds.Default));
                    Emit(EventKind.AssertionEvaluated, EventCausation.None);
                }
            }

            batch.Observer.OnEvaluated(ValueList<PredicateEvaluationResult>.From(results));
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

        private PinnedObservationReader PinReader(SecurityDomainId domain)
        {
            return new PinnedObservationReader(
                nodeStore, sourceTable, domain, options.RecordDomain, kernelView, "root", currentLogicalNow);
        }

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
            trace.Emit(new SemanticEvent(
                kind, Incarnation, causation, request, operation, order, revision, detailCode));
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
                NodeRef node, ValueList<NodeAttribute> updates, IRegistrationObserver? observer)
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
                    bytes += 32 + (field.Name.Length * 2) + (field.Value.ToString().Length * 2);
                }

                return bytes;
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
        }
    }
}
