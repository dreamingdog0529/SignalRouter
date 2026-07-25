using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// Monotonic revision of the observation store — the node store plus all
    /// revision-bound state-source documents — within an incarnation
    /// (semantic-model.md §4). Also plays the <c>ViewWatermark</c> role: the highest
    /// revision a view materialization or subscription has fully applied
    /// (observation-state.md §4).
    /// </summary>
    public readonly struct SourceRevision : IEquatable<SourceRevision>, IComparable<SourceRevision>
    {
        public SourceRevision(ulong value)
        {
            Value = value;
        }

        public ulong Value { get; }

        public int CompareTo(SourceRevision other) => Value.CompareTo(other.Value);

        public bool Equals(SourceRevision other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is SourceRevision other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString();

        public static bool operator ==(SourceRevision left, SourceRevision right) => left.Equals(right);

        public static bool operator !=(SourceRevision left, SourceRevision right) => !left.Equals(right);

        public static bool operator <(SourceRevision left, SourceRevision right) => left.Value < right.Value;

        public static bool operator >(SourceRevision left, SourceRevision right) => left.Value > right.Value;

        public static bool operator <=(SourceRevision left, SourceRevision right) => left.Value <= right.Value;

        public static bool operator >=(SourceRevision left, SourceRevision right) => left.Value >= right.Value;
    }

    /// <summary>
    /// Total admission order of mutation interactions within an incarnation — the
    /// serializable-tier linearization point (guarantees.md §4, semantic-model.md §4).
    /// </summary>
    public readonly struct LogicalOrder : IEquatable<LogicalOrder>, IComparable<LogicalOrder>
    {
        public LogicalOrder(ulong value)
        {
            Value = value;
        }

        public ulong Value { get; }

        public int CompareTo(LogicalOrder other) => Value.CompareTo(other.Value);

        public bool Equals(LogicalOrder other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is LogicalOrder other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString();

        public static bool operator ==(LogicalOrder left, LogicalOrder right) => left.Equals(right);

        public static bool operator !=(LogicalOrder left, LogicalOrder right) => !left.Equals(right);

        public static bool operator <(LogicalOrder left, LogicalOrder right) => left.Value < right.Value;

        public static bool operator >(LogicalOrder left, LogicalOrder right) => left.Value > right.Value;

        public static bool operator <=(LogicalOrder left, LogicalOrder right) => left.Value <= right.Value;

        public static bool operator >=(LogicalOrder left, LogicalOrder right) => left.Value >= right.Value;
    }

    /// <summary>Delta ordering within one view subscription (semantic-model.md §4).</summary>
    public readonly struct ViewSequence : IEquatable<ViewSequence>, IComparable<ViewSequence>
    {
        public ViewSequence(ulong value)
        {
            Value = value;
        }

        public ulong Value { get; }

        public int CompareTo(ViewSequence other) => Value.CompareTo(other.Value);

        public bool Equals(ViewSequence other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is ViewSequence other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString();

        public static bool operator ==(ViewSequence left, ViewSequence right) => left.Equals(right);

        public static bool operator !=(ViewSequence left, ViewSequence right) => !left.Equals(right);

        public static bool operator <(ViewSequence left, ViewSequence right) => left.Value < right.Value;

        public static bool operator >(ViewSequence left, ViewSequence right) => left.Value > right.Value;

        public static bool operator <=(ViewSequence left, ViewSequence right) => left.Value <= right.Value;

        public static bool operator >=(ViewSequence left, ViewSequence right) => left.Value >= right.Value;
    }

    /// <summary>
    /// Monotonic append position of a ReplayEvidence cut within one recording
    /// artifact (semantic-model.md §4, recording-replay.md §2). Non-interaction cuts
    /// are positioned by this alone; interaction cuts additionally carry their
    /// interaction's <see cref="LogicalOrder"/>.
    /// </summary>
    public readonly struct EvidenceSequence : IEquatable<EvidenceSequence>, IComparable<EvidenceSequence>
    {
        public EvidenceSequence(ulong value)
        {
            Value = value;
        }

        public ulong Value { get; }

        public int CompareTo(EvidenceSequence other) => Value.CompareTo(other.Value);

        public bool Equals(EvidenceSequence other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is EvidenceSequence other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString();

        public static bool operator ==(EvidenceSequence left, EvidenceSequence right) => left.Equals(right);

        public static bool operator !=(EvidenceSequence left, EvidenceSequence right) => !left.Equals(right);

        public static bool operator <(EvidenceSequence left, EvidenceSequence right) => left.Value < right.Value;

        public static bool operator >(EvidenceSequence left, EvidenceSequence right) => left.Value > right.Value;

        public static bool operator <=(EvidenceSequence left, EvidenceSequence right) => left.Value <= right.Value;

        public static bool operator >=(EvidenceSequence left, EvidenceSequence right) => left.Value >= right.Value;
    }
}
