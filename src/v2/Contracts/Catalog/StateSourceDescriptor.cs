using System;
using System.Collections.Generic;

namespace SignalRouter.V2.Contracts
{
    /// <summary>The two state-source classes (observation-state.md §7.1).</summary>
    public enum StateSourceClass
    {
        /// <summary>Published through the kernel; strict-eligible.</summary>
        RevisionBound,

        /// <summary>Read at materialization time; diagnostic only.</summary>
        Sampled,
    }

    /// <summary>One field of a state-source document schema, with its sensitivity.</summary>
    public readonly struct SourceFieldSchema : IEquatable<SourceFieldSchema>
    {
        public SourceFieldSchema(string name, FieldType type, Sensitivity sensitivity)
        {
            Name = ContractGrammar.ValidateIdentifier(name, nameof(name));
            Type = type;
            Sensitivity = sensitivity;
        }

        public string Name { get; }

        public FieldType Type { get; }

        public Sensitivity Sensitivity { get; }

        public bool Equals(SourceFieldSchema other) =>
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            Type == other.Type &&
            Sensitivity == other.Sensitivity;

        public override bool Equals(object? obj) => obj is SourceFieldSchema other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(StringComparer.Ordinal.GetHashCode(Name), (int)Type);
            return ContractGrammar.CombineHashes(hash, (int)Sensitivity);
        }

        public static bool operator ==(SourceFieldSchema left, SourceFieldSchema right) => left.Equals(right);

        public static bool operator !=(SourceFieldSchema left, SourceFieldSchema right) => !left.Equals(right);
    }

    /// <summary>
    /// A state-source contract (semantic-model.md §8, observation-state.md §7):
    /// document schema with per-field sensitivity and the two independent exposure
    /// flags (agent-visible / record-visible).
    /// </summary>
    public sealed class StateSourceContractDescriptor
    {
        public StateSourceContractDescriptor(
            StateSourceContractRef contract,
            ValueList<SourceFieldSchema> fields,
            bool agentVisible,
            bool recordVisible,
            int maxDocumentBytes)
        {
            if (contract.IsDefault)
            {
                throw new ArgumentException(
                    "Descriptor requires a non-default contract reference.", nameof(contract));
            }

            if (fields == null)
            {
                throw new ArgumentNullException(nameof(fields));
            }

            if (maxDocumentBytes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDocumentBytes), "Document byte ceiling must be positive.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (!names.Add(field.Name))
                {
                    throw new ArgumentException("Field names must be unique.", nameof(fields));
                }
            }

            Contract = contract;
            Fields = fields;
            AgentVisible = agentVisible;
            RecordVisible = recordVisible;
            MaxDocumentBytes = maxDocumentBytes;
        }

        public StateSourceContractRef Contract { get; }

        public ValueList<SourceFieldSchema> Fields { get; }

        public bool AgentVisible { get; }

        public bool RecordVisible { get; }

        /// <summary>A contract may lower the profile's document ceiling, never raise it (security-resources.md §5.1).</summary>
        public int MaxDocumentBytes { get; }
    }

    /// <summary>Binds a stable source key to its contract and class (semantic-model.md §8).</summary>
    public sealed class StateSourceRegistration
    {
        public StateSourceRegistration(
            StateSourceKey key,
            StateSourceContractDescriptor descriptor,
            StateSourceClass sourceClass)
        {
            if (key.IsDefault)
            {
                throw new ArgumentException("Registration requires a non-default key.", nameof(key));
            }

            Key = key;
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            SourceClass = sourceClass;
        }

        public StateSourceKey Key { get; }

        public StateSourceContractDescriptor Descriptor { get; }

        public StateSourceClass SourceClass { get; }
    }
}
