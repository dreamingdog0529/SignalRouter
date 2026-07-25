using System;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.AdapterSdk
{
    /// <summary>
    /// A value from the adapter's declared, open frame-phase vocabulary
    /// (kernel-execution.md §6, adapter-conformance.md §4).
    /// </summary>
    public readonly struct FramePhase : IEquatable<FramePhase>
    {
        private readonly string? value;

        public FramePhase(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        public static FramePhase Update => new FramePhase("Update");

        public static FramePhase LateUpdate => new FramePhase("LateUpdate");

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default FramePhase carries no value.");

        public bool Equals(FramePhase other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is FramePhase other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(FramePhase left, FramePhase right) => left.Equals(right);

        public static bool operator !=(FramePhase left, FramePhase right) => !left.Equals(right);
    }

    /// <summary>
    /// The host-supplied logical clock reading (ADR 0010): semantic time — wait
    /// timeouts, retention expiry — advances only with this value, resolving at pump
    /// boundaries. Units are host-defined but consistent within an incarnation.
    /// </summary>
    public readonly struct LogicalTime : IEquatable<LogicalTime>, IComparable<LogicalTime>
    {
        public LogicalTime(long value)
        {
            Value = value;
        }

        public long Value { get; }

        public int CompareTo(LogicalTime other) => Value.CompareTo(other.Value);

        public bool Equals(LogicalTime other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is LogicalTime other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString();

        public static bool operator ==(LogicalTime left, LogicalTime right) => left.Equals(right);

        public static bool operator !=(LogicalTime left, LogicalTime right) => !left.Equals(right);

        public static bool operator <(LogicalTime left, LogicalTime right) => left.Value < right.Value;

        public static bool operator >(LogicalTime left, LogicalTime right) => left.Value > right.Value;

        public static bool operator <=(LogicalTime left, LogicalTime right) => left.Value <= right.Value;

        public static bool operator >=(LogicalTime left, LogicalTime right) => left.Value >= right.Value;
    }

    /// <summary>
    /// The host-injected monotonic clock the kernel reads at step boundaries to
    /// enforce the pump deadline (ADR 0010). The kernel never reads a system clock;
    /// a reading that moves backwards is a fail-fast kernel fault.
    /// </summary>
    public interface IMonotonicClock
    {
        /// <summary>The current monotonic reading, in the same units as pump deadlines.</summary>
        long Now { get; }
    }

    /// <summary>One pump invocation's inputs (kernel-execution.md §6).</summary>
    public readonly struct PumpBudget
    {
        public PumpBudget(int maxTurns, long deadline, LogicalTime logicalNow, FramePhase framePhase)
        {
            if (maxTurns < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxTurns), "A pump processes at least one turn.");
            }

            if (framePhase.IsDefault)
            {
                throw new ArgumentException("A pump requires a frame phase.", nameof(framePhase));
            }

            MaxTurns = maxTurns;
            Deadline = deadline;
            LogicalNow = logicalNow;
            FramePhase = framePhase;
        }

        public int MaxTurns { get; }

        /// <summary>Absolute deadline in the injected monotonic clock's units.</summary>
        public long Deadline { get; }

        public LogicalTime LogicalNow { get; }

        public FramePhase FramePhase { get; }
    }

    /// <summary>
    /// The truthful pump answer (kernel-execution.md §6): what ran, what remains,
    /// and what the kernel is waiting on — so hosts can schedule further pumps and
    /// bound their drive-until-quiescent loops.
    /// </summary>
    public sealed class PumpReport
    {
        public PumpReport(
            int turnsExecuted,
            bool workRemaining,
            int controlQueueDepth,
            int sourcePublicationQueueDepth,
            int mutationQueueDepth,
            bool awaitingAdapterCompletion,
            FramePhase? awaitingFramePhase)
        {
            if (turnsExecuted < 0 || controlQueueDepth < 0 ||
                sourcePublicationQueueDepth < 0 || mutationQueueDepth < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(turnsExecuted), "Report counts must not be negative.");
            }

            if (awaitingFramePhase.HasValue && awaitingFramePhase.Value.IsDefault)
            {
                throw new ArgumentException("A present awaited phase must be non-default.", nameof(awaitingFramePhase));
            }

            TurnsExecuted = turnsExecuted;
            WorkRemaining = workRemaining;
            ControlQueueDepth = controlQueueDepth;
            SourcePublicationQueueDepth = sourcePublicationQueueDepth;
            MutationQueueDepth = mutationQueueDepth;
            AwaitingAdapterCompletion = awaitingAdapterCompletion;
            AwaitingFramePhase = awaitingFramePhase;
        }

        public int TurnsExecuted { get; }

        /// <summary>Whether immediately processable work remains (not counting awaited completions/phases).</summary>
        public bool WorkRemaining { get; }

        public int ControlQueueDepth { get; }

        public int SourcePublicationQueueDepth { get; }

        public int MutationQueueDepth { get; }

        public bool AwaitingAdapterCompletion { get; }

        /// <summary>The declared frame phase progress waits on, when one is required (FrameCommitted fencing).</summary>
        public FramePhase? AwaitingFramePhase { get; }
    }
}
