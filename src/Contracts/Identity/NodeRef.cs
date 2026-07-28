using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// An opaque runtime handle minted by the runtime, unique within one
    /// <see cref="RuntimeIncarnationId"/> and meaningless outside it. Never persisted
    /// into recording artifacts and never reused within an incarnation
    /// (semantic-model.md §3.1).
    /// </summary>
    public readonly struct NodeRef : IEquatable<NodeRef>
    {
        public NodeRef(RuntimeIncarnationId incarnation, ulong value)
        {
            if (incarnation.IsDefault)
            {
                throw new ArgumentException(
                    "NodeRef requires a non-default incarnation.", nameof(incarnation));
            }

            Incarnation = incarnation;
            Value = value;
        }

        public RuntimeIncarnationId Incarnation { get; }

        public ulong Value { get; }

        public bool IsDefault => Incarnation.IsDefault;

        public bool Equals(NodeRef other) =>
            Incarnation.Equals(other.Incarnation) && Value == other.Value;

        public override bool Equals(object? obj) => obj is NodeRef other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(Incarnation.GetHashCode(), Value.GetHashCode());

        public override string ToString() =>
            IsDefault ? "(default)" : $"{Incarnation}/{Value}";

        public static bool operator ==(NodeRef left, NodeRef right) => left.Equals(right);

        public static bool operator !=(NodeRef left, NodeRef right) => !left.Equals(right);
    }
}
