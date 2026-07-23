using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SignalRouter.Protocol
{
    public enum ProtocolRequestState
    {
        Received = 0,
        Queued = 1,
        Running = 2,
        Terminal = 3,
    }

    public enum ProtocolLedgerSubmissionStatus
    {
        // A new request: reserve it, acknowledge admission, and dispatch.
        Admitted = 0,

        // A resend of a known request: answer from the ledger, never dispatch.
        Duplicate = 1,

        // The same request ID with different content: reject, never dispatch.
        Conflict = 2,

        // The ledger cannot take new work without breaking its retention
        // guarantee: reject explicitly instead of silently forgetting requests.
        CapacityExhausted = 3,
    }

    // An immutable view of one ledger entry at the moment it was read.
    public sealed class ProtocolLedgerEntry
    {
        internal ProtocolLedgerEntry(
            string requestId,
            ProtocolRequestState state,
            long? sequence,
            ProtocolInteractionOutcome? outcome,
            bool cancelRequested)
        {
            RequestId = requestId;
            State = state;
            Sequence = sequence;
            Outcome = outcome;
            CancelRequested = cancelRequested;
        }

        public string RequestId { get; }

        public ProtocolRequestState State { get; }

        public long? Sequence { get; }

        public ProtocolInteractionOutcome? Outcome { get; }

        public bool CancelRequested { get; }
    }

    public sealed class ProtocolLedgerSubmission
    {
        private ProtocolLedgerSubmission(
            ProtocolLedgerSubmissionStatus status,
            ProtocolLedgerEntry? entry,
            string? errorCode)
        {
            Status = status;
            Entry = entry;
            ErrorCode = errorCode;
        }

        public ProtocolLedgerSubmissionStatus Status { get; }

        public ProtocolLedgerEntry? Entry { get; }

        public string? ErrorCode { get; }

        internal static ProtocolLedgerSubmission Admitted(ProtocolLedgerEntry entry)
        {
            return new ProtocolLedgerSubmission(
                ProtocolLedgerSubmissionStatus.Admitted,
                entry,
                null);
        }

        internal static ProtocolLedgerSubmission Duplicate(ProtocolLedgerEntry entry)
        {
            return new ProtocolLedgerSubmission(
                ProtocolLedgerSubmissionStatus.Duplicate,
                entry,
                null);
        }

        internal static ProtocolLedgerSubmission Conflict()
        {
            return new ProtocolLedgerSubmission(
                ProtocolLedgerSubmissionStatus.Conflict,
                null,
                ProtocolErrorCodes.RequestIdConflict);
        }

        internal static ProtocolLedgerSubmission CapacityExhausted()
        {
            return new ProtocolLedgerSubmission(
                ProtocolLedgerSubmissionStatus.CapacityExhausted,
                null,
                ProtocolErrorCodes.CapacityExhausted);
        }
    }

    // The runtime-side submission ledger that makes host-owned request identity
    // safe (ADR 0007): every execute_interaction is keyed by its request ID, a
    // byte-exact fingerprint distinguishes an honest resend from a conflicting
    // reuse, and terminal results stay queryable for the retention window so a
    // host can recover after a disconnect (design §7.2, §18.2). Bounded capacity
    // and unlimited exactly-once cannot coexist, so the guarantee is explicit:
    // non-terminal entries are never evicted, terminal entries survive until
    // their retention deadline, and when honoring that would exceed capacity the
    // ledger refuses new work rather than forgetting old work. Expired results
    // answer result_unavailable, which the host must surface as OutcomeUnknown,
    // never as an invented outcome (design §8).
    //
    // Like the semantic registry, the ledger is driven only from the runtime's
    // main-thread pump and is not thread-safe (design §17.2).
    public sealed class ProtocolRequestLedger
    {
        private readonly Dictionary<string, Entry> entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        private readonly string sessionEpoch;
        private readonly int capacity;
        private readonly TimeSpan retention;
        private readonly IInteractionClock clock;

        public ProtocolRequestLedger(string sessionEpoch, IInteractionClock clock)
            : this(
                sessionEpoch,
                ProtocolLimits.DefaultLedgerCapacity,
                ProtocolLimits.DefaultLedgerRetention,
                clock)
        {
        }

        public ProtocolRequestLedger(
            string sessionEpoch,
            int capacity,
            TimeSpan retention,
            IInteractionClock clock)
        {
            ProtocolContract.RequireIdentifier(sessionEpoch, nameof(sessionEpoch));
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    capacity,
                    "Capacity must be positive.");
            }

            if (retention <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retention),
                    retention,
                    "Retention must be positive.");
            }

            this.sessionEpoch = sessionEpoch;
            this.capacity = capacity;
            this.retention = retention;
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public string SessionEpoch
        {
            get { return sessionEpoch; }
        }

        public int Count
        {
            get { return entries.Count; }
        }

        public ProtocolLedgerSubmission Submit(ExecuteInteractionMessage request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // The connection state machine already closes on foreign epochs; a
            // mismatch here is a local wiring bug, not peer behavior.
            if (!string.Equals(request.SessionEpoch, sessionEpoch, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The request belongs to a different session epoch.",
                    nameof(request));
            }

            var now = clock.UtcNow;
            EvictExpired(now);
            var fingerprint = ComputeFingerprint(request);
            if (entries.TryGetValue(request.RequestId!, out var existing))
            {
                return string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? ProtocolLedgerSubmission.Duplicate(existing.ToView())
                    : ProtocolLedgerSubmission.Conflict();
            }

            if (entries.Count >= capacity)
            {
                return ProtocolLedgerSubmission.CapacityExhausted();
            }

            var entry = new Entry(request.RequestId!, fingerprint);
            entries.Add(entry.RequestId, entry);
            return ProtocolLedgerSubmission.Admitted(entry.ToView());
        }

        public void MarkQueued(string requestId, long sequence)
        {
            if (sequence < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequence),
                    sequence,
                    "Sequence must be positive.");
            }

            var entry = RequireEntry(requestId);
            RequireForwardTransition(entry, ProtocolRequestState.Queued);
            entry.State = ProtocolRequestState.Queued;
            entry.Sequence = sequence;
        }

        public void MarkRunning(string requestId)
        {
            var entry = RequireEntry(requestId);
            RequireForwardTransition(entry, ProtocolRequestState.Running);
            entry.State = ProtocolRequestState.Running;
        }

        public void MarkTerminal(string requestId, ProtocolInteractionOutcome outcome)
        {
            if (outcome == null)
            {
                throw new ArgumentNullException(nameof(outcome));
            }

            var entry = RequireEntry(requestId);
            if (!string.Equals(outcome.RequestId, entry.RequestId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The outcome does not belong to this request.",
                    nameof(outcome));
            }

            // The sequence acknowledged at admission and the one on the terminal
            // result must be the same dispatch; letting them disagree would make
            // the ledger vouch for two different orderings of one request.
            if (entry.Sequence.HasValue && outcome.Sequence != entry.Sequence.Value)
            {
                throw new ArgumentException(
                    "The outcome does not match the request's queued sequence.",
                    nameof(outcome));
            }

            RequireForwardTransition(entry, ProtocolRequestState.Terminal);
            entry.State = ProtocolRequestState.Terminal;
            entry.Outcome = outcome;
            entry.RetainUntil = clock.UtcNow + retention;
        }

        // Records that cancellation was requested for a live entry, so a
        // status reply can tell a reconnecting host its cancel intent arrived
        // and the host can stop resending it. Idempotent; false for unknown or
        // already-terminal requests, mirroring the dispatcher's TryCancel.
        public bool TryMarkCancelRequested(string requestId)
        {
            ProtocolContract.RequireIdentifier(requestId, nameof(requestId));
            if (!entries.TryGetValue(requestId, out var entry)
                || entry.State == ProtocolRequestState.Terminal)
            {
                return false;
            }

            entry.CancelRequested = true;
            return true;
        }

        // Discards a reservation that never reached Core admission — the one
        // case where forgetting is correct: the submitter got no acceptance,
        // Core queued nothing, and keeping the entry would make its honest
        // resend a false Duplicate forever (a Received entry is never evicted
        // by retention). Any later state means work was admitted and must run
        // to a terminal state instead of being forgotten (ADR 0007).
        public void Abandon(string requestId)
        {
            ProtocolContract.RequireIdentifier(requestId, nameof(requestId));
            if (!entries.TryGetValue(requestId, out var entry))
            {
                throw new InvalidOperationException(
                    "The request is not tracked by this ledger.");
            }

            if (entry.State != ProtocolRequestState.Received)
            {
                throw new InvalidOperationException(
                    "Only reservations that never reached admission may be abandoned.");
            }

            entries.Remove(requestId);
        }

        // Answers get_interaction_result: a null return means the request is
        // unknown or its retention expired, which the caller reports as
        // result_unavailable (design §18.2).
        public ProtocolLedgerEntry? TryGet(string requestId)
        {
            ProtocolContract.RequireIdentifier(requestId, nameof(requestId));
            EvictExpired(clock.UtcNow);
            return entries.TryGetValue(requestId, out var entry) ? entry.ToView() : null;
        }

        private Entry RequireEntry(string requestId)
        {
            ProtocolContract.RequireIdentifier(requestId, nameof(requestId));
            if (!entries.TryGetValue(requestId, out var entry))
            {
                throw new InvalidOperationException(
                    "The request is not tracked by this ledger.");
            }

            return entry;
        }

        private static void RequireForwardTransition(Entry entry, ProtocolRequestState next)
        {
            if (entry.State >= next)
            {
                throw new InvalidOperationException(
                    "Request states only move forward from "
                    + entry.State + " and never repeat.");
            }
        }

        private void EvictExpired(DateTimeOffset now)
        {
            List<string>? expired = null;
            foreach (var pair in entries)
            {
                if (pair.Value.State == ProtocolRequestState.Terminal
                    && pair.Value.RetainUntil <= now)
                {
                    expired = expired ?? new List<string>();
                    expired.Add(pair.Key);
                }
            }

            if (expired != null)
            {
                for (var index = 0; index < expired.Count; index++)
                {
                    entries.Remove(expired[index]);
                }
            }
        }

        // A byte-exact fingerprint over everything that defines the request
        // except its identity. A resend must repeat the arguments text verbatim;
        // semantically equal but differently formatted JSON is treated as a
        // conflict, which errs on the side of never double-executing.
        private static string ComputeFingerprint(ExecuteInteractionMessage request)
        {
            var builder = new StringBuilder();
            builder.Append(request.CommandName).Append('\n');
            builder.Append(request.CommandVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(request.TargetId).Append('\n');
            builder.Append(request.ArgumentsJson).Append('\n');
            builder.Append(request.CorrelationId ?? string.Empty).Append('\n');
            builder.Append(request.IdempotencyKey ?? string.Empty);
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var text = new StringBuilder(hash.Length * 2);
                for (var index = 0; index < hash.Length; index++)
                {
                    text.Append(hash[index].ToString(
                        "x2",
                        System.Globalization.CultureInfo.InvariantCulture));
                }

                return text.ToString();
            }
        }

        private sealed class Entry
        {
            public Entry(string requestId, string fingerprint)
            {
                RequestId = requestId;
                Fingerprint = fingerprint;
                State = ProtocolRequestState.Received;
            }

            public string RequestId { get; }

            public string Fingerprint { get; }

            public ProtocolRequestState State { get; set; }

            public long? Sequence { get; set; }

            public ProtocolInteractionOutcome? Outcome { get; set; }

            public DateTimeOffset RetainUntil { get; set; }

            public bool CancelRequested { get; set; }

            public ProtocolLedgerEntry ToView()
            {
                return new ProtocolLedgerEntry(
                    RequestId,
                    State,
                    Sequence,
                    Outcome,
                    CancelRequested);
            }
        }
    }
}
