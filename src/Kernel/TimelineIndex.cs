using System;
using System.Collections.Generic;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel
{
    /// <summary>
    /// One retained state-timeline entry (observation-state.md §8): a checkpoint at
    /// one `SourceRevision`, with the causing `LogicalOrder` as optional metadata —
    /// absent for source-only publications and external mutations, which have no
    /// admission order to cite (ADR 0009/0011).
    /// </summary>
    public sealed class TimelineEntry
    {
        internal TimelineEntry(
            SourceRevision revision,
            ContentId contentId,
            LogicalOrder? causingOrder,
            bool afterGap,
            long logicalTime,
            ulong entrySequence)
        {
            Revision = revision;
            ContentId = contentId;
            CausingOrder = causingOrder;
            AfterGap = afterGap;
            LogicalTime = logicalTime;
            EntrySequence = entrySequence;
        }

        public SourceRevision Revision { get; }

        public ContentId ContentId { get; }

        public LogicalOrder? CausingOrder { get; }

        /// <summary>This checkpoint resynchronized across a gap (a revision advance with no retained entry).</summary>
        public bool AfterGap { get; }

        public long LogicalTime { get; }

        /// <summary>The deterministic per-feed entry sequence (observation-state.md §8).</summary>
        public ulong EntrySequence { get; }
    }

    /// <summary>
    /// The bounded diagnostic state timeline (observation-state.md §8): checkpoints
    /// only in v2.0, doubly bounded (entries and retained bytes), eviction releases
    /// the entry's pin, and the reading surface is principal-bound default-deny —
    /// entries expose record-view `ContentId`s, so a principal outside the record
    /// domain receives no entries, indistinguishably from an empty timeline. It
    /// carries no replay authority.
    /// </summary>
    public sealed class TimelineIndex
    {
        private sealed class Retained
        {
            internal Retained(TimelineEntry entry, int blobBytes)
            {
                Entry = entry;
                BlobBytes = blobBytes;
            }

            internal TimelineEntry Entry { get; }

            internal int BlobBytes { get; }
        }

        private readonly object gate = new object();
        private readonly List<Retained> retained = new List<Retained>();
        private readonly StateStore store;
        private readonly KernelOptions options;
        private long retainedBytes;

        internal TimelineIndex(StateStore store, KernelOptions options)
        {
            this.store = store;
            this.options = options;
        }

        /// <summary>
        /// Default-deny (observation-state.md §8): only a principal bound to the
        /// record domain reads entries; every other principal gets the empty
        /// answer, indistinguishably from an empty timeline.
        /// </summary>
        public ValueArray<TimelineEntry> Snapshot(Principal principal)
        {
            if (principal == null)
            {
                throw new ArgumentNullException(nameof(principal));
            }

            if (!options.TryResolveDomain(principal, out var domain) ||
                !domain.Equals(options.RecordDomain))
            {
                return ValueArray<TimelineEntry>.Empty;
            }

            lock (gate)
            {
                var entries = new List<TimelineEntry>(retained.Count);
                foreach (var item in retained)
                {
                    entries.Add(item.Entry);
                }

                return ValueArray<TimelineEntry>.From(entries);
            }
        }

        /// <summary>
        /// Pump thread: the entry's blob is already retained and pinned by the
        /// timeline owner. Answers false when the entry could not be retained even
        /// after evicting everything older (it exceeds the retention bound by
        /// itself) — the caller records a gap instead of pretending retention.
        /// </summary>
        internal bool Append(TimelineEntry entry, int blobBytes)
        {
            lock (gate)
            {
                var added = new Retained(entry, blobBytes);
                retained.Add(added);
                retainedBytes += blobBytes;
                while (retained.Count > options.TimelineRetentionEntries ||
                    retainedBytes > options.TimelineRetentionBytes)
                {
                    EvictOldestLocked();
                    if (retained.Count == 0)
                    {
                        return false;
                    }
                }

                return retained.Contains(added);
            }
        }

        /// <summary>
        /// Pump thread: releases the oldest diagnostic entry so an evidence put is
        /// never refused because of diagnostic retention (observation-state.md §5.1).
        /// </summary>
        internal bool TryEvictOldest()
        {
            lock (gate)
            {
                if (retained.Count == 0)
                {
                    return false;
                }

                EvictOldestLocked();
                return true;
            }
        }

        internal void Clear()
        {
            lock (gate)
            {
                while (retained.Count > 0)
                {
                    EvictOldestLocked();
                }
            }
        }

        private void EvictOldestLocked()
        {
            var oldest = retained[0];
            retained.RemoveAt(0);
            retainedBytes -= oldest.BlobBytes;
            store.Release(options.RecordDomain, oldest.Entry.ContentId, LeaseOwner.Timeline);
        }
    }
}
