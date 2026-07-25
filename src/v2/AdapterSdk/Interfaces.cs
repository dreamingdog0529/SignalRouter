using System;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.AdapterSdk
{
    // ── Adapter-implemented surfaces (adapter-conformance.md §2) ──────────────
    // Threading contracts are declared per member; the SDK never assumes a
    // synchronization context.

    /// <summary>
    /// The adapter's node surface. Attach is called once on the pump thread before
    /// the first pump; the source performs initial construction through the
    /// bootstrap registry and runtime updates through the message-based registry.
    /// </summary>
    public interface INodeSource
    {
        /// <summary>Pump thread, once, before the runtime starts.</summary>
        void Attach(IBootstrapRegistry bootstrap, INodeRegistry registry);

        /// <summary>Pump thread, at incarnation teardown.</summary>
        void Detach();
    }

    /// <summary>
    /// The adapter's ingress surface: ManagedIntent submissions and
    /// ObservedExternal reports flow through the sink (adapter-conformance.md §6).
    /// </summary>
    public interface IIngressSource
    {
        /// <summary>Pump thread, once, before the runtime starts. The sink itself is thread-safe.</summary>
        void Attach(IIngressSink sink);

        /// <summary>Pump thread, at incarnation teardown.</summary>
        void Detach();
    }

    /// <summary>
    /// The adapter's effect surface (adapter-conformance.md §3). `Execute` is called
    /// on the pump thread and MUST adopt or refuse synchronously within the declared
    /// sync bound; no effect may begin before `Adopted` is returned. Fence and
    /// completion messages go to the thread-safe completion sink.
    /// </summary>
    public interface IEffectExecutor
    {
        /// <summary>
        /// Pump thread, once, before the runtime starts: the completion sink the
        /// executor reports fences and completions through for every adopted permit.
        /// </summary>
        void Attach(IEffectCompletionSink sink);

        /// <summary>Pump thread, at incarnation teardown.</summary>
        void Detach();

        /// <summary>Pump thread; synchronous adopt-or-refuse within the declared bound.</summary>
        EffectAdoption Execute(EffectRequest request);

        /// <summary>Pump thread; cooperative — the effect still completes exactly once.</summary>
        void RequestCancel(EffectPermitToken permit);
    }

    /// <summary>
    /// The adapter's pump host (kernel-execution.md §6): drives `Pump`, supplies
    /// frame phases, deadlines, and the two host clocks. The kernel never installs
    /// its own thread or timer.
    /// </summary>
    public interface IPumpHost
    {
        /// <summary>Pump thread, once. The host owns the drive loop thereafter.</summary>
        void Attach(IPumpable kernel);

        /// <summary>Pump thread, at incarnation teardown.</summary>
        void Detach();
    }

    // ── Kernel-implemented counterparts, handed to the adapter at attach ──────

    /// <summary>
    /// The synchronous bootstrap registry (ADR 0010): initial construction before
    /// the runtime starts. Duplicate `AuthorKey`s and duplicate keys throw
    /// immediately (semantic-model.md §3.2, §8). Pump thread only; calls after
    /// start throw.
    /// </summary>
    public interface IBootstrapRegistry
    {
        NodeRef RegisterNode(NodeRegistration registration);

        void RegisterCapabilityContract(CapabilityContractDescriptor descriptor);

        void RegisterStateSource(StateSourceRegistration registration);

        void RegisterPredicateContract(PredicateContractRef contract, PredicateDefinition definition);
    }

    /// <summary>The receipt answering a runtime registration message (ADR 0010).</summary>
    public sealed class RegistrationReceipt
    {
        private RegistrationReceipt(bool succeeded, NodeRef? node, string? failureCode)
        {
            Succeeded = succeeded;
            Node = node;
            FailureCode = failureCode;
        }

        public static RegistrationReceipt Success(NodeRef? node) =>
            new RegistrationReceipt(true, node, null);

        public static RegistrationReceipt Failure(string failureCode) =>
            new RegistrationReceipt(
                false, null, ContractGrammar.ValidateCode(failureCode, nameof(failureCode)));

        public bool Succeeded { get; }

        public NodeRef? Node { get; }

        /// <summary>A stable failure code (e.g. DuplicateAuthorKey); never free text.</summary>
        public string? FailureCode { get; }
    }

    /// <summary>Receives registration receipts; called on the pump thread when the message is processed.</summary>
    public interface IRegistrationObserver
    {
        void OnCompleted(RegistrationReceipt receipt);
    }

    /// <summary>
    /// The message-based runtime registry (kernel-execution.md §4): registration,
    /// unregistration, and attribute updates are bounded control-lane messages
    /// answered with receipts — a duplicate `AuthorKey` fails in the receipt before
    /// any subsequent message. Thread-safe producers.
    /// </summary>
    public interface INodeRegistry
    {
        void Register(NodeRegistration registration, IRegistrationObserver? observer);

        void Unregister(NodeRef node, IRegistrationObserver? observer);

        void UpdateAttributes(NodeRef node, ValueList<NodeAttribute> updates, IRegistrationObserver? observer);

        void SetCapabilityAvailability(
            NodeRef node, CapabilityContractRef capability, bool available, IRegistrationObserver? observer);
    }

    /// <summary>
    /// The kernel's ingress sink. Thread-safe: adoption at the mailbox is the single
    /// linearization point; enqueue-time overflow answers are synchronous
    /// (kernel-execution.md §4).
    /// </summary>
    public interface IIngressSink
    {
        /// <summary>Thread-safe. Overflow of the mutation class rejects via the observer (CapacityExhausted).</summary>
        void Submit(IntentSubmission submission);

        /// <summary>Thread-safe.</summary>
        void ReportObservedExternal(ObservedExternalReport report);

        /// <summary>Thread-safe. Overflow answers `Refused` — a partial document swap never occurs.</summary>
        PublicationAnswer PublishSourceDocument(SourcePublication publication);
    }

    /// <summary>
    /// The kernel's effect-completion sink (adapter-conformance.md §3). Thread-safe.
    /// For every adopted permit: at most one fence report and exactly one
    /// completion; duplicates, unknown tokens, and stale-incarnation tokens are
    /// rejected and traced, never applied.
    /// </summary>
    public interface IEffectCompletionSink
    {
        void ReportFenceReached(EffectPermitToken permit);

        void ReportCompletion(EffectCompletion completion);
    }

    /// <summary>The kernel as the host pumps it (kernel-execution.md §6). Single consumer: concurrent pumps throw.</summary>
    public interface IPumpable
    {
        PumpReport Pump(PumpBudget budget);
    }
}
