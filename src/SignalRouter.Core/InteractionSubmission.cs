using System;
using System.Threading.Tasks;

namespace SignalRouter
{
    // Options for the split-phase submission path (ADR 0007): unlike
    // InteractionDispatchOptions, the request identity is supplied by the
    // caller — the transport's host names the request so it stays queryable
    // and safely resendable across disconnects. There is deliberately no
    // idempotency key: transport-level deduplication is the protocol ledger's
    // single authority, and Core trusts the submitter to keep external IDs
    // unique (a live collision fails fast at admission).
    public readonly struct InteractionSubmissionOptions : IEquatable<InteractionSubmissionOptions>
    {
        public InteractionSubmissionOptions(
            string requestId,
            InteractionOrigin origin,
            string? correlationId = null)
        {
            InteractionContract.RequireIdentifier(requestId, nameof(requestId));
            InteractionContract.RequireDefinedEnum(origin, nameof(origin));
            InteractionContract.RequireOptionalIdentifier(correlationId, nameof(correlationId));

            RequestId = requestId;
            Origin = origin;
            CorrelationId = correlationId;
        }

        public string RequestId { get; }

        public InteractionOrigin Origin { get; }

        public string? CorrelationId { get; }

        public bool Equals(InteractionSubmissionOptions other)
        {
            return string.Equals(RequestId, other.RequestId, StringComparison.Ordinal)
                && Origin == other.Origin
                && string.Equals(CorrelationId, other.CorrelationId, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is InteractionSubmissionOptions other && Equals(other);
        }

        public override int GetHashCode()
        {
            return InteractionContract.CombineHashCodes(
                RequestId == null ? 0 : StringComparer.Ordinal.GetHashCode(RequestId),
                (int)Origin,
                CorrelationId == null ? 0 : StringComparer.Ordinal.GetHashCode(CorrelationId));
        }

        public static bool operator ==(
            InteractionSubmissionOptions left,
            InteractionSubmissionOptions right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(
            InteractionSubmissionOptions left,
            InteractionSubmissionOptions right)
        {
            return !left.Equals(right);
        }
    }

    public enum InteractionAdmissionKind
    {
        // The request entered the FIFO; an admission acknowledgment
        // (interaction_accepted) may be sent for it.
        Queued = 0,

        // The request terminated without ever entering the FIFO (an immediate
        // rejection); Completion is already terminal and no admission
        // acknowledgment applies.
        Completed = 1,
    }

    // The synchronous half of a split-phase submission: identity and queue
    // admission are fixed before the constructor runs, while the terminal
    // result rides Completion. Started resolves to true at the moment
    // execution genuinely begins (the predecessor drained and pre-start checks
    // passed — the transport's Queued→Running transition), and to false when
    // the request reaches a terminal state without ever starting, so a
    // consumer can always distinguish the two without racing Completion.
    public sealed class InteractionSubmission
    {
        // Public so transport tests can drive session logic with scripted
        // submissions; the dispatcher remains the only production source.
        public InteractionSubmission(
            InteractionAdmissionKind kind,
            string requestId,
            long sequence,
            Task<bool> started,
            Task<InteractionResult> completion)
        {
            InteractionContract.RequireDefinedEnum(kind, nameof(kind));
            InteractionContract.RequireIdentifier(requestId, nameof(requestId));
            if (sequence < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequence),
                    sequence,
                    "Sequence must be positive.");
            }

            Kind = kind;
            RequestId = requestId;
            Sequence = sequence;
            Started = started ?? throw new ArgumentNullException(nameof(started));
            Completion = completion ?? throw new ArgumentNullException(nameof(completion));
        }

        public InteractionAdmissionKind Kind { get; }

        public string RequestId { get; }

        public long Sequence { get; }

        public Task<bool> Started { get; }

        public Task<InteractionResult> Completion { get; }
    }
}
