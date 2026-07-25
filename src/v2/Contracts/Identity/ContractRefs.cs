using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>A versioned reference to a capability contract (<c>CapabilityContractId@version</c>).</summary>
    public readonly struct CapabilityContractRef : IEquatable<CapabilityContractRef>
    {
        public CapabilityContractRef(CapabilityContractId id, ContractVersion version)
        {
            if (id.IsDefault)
            {
                throw new ArgumentException("Contract reference requires a non-default id.", nameof(id));
            }

            Id = id;
            Version = version;
        }

        public CapabilityContractId Id { get; }

        public ContractVersion Version { get; }

        public bool IsDefault => Id.IsDefault;

        public bool Equals(CapabilityContractRef other) => Id.Equals(other.Id) && Version.Equals(other.Version);

        public override bool Equals(object? obj) => obj is CapabilityContractRef other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes(Id.GetHashCode(), Version.GetHashCode());

        public override string ToString() => IsDefault ? "(default)" : $"{Id}@{Version}";

        public static bool operator ==(CapabilityContractRef left, CapabilityContractRef right) => left.Equals(right);

        public static bool operator !=(CapabilityContractRef left, CapabilityContractRef right) => !left.Equals(right);
    }

    /// <summary>A versioned reference to a completion profile (<c>CompletionProfileId@version</c>).</summary>
    public readonly struct CompletionProfileRef : IEquatable<CompletionProfileRef>
    {
        public CompletionProfileRef(CompletionProfileId id, ContractVersion version)
        {
            if (id.IsDefault)
            {
                throw new ArgumentException("Contract reference requires a non-default id.", nameof(id));
            }

            Id = id;
            Version = version;
        }

        public CompletionProfileId Id { get; }

        public ContractVersion Version { get; }

        public bool IsDefault => Id.IsDefault;

        public bool Equals(CompletionProfileRef other) => Id.Equals(other.Id) && Version.Equals(other.Version);

        public override bool Equals(object? obj) => obj is CompletionProfileRef other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes(Id.GetHashCode(), Version.GetHashCode());

        public override string ToString() => IsDefault ? "(default)" : $"{Id}@{Version}";

        public static bool operator ==(CompletionProfileRef left, CompletionProfileRef right) => left.Equals(right);

        public static bool operator !=(CompletionProfileRef left, CompletionProfileRef right) => !left.Equals(right);
    }

    /// <summary>A versioned reference to a view contract (<c>ViewContractId@version</c>).</summary>
    public readonly struct ViewContractRef : IEquatable<ViewContractRef>
    {
        public ViewContractRef(ViewContractId id, ContractVersion version)
        {
            if (id.IsDefault)
            {
                throw new ArgumentException("Contract reference requires a non-default id.", nameof(id));
            }

            Id = id;
            Version = version;
        }

        public ViewContractId Id { get; }

        public ContractVersion Version { get; }

        public bool IsDefault => Id.IsDefault;

        public bool Equals(ViewContractRef other) => Id.Equals(other.Id) && Version.Equals(other.Version);

        public override bool Equals(object? obj) => obj is ViewContractRef other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes(Id.GetHashCode(), Version.GetHashCode());

        public override string ToString() => IsDefault ? "(default)" : $"{Id}@{Version}";

        public static bool operator ==(ViewContractRef left, ViewContractRef right) => left.Equals(right);

        public static bool operator !=(ViewContractRef left, ViewContractRef right) => !left.Equals(right);
    }

    /// <summary>A versioned reference to a state-source contract (<c>StateSourceContractId@version</c>).</summary>
    public readonly struct StateSourceContractRef : IEquatable<StateSourceContractRef>
    {
        public StateSourceContractRef(StateSourceContractId id, ContractVersion version)
        {
            if (id.IsDefault)
            {
                throw new ArgumentException("Contract reference requires a non-default id.", nameof(id));
            }

            Id = id;
            Version = version;
        }

        public StateSourceContractId Id { get; }

        public ContractVersion Version { get; }

        public bool IsDefault => Id.IsDefault;

        public bool Equals(StateSourceContractRef other) => Id.Equals(other.Id) && Version.Equals(other.Version);

        public override bool Equals(object? obj) => obj is StateSourceContractRef other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes(Id.GetHashCode(), Version.GetHashCode());

        public override string ToString() => IsDefault ? "(default)" : $"{Id}@{Version}";

        public static bool operator ==(StateSourceContractRef left, StateSourceContractRef right) => left.Equals(right);

        public static bool operator !=(StateSourceContractRef left, StateSourceContractRef right) => !left.Equals(right);
    }

    /// <summary>A versioned reference to a predicate contract (<c>PredicateContractId@version</c>).</summary>
    public readonly struct PredicateContractRef : IEquatable<PredicateContractRef>
    {
        public PredicateContractRef(PredicateContractId id, ContractVersion version)
        {
            if (id.IsDefault)
            {
                throw new ArgumentException("Contract reference requires a non-default id.", nameof(id));
            }

            Id = id;
            Version = version;
        }

        public PredicateContractId Id { get; }

        public ContractVersion Version { get; }

        public bool IsDefault => Id.IsDefault;

        public bool Equals(PredicateContractRef other) => Id.Equals(other.Id) && Version.Equals(other.Version);

        public override bool Equals(object? obj) => obj is PredicateContractRef other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes(Id.GetHashCode(), Version.GetHashCode());

        public override string ToString() => IsDefault ? "(default)" : $"{Id}@{Version}";

        public static bool operator ==(PredicateContractRef left, PredicateContractRef right) => left.Equals(right);

        public static bool operator !=(PredicateContractRef left, PredicateContractRef right) => !left.Equals(right);
    }

    /// <summary>A versioned reference to a replay comparison profile (<c>ReplayComparisonProfileId@version</c>).</summary>
    public readonly struct ReplayComparisonProfileRef : IEquatable<ReplayComparisonProfileRef>
    {
        public ReplayComparisonProfileRef(ReplayComparisonProfileId id, ContractVersion version)
        {
            if (id.IsDefault)
            {
                throw new ArgumentException("Contract reference requires a non-default id.", nameof(id));
            }

            Id = id;
            Version = version;
        }

        public ReplayComparisonProfileId Id { get; }

        public ContractVersion Version { get; }

        public bool IsDefault => Id.IsDefault;

        public bool Equals(ReplayComparisonProfileRef other) => Id.Equals(other.Id) && Version.Equals(other.Version);

        public override bool Equals(object? obj) => obj is ReplayComparisonProfileRef other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes(Id.GetHashCode(), Version.GetHashCode());

        public override string ToString() => IsDefault ? "(default)" : $"{Id}@{Version}";

        public static bool operator ==(ReplayComparisonProfileRef left, ReplayComparisonProfileRef right) => left.Equals(right);

        public static bool operator !=(ReplayComparisonProfileRef left, ReplayComparisonProfileRef right) => !left.Equals(right);
    }
}
