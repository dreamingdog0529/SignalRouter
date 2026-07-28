using System;

namespace SignalRouter.Contracts
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
            CompletionProfileRef completionProfile,
            PredicateDefinition? postcondition = null)
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
            Postcondition = postcondition;
        }

        public CapabilityContractRef Contract { get; }

        public ArgumentSchema Arguments { get; }

        /// <summary>Contract-declared validation precondition; null when the contract declares none.</summary>
        public PredicateDefinition? Precondition { get; }

        public CompletionProfileRef CompletionProfile { get; }

        /// <summary>
        /// Contract-declared postcondition, evaluated during `Observing` against the
        /// pinned after basis; its failure terminates
        /// `Faulted(CompletionPostconditionNotSatisfied)` (verification.md §2.1, §3.4).
        /// </summary>
        public PredicateDefinition? Postcondition { get; }
    }
}
