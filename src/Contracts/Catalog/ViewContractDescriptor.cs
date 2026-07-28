using System;

namespace SignalRouter.Contracts
{
    /// <summary>The view family a contract serves (observation-state.md §1).</summary>
    public enum ViewFamily
    {
        Agent,
        Record,
    }

    /// <summary>
    /// One registered view contract (observation-state.md §1, ADR 0011): family,
    /// scope, materialization bounds, and keyless-node inclusion. Registration is
    /// bootstrap-only, so a view contract can never change while a recording is
    /// active. The `ViewContractId@version` identifies exactly this descriptor plus
    /// the fixed ordinal normalization — richer projection rules are a new version,
    /// never a reinterpretation.
    /// </summary>
    public sealed class ViewContractDescriptor
    {
        public ViewContractDescriptor(
            ViewContractRef contract,
            ViewFamily family,
            string scope,
            int maxNodes,
            int maxFieldBytes,
            bool includeKeylessNodes)
        {
            if (contract.IsDefault)
            {
                throw new ArgumentException(
                    "A view contract descriptor requires a non-default contract.", nameof(contract));
            }

            if (maxNodes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxNodes), "MaxNodes is at least one.");
            }

            if (maxFieldBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFieldBytes), "MaxFieldBytes is at least one.");
            }

            if (family == ViewFamily.Record && includeKeylessNodes)
            {
                throw new ArgumentException(
                    "A Record-family view MUST exclude keyless nodes (semantic-model.md 3.2).",
                    nameof(includeKeylessNodes));
            }

            Contract = contract;
            Family = family;
            Scope = ContractGrammar.ValidateIdentifier(scope, nameof(scope));
            MaxNodes = maxNodes;
            MaxFieldBytes = maxFieldBytes;
            IncludeKeylessNodes = includeKeylessNodes;
        }

        public ViewContractRef Contract { get; }

        public ViewFamily Family { get; }

        /// <summary>The subtree root's AuthorKey, or the reserved identifier <c>root</c> for the whole tree.</summary>
        public string Scope { get; }

        public int MaxNodes { get; }

        /// <summary>Per-field ceiling in UTF-16 code units; oversized values surface as completeness.</summary>
        public int MaxFieldBytes { get; }

        /// <summary>Keyless nodes contribute to visible child counts when included; they are never path-addressable.</summary>
        public bool IncludeKeylessNodes { get; }

        /// <summary>The whole-tree scope identifier.</summary>
        public const string RootScope = "root";
    }
}
