using System;
using System.Collections.Generic;
using SignalRouter.Contracts;

namespace SignalRouter.Comparison
{
    /// <summary>
    /// One value normalizer (recording-replay.md §5): a pure function applied to
    /// both sides before typed equality. Determinism is the implementer's
    /// obligation — a normalizer reading ambient state would silently break
    /// replay comparison.
    /// </summary>
    public interface IValueNormalizer
    {
        FieldValue Normalize(FieldValue value);
    }

    /// <summary>
    /// One schema migration (recording-replay.md §5; semantic-model.md §5): a
    /// pure projection of an older recorded profile document onto the version
    /// the comparator supports.
    /// </summary>
    public interface IProfileMigration
    {
        ReplayComparisonProfile Project(ReplayComparisonProfile recorded);
    }

    /// <summary>
    /// The comparator's open extension surface: registered normalizers and
    /// profile migrations. v2.0 reserves only the Identity normalizer and ships
    /// no migrations; an unknown code at comparison time answers Incomparable,
    /// never a guess. Registration happens at assembly time on one thread;
    /// instances are read-only afterwards by convention.
    /// </summary>
    public sealed class ComparisonVocabulary
    {
        private sealed class IdentityNormalizer : IValueNormalizer
        {
            public FieldValue Normalize(FieldValue value) => value;
        }

        private readonly Dictionary<string, IValueNormalizer> normalizers =
            new Dictionary<string, IValueNormalizer>(StringComparer.Ordinal);

        private readonly Dictionary<ReplayComparisonProfileRef, IProfileMigration> migrations =
            new Dictionary<ReplayComparisonProfileRef, IProfileMigration>();

        public ComparisonVocabulary()
        {
            normalizers.Add(NormalizationRule.Identity, new IdentityNormalizer());
        }

        public void RegisterNormalizer(string code, IValueNormalizer normalizer)
        {
            ContractGrammar.ValidateCode(code, nameof(code));
            if (normalizer == null)
            {
                throw new ArgumentNullException(nameof(normalizer));
            }

            // Add-only: silently replacing a normalizer would change what a
            // recorded comparison means.
            normalizers.Add(code, normalizer);
        }

        public void RegisterMigration(ReplayComparisonProfileRef recordedVersion, IProfileMigration migration)
        {
            if (recordedVersion.IsDefault)
            {
                throw new ArgumentException(
                    "A migration requires a non-default recorded version.", nameof(recordedVersion));
            }

            if (migration == null)
            {
                throw new ArgumentNullException(nameof(migration));
            }

            migrations.Add(recordedVersion, migration);
        }

        public bool TryGetNormalizer(string code, out IValueNormalizer normalizer)
        {
            if (normalizers.TryGetValue(code, out var found))
            {
                normalizer = found;
                return true;
            }

            normalizer = null!;
            return false;
        }

        public bool TryGetMigration(ReplayComparisonProfileRef recordedVersion, out IProfileMigration migration)
        {
            if (migrations.TryGetValue(recordedVersion, out var found))
            {
                migration = found;
                return true;
            }

            migration = null!;
            return false;
        }
    }
}
