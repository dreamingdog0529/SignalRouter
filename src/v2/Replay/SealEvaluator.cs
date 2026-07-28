using System;
using SignalRouter.V2.Codec.Recording;
using SignalRouter.V2.Comparison;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Replay
{
    /// <summary>The seal verdict: sealable, or exactly which §5.2 condition failed.</summary>
    public sealed class SealEvaluation
    {
        private SealEvaluation(string? failedCondition)
        {
            FailedCondition = failedCondition;
        }

        public bool IsSealable => FailedCondition == null;

        /// <summary>Null when sealable; otherwise one of the <see cref="SealConditions"/> codes.</summary>
        public string? FailedCondition { get; }

        internal static SealEvaluation Sealable { get; } = new SealEvaluation(null);

        internal static SealEvaluation Failed(string condition) =>
            new SealEvaluation(ContractGrammar.ValidateCode(condition, nameof(condition)));
    }

    /// <summary>The verification.md §5.2 condition codes a failed seal reports.</summary>
    public static class SealConditions
    {
        /// <summary>Condition 1: the artifact is not Completed with reader-verified closure.</summary>
        public const string NotCompleted = "NotCompleted";

        /// <summary>Conditions 1–2: the trust boundary refused the artifact outright.</summary>
        public const string ArtifactRefused = "ArtifactRefused";

        /// <summary>Condition 3: strict replay would stop at or answer Incomparable for some cut.</summary>
        public const string StrictIneligible = "StrictIneligible";

        /// <summary>Condition 4: a required assertion is missing or did not evaluate Satisfied.</summary>
        public const string RequiredAssertionNotSatisfied = "RequiredAssertionNotSatisfied";

        /// <summary>Condition 5: a contract pinned in E1 is unavailable at a compatible version.</summary>
        public const string ContractPreflight = "ContractPreflight";
    }

    /// <summary>
    /// The verification.md §5.2 seal evaluator: an artifact may be sealed into a
    /// CI verification case only when all five conditions hold — a case known
    /// in advance to be unreplayable is not a test. An artifact that fails
    /// sealing remains a diagnostic recording; the answer names which condition
    /// failed. v2.0 artifacts are self-contained by construction (condition 2
    /// is the reader's blob-closure verification), and the full replay pre-scan
    /// is the strict-eligibility authority (condition 3).
    /// </summary>
    public static class SealEvaluator
    {
        public static SealEvaluation Evaluate(
            byte[] artifact,
            ArtifactReadLimits limits,
            ReplayAllowlist allowlist,
            ComparisonVocabulary vocabulary,
            ReplayTrustOptions trust,
            ValueArray<PredicateContractRef> requiredAssertions)
        {
            if (artifact == null)
            {
                throw new ArgumentNullException(nameof(artifact));
            }

            // A sealed case resolves its secrets at run time, so secret
            // resolvability is not a seal condition: the scan treats every
            // reference as resolvable so a secret stop cannot mask a real
            // strict-eligibility stop further along the stream.
            var scan = ReplayPreScan.Scan(
                artifact, limits, allowlist, vocabulary, SecretsResolveLater.Instance, trust);
            if (scan.Refusal != null)
            {
                // Condition 5 has its own name; every other refusal is the
                // trust boundary rejecting the artifact as input.
                return SealEvaluation.Failed(
                    scan.Refusal.Code == ReplayRefusalCodes.ContractAllowlist ||
                    scan.Refusal.Code == ReplayRefusalCodes.PredicateDigestMismatch
                        ? SealConditions.ContractPreflight
                        : SealConditions.ArtifactRefused);
            }

            if (scan.Incomparability != null)
            {
                return SealEvaluation.Failed(SealConditions.StrictIneligible);
            }

            var plan = scan.Plan!;
            if (plan.Classification.Outcome.Kind != RecordingOutcomeKind.Completed)
            {
                return SealEvaluation.Failed(SealConditions.NotCompleted);
            }

            if (plan.Stop != null)
            {
                // Anything strict replay would stop at — contamination,
                // OutcomeUnknown shapes, non-Satisfied waits, recorded
                // Unevaluable assertions — disqualifies sealing (§5.2
                // condition 3).
                return SealEvaluation.Failed(SealConditions.StrictIneligible);
            }

            for (var index = 0; index < requiredAssertions.Count; index++)
            {
                if (!HasSatisfiedAssertion(plan, requiredAssertions[index]))
                {
                    return SealEvaluation.Failed(SealConditions.RequiredAssertionNotSatisfied);
                }
            }

            return SealEvaluation.Sealable;
        }

        private sealed class SecretsResolveLater : ISecretReferenceResolver
        {
            internal static SecretsResolveLater Instance { get; } = new SecretsResolveLater();

            private SecretsResolveLater()
            {
            }

            public bool CanResolve(SecretReference reference) => true;

            public bool TryResolve(
                SecretReference reference, ArgumentDigest expectedDigest, out FieldValue value)
            {
                // The seal evaluation never executes; materializing a secret
                // here would be a policy violation, not a convenience.
                throw new InvalidOperationException(
                    "Seal evaluation never resolves secret values.");
            }
        }

        private static bool HasSatisfiedAssertion(ReplayPlan plan, PredicateContractRef required)
        {
            for (var index = 0; index < plan.Reading.Cuts.Count; index++)
            {
                if (plan.Reading.Cuts[index] is AssertionEvaluated assertion &&
                    assertion.Predicate.Equals(required) &&
                    assertion.Outcome.Kind == PredicateEvaluationKind.Satisfied)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
