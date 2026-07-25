using System;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.AdapterSdk
{
    /// <summary>Split-phase admission answers delivered to the submitter (protocol-topology.md §4).</summary>
    public interface ISubmissionObserver
    {
        void OnAccepted(RequestId request);

        void OnRejected(RequestId request, RejectionReason reason);
    }

    /// <summary>
    /// One submission (kernel-execution.md §3): the caller-assigned `RequestId`, the
    /// invocation with its ephemeral typed payload, and the identity envelope. The
    /// kernel derives the authoritative fingerprint from the canonicalized payload.
    /// </summary>
    public sealed class IntentSubmission
    {
        public IntentSubmission(
            RequestId request,
            CapabilityInvocation invocation,
            InvocationPayload payload,
            IdentityEnvelope envelope,
            ISubmissionObserver? observer)
        {
            if (request.IsDefault)
            {
                throw new ArgumentException("Submission requires a non-default RequestId.", nameof(request));
            }

            Request = request;
            Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            Observer = observer;
        }

        public RequestId Request { get; }

        public CapabilityInvocation Invocation { get; }

        /// <summary>Ephemeral (kernel-execution.md §3): never stored, lifetime ends at terminal.</summary>
        public InvocationPayload Payload { get; }

        public IdentityEnvelope Envelope { get; }

        public ISubmissionObserver? Observer { get; }
    }

    /// <summary>
    /// One ObservedExternal report (adapter-conformance.md §6.2): an effect the
    /// adapter could not prevent or pre-capture. Traced, never promoted to
    /// replayable evidence; contamination rules apply when it intersects controlled
    /// work.
    /// </summary>
    public sealed class ObservedExternalReport
    {
        public ObservedExternalReport(string sourceHint, NodeRef? node, AuthorKey? authorKey)
        {
            SourceHint = ContractGrammar.ValidateIdentifier(sourceHint, nameof(sourceHint));
            if (node.HasValue && node.Value.IsDefault)
            {
                throw new ArgumentException("A present node must be non-default.", nameof(node));
            }

            if (authorKey.HasValue && authorKey.Value.IsDefault)
            {
                throw new ArgumentException("A present author key must be non-default.", nameof(authorKey));
            }

            Node = node;
            AuthorKey = authorKey;
        }

        public string SourceHint { get; }

        public NodeRef? Node { get; }

        public AuthorKey? AuthorKey { get; }
    }

    /// <summary>
    /// One revision-bound source publication (observation-state.md §7.1): an
    /// immutable typed document with its causation, adopted atomically at the
    /// mailbox — the document swap and the `SourceRevision` advance are one step.
    /// </summary>
    public sealed class SourcePublication
    {
        public SourcePublication(StateSourceKey source, SourceDocument document, EventCausation causation)
        {
            if (source.IsDefault)
            {
                throw new ArgumentException("Publication requires a non-default source key.", nameof(source));
            }

            Source = source;
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Causation = causation;
        }

        public StateSourceKey Source { get; }

        public SourceDocument Document { get; }

        public EventCausation Causation { get; }
    }

    /// <summary>The synchronous answer of a publication enqueue: accepted, or explicitly refused on overflow (kernel-execution.md §4).</summary>
    public enum PublicationAnswer
    {
        Accepted,
        Refused,
    }
}
