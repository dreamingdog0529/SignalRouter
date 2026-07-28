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

    /// <summary>
    /// The verification.md §5.2 condition codes a failed seal reports — one per
    /// condition, first failing condition in §5.2 order. Trust-of-origin and
    /// resource-budget refusals sit outside the five conditions (the artifact
    /// never became evaluable input) and report <see cref="ArtifactRefused"/>.
    /// </summary>
    public static class SealConditions
    {
        /// <summary>
        /// Conditions 1–2: not Completed with reader-verified closure — the
        /// classification, an integrity/closure failure, or a missing artifact.
        /// v2.0 artifacts are self-contained by construction, so condition 2
        /// reduces to the reader's blob-closure verification folded in here.
        /// </summary>
        public const string NotCompleted = "NotCompleted";

        /// <summary>Outside the five conditions: provenance or resource budget refused the input.</summary>
        public const string ArtifactRefused = "ArtifactRefused";

        /// <summary>Condition 3: strict replay would stop at or answer Incomparable for some cut.</summary>
        public const string StrictIneligible = "StrictIneligible";

        /// <summary>Condition 4: a required assertion is missing, or any of its evaluations was not Satisfied.</summary>
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
            ValueArray<EvidenceSequence> requiredAssertions)
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
                // Map each refusal onto its §5.2 condition: integrity/closure
                // failures and a missing artifact are conditions 1–2; contract
                // failures are condition 5; only trust-of-origin and resource
                // budgets fall outside the five.
                switch (scan.Refusal.Code)
                {
                    case ReplayRefusalCodes.ContractAllowlist:
                    case ReplayRefusalCodes.PredicateDigestMismatch:
                        return SealEvaluation.Failed(SealConditions.ContractPreflight);
                    case ReplayRefusalCodes.ArtifactIntegrity:
                    case ReplayRefusalCodes.OpenFailed:
                        return SealEvaluation.Failed(SealConditions.NotCompleted);
                    default:
                        return SealEvaluation.Failed(SealConditions.ArtifactRefused);
                }
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

            // The manifest names its required assertions by evidence reference
            // (verification.md §5.1): only the selected E8 cuts are judged —
            // a non-required assertion may legitimately have answered False.
            for (var index = 0; index < requiredAssertions.Count; index++)
            {
                if (!IsSatisfiedAssertionAt(plan, requiredAssertions[index]))
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

        private static bool IsSatisfiedAssertionAt(ReplayPlan plan, EvidenceSequence required)
        {
            for (var index = 0; index < plan.Reading.Cuts.Count; index++)
            {
                if (plan.Reading.Cuts[index].Sequence.Equals(required))
                {
                    return plan.Reading.Cuts[index] is AssertionEvaluated assertion &&
                        assertion.Outcome.Kind == PredicateEvaluationKind.Satisfied;
                }
            }

            return false;
        }
    }
}
