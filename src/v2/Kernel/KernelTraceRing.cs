using System;
using System.Collections.Generic;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>
    /// The always-on, bounded, lossy-permitted, gap-marked diagnostics store
    /// (observation-state.md §6). Loss is never silent: evictions are counted and a
    /// `TraceGap` marker with the dropped count stands where the loss happened.
    /// Never an input to recording (ADR 0003).
    /// </summary>
    public sealed class KernelTraceRing
    {
        private readonly object gate = new object();
        private readonly Queue<SemanticEvent> events = new Queue<SemanticEvent>();
        private readonly int capacity;
        private readonly int byteCapacity;
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

            lock (gate)
            {
                events.Enqueue(semanticEvent);
                approximateBytes += EstimateBytes(semanticEvent);

                // Evict to one below the bounds so the gap marker itself never
                // pushes the ring past its configured capacity or byte ceiling.
                var droppedNow = 0;
                var gapBytes = 96;
                var overLimit = events.Count > capacity || approximateBytes > byteCapacity;
                while (overLimit &&
                    (events.Count > capacity - 1 || approximateBytes > byteCapacity - gapBytes))
                {
                    if (events.Count == 1)
                    {
                        break;
                    }

                    var evicted = events.Dequeue();
                    approximateBytes -= EstimateBytes(evicted);
                    if (evicted.Kind != EventKind.TraceGap)
                    {
                        droppedNow++;
                    }
                }

                if (droppedNow > 0)
                {
                    totalDropped += droppedNow;
                    var gap = new SemanticEvent(
                        EventKind.TraceGap,
                        semanticEvent.Incarnation,
                        EventCausation.None,
                        detailCode: "Dropped" + droppedNow.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));

                    // The gap marker stands at the loss point: rebuild with the
                    // marker in front of the retained tail.
                    var retained = events.ToArray();
                    events.Clear();
                    events.Enqueue(gap);
                    approximateBytes = EstimateBytes(gap);
                    foreach (var retainedEvent in retained)
                    {
                        events.Enqueue(retainedEvent);
                        approximateBytes += EstimateBytes(retainedEvent);
                    }
                }
            }
        }

        /// <summary>A point-in-time copy for diagnostics; never replay authority.</summary>
        public ValueList<SemanticEvent> Snapshot()
        {
            lock (gate)
            {
                return ValueList<SemanticEvent>.From(events);
            }
        }

        private static int EstimateBytes(SemanticEvent semanticEvent)
        {
            var bytes = 64;
            bytes += semanticEvent.Kind.Value.Length * 2;
            bytes += semanticEvent.Incarnation.Value.Length * 2;
            if (semanticEvent.DetailCode != null)
            {
                bytes += semanticEvent.DetailCode.Length * 2;
            }

            return bytes;
        }
    }
}
