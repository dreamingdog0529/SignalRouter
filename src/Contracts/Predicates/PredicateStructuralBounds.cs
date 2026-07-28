using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// The structural bounds enforced at predicate validation and re-checked by the
    /// evaluator's step counter (verification.md §2.2,
    /// security-resources.md §5.1). <see cref="Default"/> carries the `default@1`
    /// resource-profile values.
    /// </summary>
    public readonly struct PredicateStructuralBounds : IEquatable<PredicateStructuralBounds>
    {
        public PredicateStructuralBounds(
            int maxDepth,
            int maxNodeCount,
            int maxOperandLength,
            int maxBatchSize,
            int maxEvaluationSteps)
        {
            if (maxDepth < 1 || maxNodeCount < 1 || maxOperandLength < 1 ||
                maxBatchSize < 1 || maxEvaluationSteps < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDepth), "Structural bounds must be positive.");
            }

            MaxDepth = maxDepth;
            MaxNodeCount = maxNodeCount;
            MaxOperandLength = maxOperandLength;
            MaxBatchSize = maxBatchSize;
            MaxEvaluationSteps = maxEvaluationSteps;
        }

        /// <summary>The `default@1` resource profile (security-resources.md §5.1).</summary>
        public static PredicateStructuralBounds Default =>
            new PredicateStructuralBounds(16, 256, 4096, 32, 65536);

        public int MaxDepth { get; }

        public int MaxNodeCount { get; }

        /// <summary>Maximum operand length in UTF-16 code units.</summary>
        public int MaxOperandLength { get; }

        public int MaxBatchSize { get; }

        public int MaxEvaluationSteps { get; }

        public bool IsDefault => MaxDepth == 0;

        public bool Equals(PredicateStructuralBounds other) =>
            MaxDepth == other.MaxDepth &&
            MaxNodeCount == other.MaxNodeCount &&
            MaxOperandLength == other.MaxOperandLength &&
            MaxBatchSize == other.MaxBatchSize &&
            MaxEvaluationSteps == other.MaxEvaluationSteps;

        public override bool Equals(object? obj) => obj is PredicateStructuralBounds other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(MaxDepth, MaxNodeCount);
            hash = ContractGrammar.CombineHashes(hash, MaxOperandLength);
            hash = ContractGrammar.CombineHashes(hash, MaxBatchSize);
            return ContractGrammar.CombineHashes(hash, MaxEvaluationSteps);
        }

        public static bool operator ==(PredicateStructuralBounds left, PredicateStructuralBounds right) => left.Equals(right);

        public static bool operator !=(PredicateStructuralBounds left, PredicateStructuralBounds right) => !left.Equals(right);
    }
}
