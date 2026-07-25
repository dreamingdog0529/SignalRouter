using System;
using System.Collections.Generic;

namespace SignalRouter.V2.Contracts
{
    /// <summary>A node's descriptive role — an open, versioned vocabulary (semantic-model.md §2.1). Roles carry no operational authority.</summary>
    public readonly struct NodeRole : IEquatable<NodeRole>
    {
        private readonly string? value;

        public NodeRole(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        public static NodeRole Button => new NodeRole("button");

        public static NodeRole Textbox => new NodeRole("textbox");

        public static NodeRole List => new NodeRole("list");

        public static NodeRole ListItem => new NodeRole("listitem");

        public static NodeRole Container => new NodeRole("container");

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default NodeRole carries no value.");

        public bool Equals(NodeRole other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is NodeRole other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(NodeRole left, NodeRole right) => left.Equals(right);

        public static bool operator !=(NodeRole left, NodeRole right) => !left.Equals(right);
    }

    /// <summary>One observable node attribute with its sensitivity (semantic-model.md §1, §7).</summary>
    public readonly struct NodeAttribute : IEquatable<NodeAttribute>
    {
        public NodeAttribute(string name, FieldValue value, Sensitivity sensitivity)
        {
            Name = ContractGrammar.ValidateIdentifier(name, nameof(name));
            if (value.IsDefault)
            {
                throw new ArgumentException("Attribute requires a non-default value.", nameof(value));
            }

            Value = value;
            Sensitivity = sensitivity;
        }

        public string Name { get; }

        public FieldValue Value { get; }

        public Sensitivity Sensitivity { get; }

        public bool Equals(NodeAttribute other) =>
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            Value.Equals(other.Value) &&
            Sensitivity == other.Sensitivity;

        public override bool Equals(object? obj) => obj is NodeAttribute other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(Name), Value.GetHashCode());
            return ContractGrammar.CombineHashes(hash, (int)Sensitivity);
        }

        public static bool operator ==(NodeAttribute left, NodeAttribute right) => left.Equals(right);

        public static bool operator !=(NodeAttribute left, NodeAttribute right) => !left.Equals(right);
    }

    /// <summary>One declared capability on a node with its availability state (semantic-model.md §2.2).</summary>
    public readonly struct CapabilityDeclaration : IEquatable<CapabilityDeclaration>
    {
        public CapabilityDeclaration(CapabilityContractRef contract, bool initiallyAvailable)
        {
            if (contract.IsDefault)
            {
                throw new ArgumentException(
                    "Declaration requires a non-default contract reference.", nameof(contract));
            }

            Contract = contract;
            InitiallyAvailable = initiallyAvailable;
        }

        public CapabilityContractRef Contract { get; }

        public bool InitiallyAvailable { get; }

        public bool Equals(CapabilityDeclaration other) =>
            Contract.Equals(other.Contract) && InitiallyAvailable == other.InitiallyAvailable;

        public override bool Equals(object? obj) => obj is CapabilityDeclaration other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(Contract.GetHashCode(), InitiallyAvailable ? 1 : 0);

        public static bool operator ==(CapabilityDeclaration left, CapabilityDeclaration right) => left.Equals(right);

        public static bool operator !=(CapabilityDeclaration left, CapabilityDeclaration right) => !left.Equals(right);
    }

    /// <summary>
    /// A node registration (semantic-model.md §1, §3): optional persistent
    /// `AuthorKey`, role, optional parent (by author key), attributes, declared
    /// capabilities, and the exposure policy. Attribute names and capability
    /// contracts are unique.
    /// </summary>
    public sealed class NodeRegistration
    {
        public NodeRegistration(
            AuthorKey? authorKey,
            NodeRole role,
            AuthorKey? parent,
            ValueList<NodeAttribute> attributes,
            ValueList<CapabilityDeclaration> capabilities,
            ExposurePolicy exposure)
        {
            if (authorKey.HasValue && authorKey.Value.IsDefault)
            {
                throw new ArgumentException("A present AuthorKey must be non-default.", nameof(authorKey));
            }

            if (role.IsDefault)
            {
                throw new ArgumentException("Registration requires a non-default role.", nameof(role));
            }

            if (parent.HasValue && parent.Value.IsDefault)
            {
                throw new ArgumentException("A present parent key must be non-default.", nameof(parent));
            }

            if (attributes == null)
            {
                throw new ArgumentNullException(nameof(attributes));
            }

            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            var attributeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attribute in attributes)
            {
                if (!attributeNames.Add(attribute.Name))
                {
                    throw new ArgumentException("Attribute names must be unique.", nameof(attributes));
                }
            }

            var contracts = new HashSet<CapabilityContractRef>();
            foreach (var capability in capabilities)
            {
                if (!contracts.Add(capability.Contract))
                {
                    throw new ArgumentException(
                        "Capability contracts must be unique per node.", nameof(capabilities));
                }
            }

            AuthorKey = authorKey;
            Role = role;
            Parent = parent;
            Attributes = attributes;
            Capabilities = capabilities;
            Exposure = exposure ?? throw new ArgumentNullException(nameof(exposure));
        }

        /// <summary>Null when the node is keyless (excluded from strict-replay scope, semantic-model.md §3.2).</summary>
        public AuthorKey? AuthorKey { get; }

        public NodeRole Role { get; }

        public AuthorKey? Parent { get; }

        public ValueList<NodeAttribute> Attributes { get; }

        public ValueList<CapabilityDeclaration> Capabilities { get; }

        public ExposurePolicy Exposure { get; }
    }
}
