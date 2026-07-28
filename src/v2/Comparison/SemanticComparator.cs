using System;
using System.Collections.Generic;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Comparison
{
    /// <summary>
    /// Typed exact comparison of two record-view materializations over the
    /// profile's field set (recording-replay.md §5.1–§5.2) — not hash equality,
    /// not fuzzy matching. Absence, null, unknown, and redaction are four
    /// distinct comparator inputs. Answers Equal, Diverged with a structured
    /// diff in comparison order, or Incomparable(reason) — never a guess. Pure:
    /// a deterministic function of the two materializations, the profile, and
    /// the vocabulary.
    ///
    /// v2.0 freezes deliberately coarse forms (mirroring the profile DTO):
    /// AuthorKey node matching only; item-key and non-ordered collection rules
    /// are declared but unsupported (dynamic collections arrive with the delta
    /// track) and are refused at comparison, never improvised; rule field paths
    /// select attribute names (nodes) and document field names (sources).
    /// </summary>
    public sealed class SemanticComparator
    {
        private const string StateMismatch = "StateMismatch";
        private const string ValueMismatch = "ValueMismatch";

        private readonly ComparisonVocabulary vocabulary;

        public SemanticComparator(ComparisonVocabulary vocabulary)
        {
            this.vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
        }

        public ComparisonResult CompareState(
            ObservationMaterialization recorded,
            ObservationMaterialization actual,
            ReplayComparisonProfile profile)
        {
            if (recorded == null)
            {
                throw new ArgumentNullException(nameof(recorded));
            }

            if (actual == null)
            {
                throw new ArgumentNullException(nameof(actual));
            }

            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (!string.Equals(
                profile.NodeMatching, ReplayComparisonProfile.MatchByAuthorKey, StringComparison.Ordinal))
            {
                return ComparisonResult.Incomparable("UnsupportedNodeMatching");
            }

            if (profile.ItemKeyRules.Count > 0)
            {
                return ComparisonResult.Incomparable("UnsupportedProfileRule");
            }

            for (var index = 0; index < profile.CollectionRules.Count; index++)
            {
                if (profile.CollectionRules[index].Comparison != CollectionComparison.Ordered)
                {
                    return ComparisonResult.Incomparable("UnsupportedProfileRule");
                }
            }

            // v2.0 implements no profile extensions: a mandatory one is unknown
            // by definition; non-mandatory unknowns are ignorable by policy.
            for (var index = 0; index < profile.ExtensionPolicies.Count; index++)
            {
                if (profile.ExtensionPolicies[index].Mandatory)
                {
                    return ComparisonResult.Incomparable(IncomparableReasons.UnknownMandatoryExtension);
                }
            }

            if (!BasisMatchesProfile(recorded.Basis, profile) || !BasisMatchesProfile(actual.Basis, profile))
            {
                return ComparisonResult.Incomparable("ViewMismatch");
            }

            var recordedComplete = recorded.Completeness.Equals(CompletenessMap.Complete);
            var actualComplete = actual.Completeness.Equals(CompletenessMap.Complete);
            if (!recordedComplete || !actualComplete)
            {
                if (profile.RequireCompleteForScope)
                {
                    return ComparisonResult.Incomparable(IncomparableReasons.Incompleteness);
                }

                // Unknown is the fourth comparator input at region granularity:
                // when the two sides' unknown regions coincide exactly, the
                // materialized remainder is comparable; when they differ, a
                // divergence cannot be told apart from a budget difference.
                if (!recorded.Completeness.Equals(actual.Completeness))
                {
                    return ComparisonResult.Incomparable(IncomparableReasons.Incompleteness);
                }
            }

            var entries = new List<SemanticDiffEntry>();
            var incomparable = CompareNodes(recorded, actual, profile, entries) ??
                CompareSources(recorded, actual, profile, entries);
            if (incomparable != null)
            {
                // An incomparable input poisons the whole comparison: partial
                // divergences would rank a guess above an honest refusal.
                return ComparisonResult.Incomparable(incomparable);
            }

            return entries.Count == 0
                ? ComparisonResult.Equal
                : ComparisonResult.Diverged(new SemanticDiff(ValueArray<SemanticDiffEntry>.From(entries)));
        }

        private static bool BasisMatchesProfile(ObservationBasis basis, ReplayComparisonProfile profile) =>
            basis.View.Equals(profile.RecordView) &&
            string.Equals(basis.Scope, profile.Scope, StringComparison.Ordinal);

        // ── Nodes ────────────────────────────────────────────────────────────

        private string? CompareNodes(
            ObservationMaterialization recorded,
            ObservationMaterialization actual,
            ReplayComparisonProfile profile,
            List<SemanticDiffEntry> entries)
        {
            var left = recorded.Nodes;
            var right = actual.Nodes;
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Count || rightIndex < right.Count)
            {
                var order = leftIndex >= left.Count ? 1
                    : rightIndex >= right.Count ? -1
                    : string.CompareOrdinal(left[leftIndex].Key.Value, right[rightIndex].Key.Value);
                if (order < 0)
                {
                    entries.Add(new SemanticDiffEntry(
                        "nodes/" + left[leftIndex].Key.Value, "NodeMissing", "present", "absent"));
                    leftIndex++;
                }
                else if (order > 0)
                {
                    entries.Add(new SemanticDiffEntry(
                        "nodes/" + right[rightIndex].Key.Value, "NodeUnexpected", "absent", "present"));
                    rightIndex++;
                }
                else
                {
                    var incomparable = CompareNode(left[leftIndex], right[rightIndex], profile, entries);
                    if (incomparable != null)
                    {
                        return incomparable;
                    }

                    leftIndex++;
                    rightIndex++;
                }
            }

            return null;
        }

        private string? CompareNode(
            MaterializedNode recorded,
            MaterializedNode actual,
            ReplayComparisonProfile profile,
            List<SemanticDiffEntry> entries)
        {
            var prefix = "nodes/" + recorded.Key.Value;
            if (!recorded.Role.Equals(actual.Role))
            {
                entries.Add(new SemanticDiffEntry(
                    prefix + "/role", ValueMismatch, recorded.Role.Value, actual.Role.Value));
            }

            if (!Nullable.Equals(recorded.Parent, actual.Parent))
            {
                entries.Add(new SemanticDiffEntry(
                    prefix + "/parent", ValueMismatch,
                    recorded.Parent?.Value ?? "none", actual.Parent?.Value ?? "none"));
            }

            if (recorded.VisibleChildCount != actual.VisibleChildCount)
            {
                entries.Add(new SemanticDiffEntry(
                    prefix + "/children", "CountMismatch",
                    recorded.VisibleChildCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    actual.VisibleChildCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            var rule = FindNodeRule(profile, recorded.Role.Value);
            var incomparable = CompareAttributes(recorded, actual, rule, profile, entries, prefix);
            if (incomparable != null)
            {
                return incomparable;
            }

            CompareCapabilities(recorded, actual, entries, prefix);
            return null;
        }

        private static ComparedNodeRule? FindNodeRule(ReplayComparisonProfile profile, string roleCode)
        {
            for (var index = 0; index < profile.NodeRules.Count; index++)
            {
                if (string.Equals(profile.NodeRules[index].RoleCode, roleCode, StringComparison.Ordinal))
                {
                    return profile.NodeRules[index];
                }
            }

            return null;
        }

        private string? CompareAttributes(
            MaterializedNode recorded,
            MaterializedNode actual,
            ComparedNodeRule? rule,
            ReplayComparisonProfile profile,
            List<SemanticDiffEntry> entries,
            string prefix)
        {
            // Merge-walk over the union of attribute names: absence is a
            // comparable state, so a name present on either side participates.
            var left = recorded.Attributes;
            var right = actual.Attributes;
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Count || rightIndex < right.Count)
            {
                var order = leftIndex >= left.Count ? 1
                    : rightIndex >= right.Count ? -1
                    : string.CompareOrdinal(left[leftIndex].Name, right[rightIndex].Name);
                MaterializedAttribute? leftAttribute = null;
                MaterializedAttribute? rightAttribute = null;
                string name;
                if (order <= 0)
                {
                    leftAttribute = left[leftIndex];
                    name = leftAttribute.Name;
                    leftIndex++;
                }
                else
                {
                    name = right[rightIndex].Name;
                }

                if (order >= 0)
                {
                    rightAttribute = right[rightIndex];
                    rightIndex++;
                }

                // A rule narrows the compared field set for its role
                // (default-strict without one); rule entries name attributes.
                if (rule != null && !RuleSelects(rule.Fields, name))
                {
                    continue;
                }

                var path = prefix + "/attributes/" + name;
                var incomparable = CompareFieldStates(
                    path,
                    leftAttribute == null ? FieldState.Absent() :
                        leftAttribute.Redacted ? FieldState.Redacted() : FieldState.Value(leftAttribute.Value),
                    rightAttribute == null ? FieldState.Absent() :
                        rightAttribute.Redacted ? FieldState.Redacted() : FieldState.Value(rightAttribute.Value),
                    profile,
                    entries);
                if (incomparable != null)
                {
                    return incomparable;
                }
            }

            return null;
        }

        private static bool RuleSelects(ValueArray<string> fields, string name)
        {
            for (var index = 0; index < fields.Count; index++)
            {
                if (string.Equals(fields[index], name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void CompareCapabilities(
            MaterializedNode recorded,
            MaterializedNode actual,
            List<SemanticDiffEntry> entries,
            string prefix)
        {
            // Paired by contract id (recording-replay.md §5.2 compares contract
            // versions, so a version drift is a version divergence, not a
            // missing/unexpected pair). Both lists are canonically sorted by
            // id@version, which is id-ordered for the one-declaration-per-
            // contract shape nodes register.
            var left = recorded.Capabilities;
            var right = actual.Capabilities;
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Count || rightIndex < right.Count)
            {
                var order = leftIndex >= left.Count ? 1
                    : rightIndex >= right.Count ? -1
                    : string.CompareOrdinal(
                        left[leftIndex].Contract.Id.Value, right[rightIndex].Contract.Id.Value);
                if (order < 0)
                {
                    entries.Add(new SemanticDiffEntry(
                        prefix + "/capabilities/" + left[leftIndex].Contract.Id.Value,
                        "CapabilityMissing", "present", "absent"));
                    leftIndex++;
                }
                else if (order > 0)
                {
                    entries.Add(new SemanticDiffEntry(
                        prefix + "/capabilities/" + right[rightIndex].Contract.Id.Value,
                        "CapabilityUnexpected", "absent", "present"));
                    rightIndex++;
                }
                else
                {
                    var path = prefix + "/capabilities/" + left[leftIndex].Contract.Id.Value;
                    if (!left[leftIndex].Contract.Version.Equals(right[rightIndex].Contract.Version))
                    {
                        entries.Add(new SemanticDiffEntry(
                            path + "/version", "VersionMismatch",
                            left[leftIndex].Contract.Version.ToString(),
                            right[rightIndex].Contract.Version.ToString()));
                    }

                    if (left[leftIndex].Available != right[rightIndex].Available)
                    {
                        entries.Add(new SemanticDiffEntry(
                            path + "/available", "AvailabilityMismatch",
                            left[leftIndex].Available ? "true" : "false",
                            right[rightIndex].Available ? "true" : "false"));
                    }

                    leftIndex++;
                    rightIndex++;
                }
            }
        }

        // ── Sources ──────────────────────────────────────────────────────────

        private string? CompareSources(
            ObservationMaterialization recorded,
            ObservationMaterialization actual,
            ReplayComparisonProfile profile,
            List<SemanticDiffEntry> entries)
        {
            if (profile.SourceRules.Count == 0)
            {
                // Default-strict: every source, every field.
                return MergeWalkSources(recorded.Sources, actual.Sources, null, profile, entries);
            }

            // Explicit rules declare the strict-scope source set: exactly the
            // listed sources are compared, each under its field filter.
            for (var index = 0; index < profile.SourceRules.Count; index++)
            {
                var rule = profile.SourceRules[index];
                var left = FindSource(recorded.Sources, rule.Source);
                var right = FindSource(actual.Sources, rule.Source);
                var incomparable = CompareSourcePair(left, right, rule, profile, entries);
                if (incomparable != null)
                {
                    return incomparable;
                }
            }

            return null;
        }

        private string? MergeWalkSources(
            ValueArray<MaterializedSource> left,
            ValueArray<MaterializedSource> right,
            ComparedSourceRule? rule,
            ReplayComparisonProfile profile,
            List<SemanticDiffEntry> entries)
        {
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Count || rightIndex < right.Count)
            {
                var order = leftIndex >= left.Count ? 1
                    : rightIndex >= right.Count ? -1
                    : string.CompareOrdinal(left[leftIndex].Key.Value, right[rightIndex].Key.Value);
                if (order < 0)
                {
                    entries.Add(new SemanticDiffEntry(
                        "sources/" + left[leftIndex].Key.Value, "SourceMissing", "present", "absent"));
                    leftIndex++;
                }
                else if (order > 0)
                {
                    entries.Add(new SemanticDiffEntry(
                        "sources/" + right[rightIndex].Key.Value, "SourceUnexpected", "absent", "present"));
                    rightIndex++;
                }
                else
                {
                    var incomparable = CompareSourcePair(
                        left[leftIndex], right[rightIndex], rule, profile, entries);
                    if (incomparable != null)
                    {
                        return incomparable;
                    }

                    leftIndex++;
                    rightIndex++;
                }
            }

            return null;
        }

        private static MaterializedSource? FindSource(
            ValueArray<MaterializedSource> sources, StateSourceKey key)
        {
            for (var index = 0; index < sources.Count; index++)
            {
                if (sources[index].Key.Equals(key))
                {
                    return sources[index];
                }
            }

            return null;
        }

        private string? CompareSourcePair(
            MaterializedSource? recorded,
            MaterializedSource? actual,
            ComparedSourceRule? rule,
            ReplayComparisonProfile profile,
            List<SemanticDiffEntry> entries)
        {
            var key = (recorded ?? actual)!.Key.Value;
            var prefix = "sources/" + key;
            if (recorded == null || actual == null)
            {
                entries.Add(new SemanticDiffEntry(
                    prefix,
                    recorded == null ? "SourceUnexpected" : "SourceMissing",
                    recorded == null ? "absent" : "present",
                    actual == null ? "absent" : "present"));
                return null;
            }

            // A Stale or UnsupportedContract omission makes the document state
            // unknowable on that side — out of tier or out of vocabulary, an
            // honest refusal either way. Deterministic absence
            // (SourceUnavailable) is a comparable state.
            var incomparableOmission = IncomparableOmission(recorded.Omission) ??
                IncomparableOmission(actual.Omission);
            if (incomparableOmission != null)
            {
                return incomparableOmission;
            }

            var recordedUnavailable = recorded.Omission.HasValue;
            var actualUnavailable = actual.Omission.HasValue;
            if (recordedUnavailable || actualUnavailable)
            {
                if (recordedUnavailable != actualUnavailable)
                {
                    entries.Add(new SemanticDiffEntry(
                        prefix, StateMismatch,
                        recordedUnavailable ? "unavailable" : "present",
                        actualUnavailable ? "unavailable" : "present"));
                }

                return null;
            }

            if (!recorded.Contract.Equals(actual.Contract))
            {
                entries.Add(new SemanticDiffEntry(
                    prefix + "/contract", "ContractMismatch",
                    RenderContract(recorded.Contract), RenderContract(actual.Contract)));
            }

            return CompareSourceFields(recorded, actual, rule, profile, entries, prefix);
        }

        private static string? IncomparableOmission(CompletenessReason? omission)
        {
            if (!omission.HasValue || omission.Value == CompletenessReason.SourceUnavailable)
            {
                return null;
            }

            return omission.Value == CompletenessReason.Stale ? "Stale" : "UnsupportedContract";
        }

        private static string RenderContract(StateSourceContractRef contract) =>
            contract.Id.Value + "@" + contract.Version;

        private string? CompareSourceFields(
            MaterializedSource recorded,
            MaterializedSource actual,
            ComparedSourceRule? rule,
            ReplayComparisonProfile profile,
            List<SemanticDiffEntry> entries,
            string prefix)
        {
            // Union of present and redacted names on both sides, in ordinal
            // order — four-state per name, filtered by the rule's field set.
            var names = new SortedSet<string>(StringComparer.Ordinal);
            CollectNames(recorded, names);
            CollectNames(actual, names);
            foreach (var name in names)
            {
                if (rule != null && !RuleSelects(rule.Fields, name))
                {
                    continue;
                }

                var incomparable = CompareFieldStates(
                    prefix + "/" + name,
                    SourceFieldState(recorded, name),
                    SourceFieldState(actual, name),
                    profile,
                    entries);
                if (incomparable != null)
                {
                    return incomparable;
                }
            }

            return null;
        }

        private static void CollectNames(MaterializedSource source, SortedSet<string> names)
        {
            for (var index = 0; index < source.Fields.Count; index++)
            {
                names.Add(source.Fields[index].Name);
            }

            for (var index = 0; index < source.RedactedFieldNames.Count; index++)
            {
                names.Add(source.RedactedFieldNames[index]);
            }
        }

        private static FieldState SourceFieldState(MaterializedSource source, string name)
        {
            for (var index = 0; index < source.RedactedFieldNames.Count; index++)
            {
                if (string.Equals(source.RedactedFieldNames[index], name, StringComparison.Ordinal))
                {
                    return FieldState.Redacted();
                }
            }

            for (var index = 0; index < source.Fields.Count; index++)
            {
                if (string.Equals(source.Fields[index].Name, name, StringComparison.Ordinal))
                {
                    return FieldState.Value(source.Fields[index].Value);
                }
            }

            return FieldState.Absent();
        }

        // ── Field states (the four comparator inputs) ────────────────────────

        private readonly struct FieldState
        {
            private enum StateKind
            {
                Value,
                Null,
                Absent,
                Redacted,
            }

            private readonly StateKind kind;
            private readonly FieldValue value;

            private FieldState(StateKind kind, FieldValue value)
            {
                this.kind = kind;
                this.value = value;
            }

            internal static FieldState Value(FieldValue value) =>
                value.Kind == FieldValueKind.Null
                    ? new FieldState(StateKind.Null, default)
                    : new FieldState(StateKind.Value, value);

            internal static FieldState Absent() => new FieldState(StateKind.Absent, default);

            internal static FieldState Redacted() => new FieldState(StateKind.Redacted, default);

            internal bool IsValue => kind == StateKind.Value;

            internal FieldValue ValueOf => value;

            internal bool SameStateAs(FieldState other) => kind == other.kind;

            internal string Render() => kind switch
            {
                StateKind.Value => "value:" + value,
                StateKind.Null => "null",
                StateKind.Absent => "absent",
                _ => "redacted",
            };
        }

        private string? CompareFieldStates(
            string path,
            FieldState recorded,
            FieldState actual,
            ReplayComparisonProfile profile,
            List<SemanticDiffEntry> entries)
        {
            if (!recorded.SameStateAs(actual))
            {
                entries.Add(new SemanticDiffEntry(
                    path, StateMismatch, recorded.Render(), actual.Render()));
                return null;
            }

            if (!recorded.IsValue)
            {
                return null;
            }

            var recordedValue = recorded.ValueOf;
            var actualValue = actual.ValueOf;
            var rule = FindNormalization(profile, path);
            if (rule != null)
            {
                if (!vocabulary.TryGetNormalizer(rule.NormalizerCode, out var normalizer))
                {
                    // An unknown normalizer code refuses the comparison — a
                    // guessed identity would change what Equal means.
                    return "UnknownNormalizer";
                }

                recordedValue = normalizer.Normalize(recordedValue);
                actualValue = normalizer.Normalize(actualValue);
            }

            if (!recordedValue.Equals(actualValue))
            {
                entries.Add(new SemanticDiffEntry(
                    path, ValueMismatch,
                    "value:" + recordedValue, "value:" + actualValue));
            }

            return null;
        }

        private static NormalizationRule? FindNormalization(ReplayComparisonProfile profile, string path)
        {
            for (var index = 0; index < profile.NormalizationRules.Count; index++)
            {
                if (string.Equals(profile.NormalizationRules[index].FieldPath, path, StringComparison.Ordinal))
                {
                    return profile.NormalizationRules[index];
                }
            }

            return null;
        }
    }
}
