using System;
using System.Collections.Generic;
using System.Globalization;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>
    /// The always-on, bounded, lossy-permitted, gap-marked diagnostics store
    /// (observation-state.md §6). Loss is never silent: evictions are counted and a
    /// `TraceGap` marker with the dropped count stands where the loss happened.
    /// Never an input to recording (ADR 0003).
    ///
    /// Storage is a preallocated circular buffer of compact value-type entries:
    /// eviction is O(1) per evicted slot (never a rebuild of the retained tail),
    /// a steady-state emit at capacity allocates nothing (the gap marker carries
    /// its drop count as an integer; its text and the public
    /// <see cref="SemanticEvent"/>s materialize only in <see cref="Snapshot"/>),
    /// and the ring holds no references to caller-allocated events.
    /// </summary>
    public sealed class KernelTraceRing
    {
        /// <summary>One retained event; a positive <see cref="GapDroppedCount"/> marks a gap entry.</summary>
        private readonly struct TraceEntry
        {
            internal TraceEntry(
                EventKind kind,
                RuntimeIncarnationId incarnation,
                EventCausation causation,
                RequestId? request,
                OperationId? operation,
                LogicalOrder? order,
                SourceRevision? revision,
                string? detailCode,
                int gapDroppedCount,
                int estimatedBytes)
            {
                Kind = kind;
                Incarnation = incarnation;
                Causation = causation;
                Request = request;
                Operation = operation;
                Order = order;
                Revision = revision;
                DetailCode = detailCode;
                GapDroppedCount = gapDroppedCount;
                EstimatedBytes = estimatedBytes;
            }

            internal EventKind Kind { get; }

            internal RuntimeIncarnationId Incarnation { get; }

            internal EventCausation Causation { get; }

            internal RequestId? Request { get; }

            internal OperationId? Operation { get; }

            internal LogicalOrder? Order { get; }

            internal SourceRevision? Revision { get; }

            internal string? DetailCode { get; }

            internal int GapDroppedCount { get; }

            internal int EstimatedBytes { get; }
        }

        private const int GapReservedBytes = 96;

        private readonly object gate = new object();
        private readonly TraceEntry[] slots;
        private readonly int capacity;
        private readonly int byteCapacity;
        private int head;
        private int count;
        private int approximateBytes;
        private long totalDropped;

        public KernelTraceRing(int capacity, int byteCapacity)
        {
            if (capacity < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity), "The ring holds at least an event and a gap marker.");
            }

            if (byteCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(byteCapacity));
            }

            this.capacity = capacity;
            this.byteCapacity = byteCapacity;
            slots = new TraceEntry[capacity];
        }

        public long TotalDropped
        {
            get
            {
                lock (gate)
                {
                    return totalDropped;
                }
            }
        }

        public void Emit(SemanticEvent semanticEvent)
        {
            if (semanticEvent == null)
            {
                throw new ArgumentNullException(nameof(semanticEvent));
            }

            Emit(
                semanticEvent.Kind,
                semanticEvent.Incarnation,
                semanticEvent.Causation,
                semanticEvent.Request,
                semanticEvent.Operation,
                semanticEvent.Order,
                semanticEvent.Revision,
                semanticEvent.DetailCode);
        }

        /// <summary>
        /// The kernel-internal emission path: the event fields travel as values,
        /// so a trace emission allocates no <see cref="SemanticEvent"/> — the
        /// public objects materialize only when a diagnostic reader snapshots.
        /// Detail codes keep the fail-fast validation of the public constructor.
        /// </summary>
        internal void Emit(
            EventKind kind,
            RuntimeIncarnationId incarnation,
            EventCausation causation,
            RequestId? request = null,
            OperationId? operation = null,
            LogicalOrder? order = null,
            SourceRevision? revision = null,
            string? detailCode = null)
        {
            if (kind.IsDefault)
            {
                throw new ArgumentException("Event requires a non-default kind.", nameof(kind));
            }

            if (incarnation.IsDefault)
            {
                throw new ArgumentException("Event requires a non-default incarnation.", nameof(incarnation));
            }

            if (detailCode != null)
            {
                ContractGrammar.ValidateCode(detailCode, nameof(detailCode));
            }

            var estimated = EstimateBytes(kind, incarnation, detailCode?.Length ?? 0);
            lock (gate)
            {
                // Evict-before-write over the virtual state that includes the new
                // event: the same fixed-point as the historical enqueue-then-evict
                // (over the ceiling at arrival evicts to one below the bounds so
                // the gap marker itself never pushes the ring past them; the
                // newest event always survives).
                var virtualCount = count + 1;
                var virtualBytes = approximateBytes + estimated;
                var overLimit = virtualCount > capacity || virtualBytes > byteCapacity;
                var droppedNow = 0;
                while (overLimit &&
                    (virtualCount > capacity - 1 || virtualBytes > byteCapacity - GapReservedBytes))
                {
                    if (virtualCount == 1)
                    {
                        break;
                    }

                    ref var evicted = ref slots[head];
                    virtualBytes -= evicted.EstimatedBytes;
                    if (evicted.GapDroppedCount == 0)
                    {
                        droppedNow++;
                    }

                    evicted = default; // release string references
                    head = (head + 1) % capacity;
                    virtualCount--;
                }

                count = virtualCount - 1;
                approximateBytes = virtualBytes - estimated;

                if (droppedNow > 0)
                {
                    totalDropped += droppedNow;
                    var gap = new TraceEntry(
                        EventKind.TraceGap,
                        incarnation,
                        EventCausation.None,
                        request: null,
                        operation: null,
                        order: null,
                        revision: null,
                        detailCode: null,
                        gapDroppedCount: droppedNow,
                        EstimateBytes(EventKind.TraceGap, incarnation, GapDetailLength(droppedNow)));

                    // The marker stands at the loss point: in front of the
                    // retained tail, in the slot eviction just freed.
                    head = (head - 1 + capacity) % capacity;
                    slots[head] = gap;
                    count++;
                    approximateBytes += gap.EstimatedBytes;
                }

                slots[(head + count) % capacity] = new TraceEntry(
                    kind, incarnation, causation, request, operation, order, revision,
                    detailCode, gapDroppedCount: 0, estimated);
                count++;
                approximateBytes += estimated;
            }
        }

        /// <summary>A point-in-time copy for diagnostics; never replay authority.</summary>
        public ValueList<SemanticEvent> Snapshot()
        {
            lock (gate)
            {
                var events = new List<SemanticEvent>(count);
                for (var i = 0; i < count; i++)
                {
                    ref var entry = ref slots[(head + i) % capacity];
                    events.Add(entry.GapDroppedCount > 0
                        ? new SemanticEvent(
                            entry.Kind,
                            entry.Incarnation,
                            entry.Causation,
                            detailCode: "Dropped" + entry.GapDroppedCount.ToString(CultureInfo.InvariantCulture))
                        : new SemanticEvent(
                            entry.Kind,
                            entry.Incarnation,
                            entry.Causation,
                            entry.Request,
                            entry.Operation,
                            entry.Order,
                            entry.Revision,
                            entry.DetailCode));
                }

                return ValueList<SemanticEvent>.From(events);
            }
        }

        private static int EstimateBytes(EventKind kind, RuntimeIncarnationId incarnation, int detailLength)
        {
            var bytes = 64;
            bytes += kind.Value.Length * 2;
            bytes += incarnation.Value.Length * 2;
            bytes += detailLength * 2;
            return bytes;
        }

        /// <summary>Length of "Dropped{n}" without building the string.</summary>
        private static int GapDetailLength(int droppedCount)
        {
            var digits = 1;
            for (var value = droppedCount; value >= 10; value /= 10)
            {
                digits++;
            }

            return 7 + digits;
        }
    }
}
