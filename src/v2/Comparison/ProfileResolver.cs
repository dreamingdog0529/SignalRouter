using System;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Comparison
{
    /// <summary>The answer of a profile resolution: the effective document, or why none exists.</summary>
    public sealed class ProfileResolution
    {
        private ProfileResolution(ReplayComparisonProfile? effective, ComparisonOutcome outcome)
        {
            Effective = effective;
            Outcome = outcome;
        }

        /// <summary>Non-null exactly when <see cref="Outcome"/> is not Incomparable.</summary>
        public ReplayComparisonProfile? Effective { get; }

        public ComparisonOutcome Outcome { get; }

        internal static ProfileResolution Resolved(ReplayComparisonProfile effective) =>
            new ProfileResolution(effective, ComparisonOutcome.Equal);

        internal static ProfileResolution Incomparable(string reason) =>
            new ProfileResolution(null, ComparisonOutcome.Incomparable(reason));
    }

    /// <summary>
    /// Resolves the artifact-pinned profile document against the profile the
    /// replayer supports (recording-replay.md §5; semantic-model.md §5). The
    /// artifact's embedded document is authoritative for its own version —
    /// registry drift never rewrites a recording's meaning; migration projects
    /// an older recorded document onto the supported version through the
    /// vocabulary's registered projections.
    /// </summary>
    public static class ProfileResolver
    {
        public static ProfileResolution Resolve(
            ReplayComparisonProfile recorded,
            ReplayComparisonProfile supported,
            ComparisonVocabulary vocabulary)
        {
            if (recorded == null)
            {
                throw new ArgumentNullException(nameof(recorded));
            }

            if (supported == null)
            {
                throw new ArgumentNullException(nameof(supported));
            }

            if (vocabulary == null)
            {
                throw new ArgumentNullException(nameof(vocabulary));
            }

            if (!recorded.Reference.Id.Equals(supported.Reference.Id))
            {
                return ProfileResolution.Incomparable(IncomparableReasons.UnsupportedProfileVersion);
            }

            if (recorded.Reference.Version.Equals(supported.Reference.Version))
            {
                return ProfileResolution.Resolved(recorded);
            }

            // Only a declared-projectable older version can migrate; anything
            // else — newer, or older but undeclared — is unsupported outright.
            var declared = false;
            for (var index = 0; index < supported.ProjectableFromVersions.Count; index++)
            {
                if (supported.ProjectableFromVersions[index].Equals(recorded.Reference.Version))
                {
                    declared = true;
                    break;
                }
            }

            if (!declared)
            {
                return ProfileResolution.Incomparable(IncomparableReasons.UnsupportedProfileVersion);
            }

            if (!vocabulary.TryGetMigration(recorded.Reference, out var migration))
            {
                // Projectability is declared but no projection is registered:
                // there is no common comparison profile (guarantees.md §3.5).
                return ProfileResolution.Incomparable(IncomparableReasons.MissingMigration);
            }

            var projected = migration.Project(recorded);
            if (projected == null ||
                !projected.Reference.Equals(supported.Reference))
            {
                // A projection that answers the wrong version is a registration
                // bug, surfaced as the same honest refusal — never a guess.
                return ProfileResolution.Incomparable(IncomparableReasons.MissingMigration);
            }

            return ProfileResolution.Resolved(projected);
        }
    }
}
