using System;
using SignalRouter.Contracts;

namespace SignalRouter.Codec.Recording
{
    /// <summary>
    /// The reserved code strings of the TimelineTrack lane (recording-replay.md
    /// §3; ADR 0016). The vocabulary is open: a reader skips a timeline kind it
    /// does not know — the lane is droppable diagnostics, never evidence.
    /// </summary>
    public static class TimelineRecordKinds
    {
        /// <summary>An armed wait was re-evaluated and stayed unsatisfied.</summary>
        public const string WaitPoll = "WaitPoll";

        /// <summary>Marks lost timeline events (loss is permitted and marked).</summary>
        public const string Gap = "TimelineGap";
    }

    /// <summary>
    /// One decoded TimelineTrack record (kind 0x03). Strict replay never
    /// depends on these; they carry no <c>EvidenceSequence</c> and are excluded
    /// from closure and the declared event count (ADR 0016).
    /// </summary>
    public sealed class TimelineRecord
    {
        private TimelineRecord(
            string kind,
            OperationId operation,
            PredicateContractRef predicate,
            SourceRevision revision,
            long droppedCount)
        {
            Kind = kind;
            Operation = operation;
            Predicate = predicate;
            Revision = revision;
            DroppedCount = droppedCount;
        }

        /// <summary>One of the <see cref="TimelineRecordKinds"/> codes.</summary>
        public string Kind { get; }

        /// <summary>The polled wait; default outside <see cref="TimelineRecordKinds.WaitPoll"/>.</summary>
        public OperationId Operation { get; }

        /// <summary>The polled predicate; default outside <see cref="TimelineRecordKinds.WaitPoll"/>.</summary>
        public PredicateContractRef Predicate { get; }

        /// <summary>The revision the poll evaluated at; default outside <see cref="TimelineRecordKinds.WaitPoll"/>.</summary>
        public SourceRevision Revision { get; }

        /// <summary>How many events were dropped; zero outside <see cref="TimelineRecordKinds.Gap"/>.</summary>
        public long DroppedCount { get; }

        public static TimelineRecord WaitPoll(
            OperationId operation, PredicateContractRef predicate, SourceRevision revision)
        {
            if (operation.IsDefault)
            {
                throw new ArgumentException(
                    "A wait poll requires a non-default operation.", nameof(operation));
            }

            if (predicate.IsDefault)
            {
                throw new ArgumentException(
                    "A wait poll requires a non-default predicate reference.", nameof(predicate));
            }

            return new TimelineRecord(
                TimelineRecordKinds.WaitPoll, operation, predicate, revision, droppedCount: 0);
        }

        public static TimelineRecord Gap(long droppedCount)
        {
            if (droppedCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(droppedCount), "A gap marks at least one dropped event.");
            }

            return new TimelineRecord(
                TimelineRecordKinds.Gap, default, default, default, droppedCount);
        }
    }
}
