using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// A capability contract (semantic-model.md §2.2): argument schema with
    /// sensitivity annotations, an optional validation precondition (a declarative
    /// predicate evaluated at `Validating` — disjoint from availability), and the
    /// completion profile binding.
    /// </summary>
    public sealed class CapabilityContractDescriptor
    {
        public CapabilityContractDescriptor(
            CapabilityContractRef contract,
            ArgumentSchema arguments,
            PredicateDefinition? precondition,
            CompletionProfileRef completionProfile)
        {
            if (contract.IsDefault)
            {
                throw new ArgumentException(
                    "Descriptor requires a non-default contract reference.", nameof(contract));
            }

            if (completionProfile.IsDefault)
            {
                throw new ArgumentException(
                    "Descriptor requires a non-default completion profile.", nameof(completionProfile));
            }

            Contract = contract;
            Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
            Precondition = precondition;
            CompletionProfile = completionProfile;
        }

        public CapabilityContractRef Contract { get; }

        public ArgumentSchema Arguments { get; }

        /// <summary>Contract-declared validation precondition; null when the contract declares none.</summary>
        public PredicateDefinition? Precondition { get; }

        public CompletionProfileRef CompletionProfile { get; }
    }
}
