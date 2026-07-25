using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The v2 successor of the v1 command — pure data:
    /// <c>(CapabilityContractId@version, target reference, typed arguments)</c>
    /// (semantic-model.md §2.2). Arguments appear here only as their redacted digest;
    /// typed argument schemas belong to capability-contract modeling in a later
    /// module. No callbacks, engine objects, transport metadata, timestamps, or
    /// identity envelope — those live in the admission envelope.
    /// </summary>
    public sealed class CapabilityInvocation : IEquatable<CapabilityInvocation>
    {
        public CapabilityInvocation(CapabilityContractRef contract, TargetReference target, ArgumentDigest arguments)
        {
            if (contract.IsDefault)
            {
                throw new ArgumentException(
                    "CapabilityInvocation requires a non-default contract reference.", nameof(contract));
            }

            if (target.IsDefault)
            {
                throw new ArgumentException(
                    "CapabilityInvocation requires a non-default target reference.", nameof(target));
            }

            if (arguments.IsDefault)
            {
                throw new ArgumentException(
                    "CapabilityInvocation requires a non-default argument digest.", nameof(arguments));
            }

            Contract = contract;
            Target = target;
            Arguments = arguments;
        }

        public CapabilityContractRef Contract { get; }

        public TargetReference Target { get; }

        public ArgumentDigest Arguments { get; }

        public bool Equals(CapabilityInvocation? other) =>
            other != null &&
            Contract.Equals(other.Contract) &&
            Target.Equals(other.Target) &&
            Arguments.Equals(other.Arguments);

        public override bool Equals(object? obj) => Equals(obj as CapabilityInvocation);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(Contract.GetHashCode(), Target.GetHashCode());
            return ContractGrammar.CombineHashes(hash, Arguments.GetHashCode());
        }

        public override string ToString() => $"{Contract} -> {Target}";
    }
}
