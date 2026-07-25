using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>The form a capability invocation targets a node in (semantic-model.md §2.2).</summary>
    public enum TargetReferenceKind
    {
        /// <summary>The runtime form: a <see cref="NodeRef"/> (semantic-model.md §3.1).</summary>
        NodeRef,

        /// <summary>The persistent form: an <see cref="AuthorKey"/> (semantic-model.md §3.2).</summary>
        AuthorKey,
    }

    /// <summary>
    /// The target reference of a capability invocation: either a runtime
    /// <see cref="NodeRef"/> or a persistent <see cref="AuthorKey"/>; admission
    /// resolves it to exactly one node (semantic-model.md §2.2).
    /// </summary>
    public readonly struct TargetReference : IEquatable<TargetReference>
    {
        private readonly NodeRef node;
        private readonly AuthorKey key;
        private readonly bool initialized;

        private TargetReference(TargetReferenceKind kind, NodeRef node, AuthorKey key)
        {
            Kind = kind;
            this.node = node;
            this.key = key;
            initialized = true;
        }

        public TargetReferenceKind Kind { get; }

        public bool IsDefault => !initialized;

        public static TargetReference ForNode(NodeRef node)
        {
            if (node.IsDefault)
            {
                throw new ArgumentException(
                    "Target reference requires a non-default NodeRef.", nameof(node));
            }

            return new TargetReference(TargetReferenceKind.NodeRef, node, default);
        }

        public static TargetReference ForKey(AuthorKey key)
        {
            if (key.IsDefault)
            {
                throw new ArgumentException(
                    "Target reference requires a non-default AuthorKey.", nameof(key));
            }

            return new TargetReference(TargetReferenceKind.AuthorKey, default, key);
        }

        public NodeRef Node =>
            initialized && Kind == TargetReferenceKind.NodeRef
                ? node
                : throw new InvalidOperationException("This target reference does not carry a NodeRef.");

        public AuthorKey Key =>
            initialized && Kind == TargetReferenceKind.AuthorKey
                ? key
                : throw new InvalidOperationException("This target reference does not carry an AuthorKey.");

        public bool Equals(TargetReference other) =>
            initialized == other.initialized &&
            Kind == other.Kind &&
            node.Equals(other.node) &&
            key.Equals(other.key);

        public override bool Equals(object? obj) => obj is TargetReference other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes((int)Kind, node.GetHashCode());
            return ContractGrammar.CombineHashes(hash, key.GetHashCode());
        }

        public override string ToString()
        {
            if (!initialized)
            {
                return "(default)";
            }

            return Kind == TargetReferenceKind.NodeRef ? $"node:{node}" : $"key:{key}";
        }

        public static bool operator ==(TargetReference left, TargetReference right) => left.Equals(right);

        public static bool operator !=(TargetReference left, TargetReference right) => !left.Equals(right);
    }
}
