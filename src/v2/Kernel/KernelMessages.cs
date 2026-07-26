using System;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>Receives a wait's resolution (verification.md §2.1). Called on the pump thread.</summary>
    public interface IWaitObserver
    {
        void OnResolved(OperationId operation, PredicateResolution resolution);
    }

    /// <summary>Receives an assertion batch's answers. Called on the pump thread.</summary>
    public interface IAssertionObserver
    {
        void OnEvaluated(ValueList<PredicateEvaluationResult> results);
    }

    /// <summary>One assertion batch: N registered predicates against ONE pinned read (verification.md §3.2).</summary>
    public sealed class AssertionBatch
    {
        public AssertionBatch(
            ValueList<PredicateContractRef> predicates,
            Principal principal,
            IAssertionObserver observer)
        {
            if (predicates == null)
            {
                throw new ArgumentNullException(nameof(predicates));
            }

            if (predicates.Count == 0)
            {
                throw new ArgumentException("A batch evaluates at least one predicate.", nameof(predicates));
            }

            Predicates = predicates;
            Principal = principal ?? throw new ArgumentNullException(nameof(principal));
            Observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        public ValueList<PredicateContractRef> Predicates { get; }

        public Principal Principal { get; }

        public IAssertionObserver Observer { get; }
    }

    /// <summary>The kernel's control operations (cancel, waits, assertions, gating, lifecycle).</summary>
    public interface IKernelControl
    {
        /// <summary>Thread-safe: a control-lane cancel request by RequestId (kernel-execution.md §8).</summary>
        void RequestCancel(RequestId request);

        /// <summary>Thread-safe: arm an explicit wait on a registered predicate (control lane).</summary>
        OperationId ArmWait(PredicateContractRef predicate, Principal principal, long timeoutAtLogicalTime, IWaitObserver observer);

        /// <summary>Thread-safe: cancel an armed wait.</summary>
        void CancelWait(OperationId operation);

        /// <summary>Thread-safe: evaluate an assertion batch against one pinned read (control lane).</summary>
        void EvaluateAssertions(AssertionBatch batch);

        /// <summary>Thread-safe: gate foreign mutation admission (exclusive control, kernel-execution.md §7).</summary>
        void AcquireExclusiveControl(SecurityDomainId holder);

        /// <summary>Thread-safe: release the gate.</summary>
        void ReleaseExclusiveControl();

        /// <summary>Thread-safe: incarnation teardown (kernel-execution.md §10); processed as a lifecycle message.</summary>
        void TearDownIncarnation();

        /// <summary>
        /// Thread-safe: pin one revision-consistent snapshot of a registered view
        /// (observation-state.md §4, control lane). Answered split-phase through the
        /// observer; deferred under budget pressure and retried at the next pump
        /// before newly adopted control work.
        /// </summary>
        OperationId RequestSnapshot(
            ViewContractRef view, Principal principal, string scope, ISnapshotObserver observer);

        /// <summary>Thread-safe: release a pinned snapshot.</summary>
        void ReleaseSnapshot(OperationId operation);
    }

    internal abstract class ControlMessage
    {
    }

    internal sealed class CancelMessage : ControlMessage
    {
        internal CancelMessage(RequestId request)
        {
            Request = request;
        }

        internal RequestId Request { get; }
    }

    internal sealed class FenceMessage : ControlMessage
    {
        internal FenceMessage(EffectPermitToken permit)
        {
            Permit = permit;
        }

        internal EffectPermitToken Permit { get; }
    }

    internal sealed class CompletionMessage : ControlMessage
    {
        internal CompletionMessage(EffectCompletion completion)
        {
            Completion = completion;
        }

        internal EffectCompletion Completion { get; }
    }

    internal sealed class ObservedExternalMessage : ControlMessage
    {
        internal ObservedExternalMessage(ObservedExternalReport report)
        {
            Report = report;
        }

        internal ObservedExternalReport Report { get; }
    }

    internal sealed class RegistrationMessage : ControlMessage
    {
        internal enum Operation
        {
            Register,
            Unregister,
            UpdateAttributes,
            SetAvailability,
        }

        internal RegistrationMessage(
            Operation kind,
            NodeRegistration? registration,
            NodeRef node,
            ValueList<NodeAttribute>? updates,
            CapabilityContractRef capability,
            bool available,
            IRegistrationObserver? observer)
        {
            Kind = kind;
            Registration = registration;
            Node = node;
            Updates = updates;
            Capability = capability;
            Available = available;
            Observer = observer;
        }

        internal Operation Kind { get; }

        internal NodeRegistration? Registration { get; }

        internal NodeRef Node { get; }

        internal ValueList<NodeAttribute>? Updates { get; }

        internal CapabilityContractRef Capability { get; }

        internal bool Available { get; }

        internal IRegistrationObserver? Observer { get; }
    }

    internal sealed class ArmWaitMessage : ControlMessage
    {
        internal ArmWaitMessage(
            OperationId operation,
            PredicateContractRef predicate,
            Principal principal,
            long timeoutAtLogicalTime,
            IWaitObserver observer)
        {
            Operation = operation;
            Predicate = predicate;
            Principal = principal;
            TimeoutAtLogicalTime = timeoutAtLogicalTime;
            Observer = observer;
        }

        internal OperationId Operation { get; }

        internal PredicateContractRef Predicate { get; }

        internal Principal Principal { get; }

        internal long TimeoutAtLogicalTime { get; }

        internal IWaitObserver Observer { get; }
    }

    internal sealed class CancelWaitMessage : ControlMessage
    {
        internal CancelWaitMessage(OperationId operation)
        {
            Operation = operation;
        }

        internal OperationId Operation { get; }
    }

    internal sealed class AssertionMessage : ControlMessage
    {
        internal AssertionMessage(AssertionBatch batch)
        {
            Batch = batch;
        }

        internal AssertionBatch Batch { get; }
    }

    internal sealed class GateMessage : ControlMessage
    {
        internal GateMessage(SecurityDomainId holder, bool acquire)
        {
            Holder = holder;
            Acquire = acquire;
        }

        internal SecurityDomainId Holder { get; }

        internal bool Acquire { get; }
    }

    internal sealed class TeardownMessage : ControlMessage
    {
    }

    internal sealed class SnapshotRequestMessage : ControlMessage
    {
        internal SnapshotRequestMessage(
            OperationId operation,
            ViewContractRef view,
            Principal principal,
            string scope,
            ISnapshotObserver observer)
        {
            Operation = operation;
            View = view;
            Principal = principal;
            Scope = scope;
            Observer = observer;
        }

        internal OperationId Operation { get; }

        internal ViewContractRef View { get; }

        internal Principal Principal { get; }

        internal string Scope { get; }

        internal ISnapshotObserver Observer { get; }
    }

    internal sealed class ReleaseSnapshotMessage : ControlMessage
    {
        internal ReleaseSnapshotMessage(OperationId operation)
        {
            Operation = operation;
        }

        internal OperationId Operation { get; }
    }

    internal sealed class SourcePublicationMessage
    {
        internal SourcePublicationMessage(SourcePublication publication, int approximateBytes)
        {
            Publication = publication;
            ApproximateBytes = approximateBytes;
        }

        internal SourcePublication Publication { get; }

        internal int ApproximateBytes { get; }
    }

    internal sealed class SubmissionMessage
    {
        internal SubmissionMessage(IntentSubmission submission, bool humanPriority)
        {
            Submission = submission;
            HumanPriority = humanPriority;
        }

        internal IntentSubmission Submission { get; }

        internal bool HumanPriority { get; }
    }
}
