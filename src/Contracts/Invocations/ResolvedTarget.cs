using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// The node identity admission resolved a target reference to. A node without an
    /// <see cref="Contracts.AuthorKey"/> is keyless: it cannot enter strict-replay
    /// scope (semantic-model.md §3.2, guarantees.md §5.2).
    /// </summary>
    public readonly struct ResolvedTarget : IEquatable<ResolvedTarget>
    {
        private readonly AuthorKey? authorKey;

        public ResolvedTarget(NodeRef node, AuthorKey? authorKey)
        {
            if (node.IsDefault)
            {
                throw new ArgumentException(
                    "ResolvedTarget requires a non-default NodeRef.", nameof(node));
            }

            if (authorKey.HasValue && authorKey.Value.IsDefault)
            {
                throw new ArgumentException(
                    "A present AuthorKey must be non-default.", nameof(authorKey));
            }

            Node = node;
            this.authorKey = authorKey;
        }

        public NodeRef Node { get; }

        /// <summary>Null when the resolved node is keyless.</summary>
        public AuthorKey? AuthorKey => authorKey;

        public bool HasAuthorKey => authorKey.HasValue;

        public bool IsDefault => Node.IsDefault;

        public bool Equals(ResolvedTarget other) =>
            Node.Equals(other.Node) && Nullable.Equals(authorKey, other.authorKey);

        public override bool Equals(object? obj) => obj is ResolvedTarget other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(Node.GetHashCode(), authorKey?.GetHashCode() ?? 0);

        public override string ToString() =>
            IsDefault ? "(default)" : HasAuthorKey ? $"{Node}({authorKey})" : $"{Node}(keyless)";

        public static bool operator ==(ResolvedTarget left, ResolvedTarget right) => left.Equals(right);

        public static bool operator !=(ResolvedTarget left, ResolvedTarget right) => !left.Equals(right);
    }
}
