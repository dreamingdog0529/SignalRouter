using System;
using System.Collections.Generic;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel
{
    /// <summary>
    /// The recovery store (observation-state.md §6, guarantees.md §8): pending
    /// entries are non-evictable; terminals are retained for the logical-time
    /// retention window; at capacity — pending or unexpired-terminal — new
    /// admissions are refused, existing entries never evicted. Also the dedup
    /// authority: `(RequestId → fingerprint)` within the incarnation. Pump-thread
    /// only.
    /// </summary>
    internal sealed class RecoveryIndex
    {
        internal sealed class PendingEntry
        {
            internal PendingEntry(SemanticFingerprint fingerprint, Principal principal, LogicalOrder order)
            {
                Fingerprint = fingerprint;
                Principal = principal;
                Order = order;
            }

            internal SemanticFingerprint Fingerprint { get; }

            internal Principal Principal { get; }

            internal LogicalOrder Order { get; }
        }

        internal sealed class TerminalEntry
        {
            internal TerminalEntry(
                SemanticFingerprint fingerprint,
                Principal principal,
                InteractionOutcome outcome,
                long expiresAtLogicalTime)
            {
                Fingerprint = fingerprint;
                Principal = principal;
                Outcome = outcome;
                ExpiresAtLogicalTime = expiresAtLogicalTime;
            }

            internal SemanticFingerprint Fingerprint { get; }

            internal Principal Principal { get; }

            internal InteractionOutcome Outcome { get; }

            internal long ExpiresAtLogicalTime { get; }
        }

        private readonly Dictionary<RequestId, PendingEntry> pending =
            new Dictionary<RequestId, PendingEntry>();

        private readonly Dictionary<RequestId, TerminalEntry> terminals =
            new Dictionary<RequestId, TerminalEntry>();

        private readonly DeadlineIndex<RequestId> terminalDeadlines = new DeadlineIndex<RequestId>();

        private readonly int pendingCapacity;
        private readonly int terminalCapacity;
        private readonly long retention;

        internal RecoveryIndex(int pendingCapacity, int terminalCapacity, long retentionLogicalTime)
        {
            this.pendingCapacity = pendingCapacity;
            this.terminalCapacity = terminalCapacity;
            retention = retentionLogicalTime;
        }

        internal bool AtCapacity => pending.Count >= pendingCapacity || terminals.Count >= terminalCapacity;

        internal IEnumerable<KeyValuePair<RequestId, PendingEntry>> Pendings => pending;

        internal IEnumerable<KeyValuePair<RequestId, TerminalEntry>> Terminals => terminals;

        internal bool TryGetPending(RequestId request, out PendingEntry entry) =>
            pending.TryGetValue(request, out entry!);

        internal bool TryGetTerminal(RequestId request, out TerminalEntry entry) =>
            terminals.TryGetValue(request, out entry!);

        internal void RegisterPending(
            RequestId request, SemanticFingerprint fingerprint, Principal principal, LogicalOrder order)
        {
            pending.Add(request, new PendingEntry(fingerprint, principal, order));
        }

        internal void CommitTerminal(RequestId request, InteractionOutcome outcome, long nowLogicalTime)
        {
            if (!pending.TryGetValue(request, out var entry))
            {
                throw new KernelFaultException("A terminal commit requires a pending entry.");
            }

            pending.Remove(request);
            if (terminals.ContainsKey(request))
            {
                // Defensive: an overwrite (impossible under admission dedup)
                // must not leave a stale deadline behind.
                terminalDeadlines.Remove(request);
            }

            terminals[request] = new TerminalEntry(
                entry.Fingerprint, entry.Principal, outcome, nowLogicalTime + retention);
            terminalDeadlines.Add(request, nowLogicalTime + retention);
        }

        /// <summary>
        /// Terminals expire only by retention, evaluated at pump boundaries
        /// (ADR 0010). Answers whether any entry expired, so the caller
        /// republishes the status snapshot only when the status actually changed.
        /// Deadline-indexed: a pump with nothing due peeks once instead of
        /// scanning every retained terminal (performance-track finding A4).
        /// </summary>
        internal bool ExpireTerminals(long nowLogicalTime)
        {
            var any = false;
            while (terminalDeadlines.TryPopExpired(nowLogicalTime, out var request))
            {
                terminals.Remove(request);
                any = true;
            }

            return any;
        }

        /// <summary>Incarnation teardown: strand every pending entry (guarantees.md §7).</summary>
        internal ValueArray<RequestId> DrainPending()
        {
            var stranded = new List<RequestId>(pending.Keys);
            pending.Clear();
            return ValueArray<RequestId>.From(stranded);
        }
    }
}
