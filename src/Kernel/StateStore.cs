using System;
using System.Collections.Generic;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel
{
    /// <summary>
    /// A discriminated pin-lease identity (observation-state.md §5.1): a
    /// snapshot/recording operation, an interaction's retained after-basis, or the
    /// reserved timeline owner — every pin consumer has an owner and a release point.
    /// </summary>
    internal readonly struct LeaseOwner : IEquatable<LeaseOwner>
    {
        private const byte OperationKind = 1;
        private const byte RequestKind = 2;
        private const byte TimelineKind = 3;

        private readonly byte kind;
        private readonly OperationId operation;
        private readonly RequestId request;

        private LeaseOwner(byte kind, OperationId operation, RequestId request)
        {
            this.kind = kind;
            this.operation = operation;
            this.request = request;
        }

        internal static LeaseOwner Of(OperationId operation) =>
            new LeaseOwner(OperationKind, operation, default);

        internal static LeaseOwner OfRequest(RequestId request) =>
            new LeaseOwner(RequestKind, default, request);

        internal static LeaseOwner Timeline => new LeaseOwner(TimelineKind, default, default);

        public bool Equals(LeaseOwner other) =>
            kind == other.kind && operation.Equals(other.operation) && request.Equals(other.request);

        public override bool Equals(object? obj) => obj is LeaseOwner other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(kind, operation.GetHashCode());
            return ContractGrammar.CombineHashes(hash, request.GetHashCode());
        }
    }

    /// <summary>The structured answer of a StateStore put — refusal is never silent (observation-state.md §5.1).</summary>
    internal enum PutAnswer
    {
        Retained,
        OverBlobBound,
        OverBudget,
    }

    /// <summary>
    /// The in-memory, content-addressed StateStore core (observation-state.md §5,
    /// ADR 0011): a cache of post-redaction view materializations. Blobs are
    /// immutable and `Put` is idempotent by content; lookups are domain-keyed so a
    /// cross-domain probe is indistinguishable from a miss; pins are reference
    /// counts per lease owner; GC evicts unpinned blobs oldest-insertion-first at
    /// over-budget put and never touches a pinned blob. Pump thread only. This
    /// cache is not the durable commit — durability belongs to the recording module.
    /// </summary>
    internal sealed class StateStore
    {
        private sealed class Entry
        {
            internal Entry(ObservationMaterialization blob, int bytes, long insertOrder)
            {
                Blob = blob;
                Bytes = bytes;
                InsertOrder = insertOrder;
            }

            internal ObservationMaterialization Blob { get; }

            internal int Bytes { get; }

            internal long InsertOrder { get; }

            internal Dictionary<LeaseOwner, int> Pins { get; } = new Dictionary<LeaseOwner, int>();
        }

        private readonly struct StoreKey : IEquatable<StoreKey>
        {
            internal StoreKey(SecurityDomainId domain, ContentId id)
            {
                Domain = domain;
                Id = id;
            }

            internal SecurityDomainId Domain { get; }

            internal ContentId Id { get; }

            public bool Equals(StoreKey other) => Domain.Equals(other.Domain) && Id.Equals(other.Id);

            public override bool Equals(object? obj) => obj is StoreKey other && Equals(other);

            public override int GetHashCode() =>
                ContractGrammar.CombineHashes(Domain.GetHashCode(), Id.GetHashCode());
        }

        private readonly Dictionary<StoreKey, Entry> entries = new Dictionary<StoreKey, Entry>();

        // Reverse lease index: owner → the keys it currently pins. Kept exactly in
        // step with Entry.Pins so ReleaseOwner touches only the owner's own leases
        // — never a scan over unrelated retained blobs (performance.md §2,
        // proportionality).
        private readonly Dictionary<LeaseOwner, HashSet<StoreKey>> ownerLeases =
            new Dictionary<LeaseOwner, HashSet<StoreKey>>();

        private readonly int maxBlobBytes;
        private readonly long maxTotalBytes;
        private long totalBytes;
        private long insertCounter;

        internal StateStore(int maxBlobBytes, long maxTotalBytes)
        {
            this.maxBlobBytes = maxBlobBytes;
            this.maxTotalBytes = maxTotalBytes;
        }

        internal long TotalBytes => totalBytes;

        internal int Count => entries.Count;

        internal PutAnswer TryPut(
            SecurityDomainId domain, ContentId id, ObservationMaterialization blob, int exactBytes)
        {
            if (exactBytes > maxBlobBytes)
            {
                return PutAnswer.OverBlobBound;
            }

            var key = new StoreKey(domain, id);
            if (entries.ContainsKey(key))
            {
                // Idempotent by content: the address is derived from the bytes.
                return PutAnswer.Retained;
            }

            if (totalBytes + exactBytes > maxTotalBytes)
            {
                EvictUnpinned(totalBytes + exactBytes - maxTotalBytes);
                if (totalBytes + exactBytes > maxTotalBytes)
                {
                    return PutAnswer.OverBudget;
                }
            }

            entries.Add(key, new Entry(blob, exactBytes, insertCounter++));
            totalBytes += exactBytes;
            return PutAnswer.Retained;
        }

        internal bool TryGet(SecurityDomainId domain, ContentId id, out ObservationMaterialization blob)
        {
            if (entries.TryGetValue(new StoreKey(domain, id), out var entry))
            {
                blob = entry.Blob;
                return true;
            }

            // A cross-domain probe answers exactly as a miss (observation-state.md §5).
            blob = null!;
            return false;
        }

        internal bool Contains(SecurityDomainId domain, ContentId id) =>
            entries.ContainsKey(new StoreKey(domain, id));

        internal bool IsPinned(SecurityDomainId domain, ContentId id) =>
            entries.TryGetValue(new StoreKey(domain, id), out var entry) && entry.Pins.Count > 0;

        internal bool TryPin(SecurityDomainId domain, ContentId id, LeaseOwner owner)
        {
            var key = new StoreKey(domain, id);
            if (!entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            entry.Pins.TryGetValue(owner, out var count);
            entry.Pins[owner] = count + 1;
            if (count == 0)
            {
                if (!ownerLeases.TryGetValue(owner, out var keys))
                {
                    keys = new HashSet<StoreKey>();
                    ownerLeases.Add(owner, keys);
                }

                keys.Add(key);
            }

            return true;
        }

        internal void Release(SecurityDomainId domain, ContentId id, LeaseOwner owner)
        {
            var key = new StoreKey(domain, id);
            if (!entries.TryGetValue(key, out var entry) ||
                !entry.Pins.TryGetValue(owner, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                entry.Pins.Remove(owner);
                DropLease(owner, key);
            }
            else
            {
                entry.Pins[owner] = count - 1;
            }
        }

        internal void ReleaseOwner(LeaseOwner owner)
        {
            if (!ownerLeases.TryGetValue(owner, out var keys))
            {
                return;
            }

            foreach (var key in keys)
            {
                if (entries.TryGetValue(key, out var entry))
                {
                    entry.Pins.Remove(owner);
                }
            }

            ownerLeases.Remove(owner);
        }

        internal void Clear()
        {
            entries.Clear();
            ownerLeases.Clear();
            totalBytes = 0;
        }

        private void DropLease(LeaseOwner owner, StoreKey key)
        {
            if (ownerLeases.TryGetValue(owner, out var keys))
            {
                keys.Remove(key);
                if (keys.Count == 0)
                {
                    ownerLeases.Remove(owner);
                }
            }
        }

        private void EvictUnpinned(long bytesNeeded)
        {
            var evictable = new List<KeyValuePair<StoreKey, Entry>>();
            foreach (var pair in entries)
            {
                if (pair.Value.Pins.Count == 0)
                {
                    evictable.Add(pair);
                }
            }

            evictable.Sort((left, right) => left.Value.InsertOrder.CompareTo(right.Value.InsertOrder));
            var freed = 0L;
            foreach (var pair in evictable)
            {
                if (freed >= bytesNeeded)
                {
                    break;
                }

                entries.Remove(pair.Key);
                totalBytes -= pair.Value.Bytes;
                freed += pair.Value.Bytes;
            }
        }
    }
}
