using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// One row of E1's immutable <c>CapabilityContractId → CompletionProfileId</c>
    /// binding table (guarantees.md §5.1).
    /// </summary>
    public readonly struct CompletionBinding : IEquatable<CompletionBinding>
    {
        public CompletionBinding(CapabilityContractRef capability, CompletionProfileRef profile)
        {
            if (capability.IsDefault)
            {
                throw new ArgumentException(
                    "CompletionBinding requires a non-default capability reference.", nameof(capability));
            }

            if (profile.IsDefault)
            {
                throw new ArgumentException(
                    "CompletionBinding requires a non-default profile reference.", nameof(profile));
            }

            Capability = capability;
            Profile = profile;
        }

        public CapabilityContractRef Capability { get; }

        public CompletionProfileRef Profile { get; }

        public bool Equals(CompletionBinding other) =>
            Capability.Equals(other.Capability) && Profile.Equals(other.Profile);

        public override bool Equals(object? obj) => obj is CompletionBinding other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(Capability.GetHashCode(), Profile.GetHashCode());

        public override string ToString() => $"{Capability} -> {Profile}";

        public static bool operator ==(CompletionBinding left, CompletionBinding right) => left.Equals(right);

        public static bool operator !=(CompletionBinding left, CompletionBinding right) => !left.Equals(right);
    }

    /// <summary>
    /// One row of E1's immutable registered state-source contract table
    /// (guarantees.md §5.1, observation-state.md §7).
    /// </summary>
    public readonly struct StateSourceBinding : IEquatable<StateSourceBinding>
    {
        public StateSourceBinding(StateSourceKey key, StateSourceContractRef contract)
        {
            if (key.IsDefault)
            {
                throw new ArgumentException(
                    "StateSourceBinding requires a non-default key.", nameof(key));
            }

            if (contract.IsDefault)
            {
                throw new ArgumentException(
                    "StateSourceBinding requires a non-default contract reference.", nameof(contract));
            }

            Key = key;
            Contract = contract;
        }

        public StateSourceKey Key { get; }

        public StateSourceContractRef Contract { get; }

        public bool Equals(StateSourceBinding other) =>
            Key.Equals(other.Key) && Contract.Equals(other.Contract);

        public override bool Equals(object? obj) => obj is StateSourceBinding other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(Key.GetHashCode(), Contract.GetHashCode());

        public override string ToString() => $"{Key} -> {Contract}";

        public static bool operator ==(StateSourceBinding left, StateSourceBinding right) => left.Equals(right);

        public static bool operator !=(StateSourceBinding left, StateSourceBinding right) => !left.Equals(right);
    }
}
