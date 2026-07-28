using System;
using System.Collections.Generic;
using System.Threading;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel
{
    /// <summary>
    /// The principal-bound status query surface (kernel-execution.md §2,
    /// guarantees.md §3.4): read-only, never blocked by the mutation lane.
    /// </summary>
    public interface IKernelQueries
    {
        /// <summary>
        /// Answers `Pending`, `Terminal(outcome)`, or `OutcomeUnknown` — never a
        /// fabricated terminal. A `RequestId` outside the querying principal's
        /// authority answers exactly as an unknown id (existence concealment,
        /// guarantees.md §3.5).
        /// </summary>
        QueryAnswer Query(RequestId request, Principal principal);
    }

    /// <summary>
    /// The owner-published immutable status snapshot (ADR 0010): the pump thread
    /// atomically publishes a new snapshot after every status change; readers
    /// consult the current reference without locks and without entering the
    /// mailbox.
    /// </summary>
    internal sealed class StatusBoard : IKernelQueries
    {
        internal sealed class Entry
        {
            internal Entry(Principal principal, QueryAnswer answer)
            {
                Principal = principal;
                Answer = answer;
            }

            internal Principal Principal { get; }

            internal QueryAnswer Answer { get; }
        }

        private Dictionary<RequestId, Entry> snapshot = new Dictionary<RequestId, Entry>();

        public QueryAnswer Query(RequestId request, Principal principal)
        {
            if (request.IsDefault)
            {
                throw new ArgumentException("Query requires a non-default RequestId.", nameof(request));
            }

            if (principal == null)
            {
                throw new ArgumentNullException(nameof(principal));
            }

            var current = Volatile.Read(ref snapshot);
            if (!current.TryGetValue(request, out var entry) || !entry.Principal.Equals(principal))
            {
                // Unknown id and unauthorized id are observationally identical.
                return QueryAnswer.OutcomeUnknown;
            }

            return entry.Answer;
        }

        /// <summary>Pump thread: publish a new immutable snapshot.</summary>
        internal void Publish(IEnumerable<KeyValuePair<RequestId, Entry>> entries)
        {
            var next = new Dictionary<RequestId, Entry>();
            foreach (var pair in entries)
            {
                next[pair.Key] = pair.Value;
            }

            Volatile.Write(ref snapshot, next);
        }
    }
}
