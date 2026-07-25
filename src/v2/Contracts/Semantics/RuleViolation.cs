using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>The shape rules of guarantees.md §6.2.</summary>
    public enum ShapeRule
    {
        /// <summary>Shape completeness, including structural evidence validity.</summary>
        R1,

        /// <summary>Control-lane operations are not ReplayEvidence.</summary>
        R2,

        /// <summary>Continuation commitments resolve to child E2/E4 chains.</summary>
        R3,

        /// <summary>Strict replay compares evidence from all cuts.</summary>
        R4,

        /// <summary>Assertions are closure-free.</summary>
        R5,
    }

    /// <summary>
    /// One rule violation found in an artifact's evidence. Violations are results,
    /// never exceptions: the reader classifies malformed artifacts honestly
    /// (they bar <c>Completed</c>, guarantees.md §6.2/§6.3).
    /// </summary>
    public sealed class RuleViolation : IEquatable<RuleViolation>
    {
        public RuleViolation(ShapeRule rule, string description, RequestId? request = null, OperationId? operation = null)
        {
            Rule = rule;
            Description = ContractGrammar.ValidateIdentifier(description, nameof(description));
            Request = request;
            Operation = operation;
        }

        public ShapeRule Rule { get; }

        public string Description { get; }

        public RequestId? Request { get; }

        public OperationId? Operation { get; }

        public bool Equals(RuleViolation? other) =>
            other != null &&
            Rule == other.Rule &&
            string.Equals(Description, other.Description, StringComparison.Ordinal) &&
            Nullable.Equals(Request, other.Request) &&
            Nullable.Equals(Operation, other.Operation);

        public override bool Equals(object? obj) => Equals(obj as RuleViolation);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes((int)Rule, StringComparer.Ordinal.GetHashCode(Description));
            hash = ContractGrammar.CombineHashes(hash, Request?.GetHashCode() ?? 0);
            return ContractGrammar.CombineHashes(hash, Operation?.GetHashCode() ?? 0);
        }

        public override string ToString() => $"{Rule}: {Description}";
    }
}
