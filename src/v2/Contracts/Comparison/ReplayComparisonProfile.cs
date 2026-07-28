using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>How a collection-valued field is compared (recording-replay.md §5).</summary>
    public enum CollectionComparison
    {
        Ordered = 0,
        Set = 1,
        Multiset = 2,
    }

    /// <summary>The compared field set for one node role (recording-replay.md §5.2).</summary>
    public sealed class ComparedNodeRule : IEquatable<ComparedNodeRule>
    {
        public ComparedNodeRule(string roleCode, ValueArray<string> fields)
        {
            RoleCode = ContractGrammar.ValidateCode(roleCode, nameof(roleCode));
            ValidateSortedUniquePaths(fields, nameof(fields));
            Fields = fields;
        }

        public string RoleCode { get; }

        /// <summary>Field paths, ordinal-sorted and unique.</summary>
        public ValueArray<string> Fields { get; }

        public bool Equals(ComparedNodeRule? other) =>
            other != null &&
            string.Equals(RoleCode, other.RoleCode, StringComparison.Ordinal) &&
            SequenceEquals(Fields, other.Fields);

        public override bool Equals(object? obj) => Equals(obj as ComparedNodeRule);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(RoleCode), SequenceHash(Fields));

        internal static void ValidateSortedUniquePaths(ValueArray<string> values, string parameterName)
        {
            for (var index = 0; index < values.Count; index++)
            {
                // Rule paths follow the FieldPath grammar so they can be matched
                // against segmented materialization paths deterministically.
                _ = new FieldPath(values[index]);
                if (index > 0)
                {
                    var comparison = string.CompareOrdinal(values[index - 1], values[index]);
                    if (comparison >= 0)
                    {
                        throw new ArgumentException(
                            "Entries must be ordinal-sorted and unique.", parameterName);
                    }
                }
            }
        }

        internal static bool SequenceEquals(ValueArray<string> left, ValueArray<string> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        internal static int SequenceHash(ValueArray<string> values)
        {
            var hash = values.Count;
            for (var index = 0; index < values.Count; index++)
            {
                hash = ContractGrammar.CombineHashes(
                    hash, StringComparer.Ordinal.GetHashCode(values[index]));
            }

            return hash;
        }
    }

    /// <summary>The compared field set for one strict-scope state source (observation-state.md §7).</summary>
    public sealed class ComparedSourceRule : IEquatable<ComparedSourceRule>
    {
        public ComparedSourceRule(StateSourceKey source, ValueArray<string> fields)
        {
            if (source.IsDefault)
            {
                throw new ArgumentException(
                    "A compared source rule requires a non-default source key.", nameof(source));
            }

            ComparedNodeRule.ValidateSortedUniquePaths(fields, nameof(fields));
            Source = source;
            Fields = fields;
        }

        public StateSourceKey Source { get; }

        /// <summary>Document field paths, ordinal-sorted and unique.</summary>
        public ValueArray<string> Fields { get; }

        public bool Equals(ComparedSourceRule? other) =>
            other != null && Source.Equals(other.Source) &&
            ComparedNodeRule.SequenceEquals(Fields, other.Fields);

        public override bool Equals(object? obj) => Equals(obj as ComparedSourceRule);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(
                Source.GetHashCode(), ComparedNodeRule.SequenceHash(Fields));
    }

    /// <summary>
    /// The stable item key that pairs the items of one dynamic collection
    /// (recording-replay.md §5; semantic-model.md §3.2 — dynamic collections use
    /// scope-stable item keys).
    /// </summary>
    public sealed class ItemKeyRule : IEquatable<ItemKeyRule>
    {
        public ItemKeyRule(string collectionPath, string keyField)
        {
            _ = new FieldPath(collectionPath);
            _ = new FieldPath(keyField);
            CollectionPath = collectionPath;
            KeyField = keyField;
        }

        public string CollectionPath { get; }

        /// <summary>The item field whose value pairs recorded and live items.</summary>
        public string KeyField { get; }

        public bool Equals(ItemKeyRule? other) =>
            other != null &&
            string.Equals(CollectionPath, other.CollectionPath, StringComparison.Ordinal) &&
            string.Equals(KeyField, other.KeyField, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as ItemKeyRule);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(CollectionPath),
                StringComparer.Ordinal.GetHashCode(KeyField));
    }

    /// <summary>One field's collection-comparison rule (recording-replay.md §5).</summary>
    public sealed class CollectionRule : IEquatable<CollectionRule>
    {
        public CollectionRule(string fieldPath, CollectionComparison comparison)
        {
            _ = new FieldPath(fieldPath);
            FieldPath = fieldPath;
            if (comparison < CollectionComparison.Ordered || comparison > CollectionComparison.Multiset)
            {
                throw new ArgumentOutOfRangeException(nameof(comparison));
            }

            Comparison = comparison;
        }

        public string FieldPath { get; }

        public CollectionComparison Comparison { get; }

        public bool Equals(CollectionRule? other) =>
            other != null &&
            string.Equals(FieldPath, other.FieldPath, StringComparison.Ordinal) &&
            Comparison == other.Comparison;

        public override bool Equals(object? obj) => Equals(obj as CollectionRule);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(FieldPath), (int)Comparison);
    }

    /// <summary>
    /// One field's value-normalization rule. The normalizer vocabulary is open;
    /// v2.0 reserves only <see cref="Identity"/>, and a comparator refusing an
    /// unknown code answers <c>Incomparable</c>, never a guess.
    /// </summary>
    public sealed class NormalizationRule : IEquatable<NormalizationRule>
    {
        public const string Identity = "Identity";

        public NormalizationRule(string fieldPath, string normalizerCode)
        {
            _ = new FieldPath(fieldPath);
            FieldPath = fieldPath;
            NormalizerCode = ContractGrammar.ValidateCode(normalizerCode, nameof(normalizerCode));
        }

        public string FieldPath { get; }

        public string NormalizerCode { get; }

        public bool Equals(NormalizationRule? other) =>
            other != null &&
            string.Equals(FieldPath, other.FieldPath, StringComparison.Ordinal) &&
            string.Equals(NormalizerCode, other.NormalizerCode, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as NormalizationRule);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(FieldPath),
                StringComparer.Ordinal.GetHashCode(NormalizerCode));
    }

    /// <summary>Whether an unknown profile extension may be ignored (recording-replay.md §5).</summary>
    public sealed class ExtensionPolicy : IEquatable<ExtensionPolicy>
    {
        public ExtensionPolicy(string extensionId, bool mandatory)
        {
            ExtensionId = ContractGrammar.ValidateIdentifier(extensionId, nameof(extensionId));
            Mandatory = mandatory;
        }

        public string ExtensionId { get; }

        public bool Mandatory { get; }

        public bool Equals(ExtensionPolicy? other) =>
            other != null &&
            string.Equals(ExtensionId, other.ExtensionId, StringComparison.Ordinal) &&
            Mandatory == other.Mandatory;

        public override bool Equals(object? obj) => Equals(obj as ExtensionPolicy);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(ExtensionId), Mandatory ? 1 : 0);
    }

    /// <summary>
    /// The declarative content of a comparison profile — everything strict replay
    /// compares and how (recording-replay.md §5; ADR 0015). The artifact embeds
    /// this document with its digest; E1 pins only the
    /// <see cref="ReplayComparisonProfileRef"/>.
    ///
    /// v2.0 freezes deliberately coarse forms: node matching is
    /// <see cref="MatchByAuthorKey"/> only (the vocabulary is open for future
    /// modes); the completeness requirement is whole-scope
    /// (<see cref="RequireCompleteForScope"/>), not per-region; and terminal
    /// evidence (outcome, fault code, completion evidence) is always fully
    /// compared — it is not profile-selectable.
    /// </summary>
    public sealed class ReplayComparisonProfile
    {
        public const string MatchByAuthorKey = "AuthorKey";

        public ReplayComparisonProfile(
            ReplayComparisonProfileRef reference,
            ViewContractRef recordView,
            string scope,
            RedactionPolicyId redactionPolicy,
            string nodeMatching,
            ValueArray<ComparedNodeRule> nodeRules,
            ValueArray<ComparedSourceRule> sourceRules,
            ValueArray<ItemKeyRule> itemKeyRules,
            ValueArray<CollectionRule> collectionRules,
            ValueArray<NormalizationRule> normalizationRules,
            bool requireCompleteForScope,
            ValueArray<ExtensionPolicy> extensionPolicies,
            ValueArray<ContractVersion> projectableFromVersions)
        {
            if (reference.IsDefault)
            {
                throw new ArgumentException(
                    "A profile requires a non-default reference.", nameof(reference));
            }

            if (recordView.IsDefault)
            {
                throw new ArgumentException(
                    "A profile requires a non-default record view.", nameof(recordView));
            }

            if (redactionPolicy.IsDefault)
            {
                throw new ArgumentException(
                    "A profile requires a non-default redaction policy.", nameof(redactionPolicy));
            }

            Reference = reference;
            RecordView = recordView;
            Scope = ContractGrammar.ValidateIdentifier(scope, nameof(scope));
            RedactionPolicy = redactionPolicy;
            NodeMatching = ContractGrammar.ValidateCode(nodeMatching, nameof(nodeMatching));
            NodeRules = ValidateRuleOrder(nodeRules, nameof(nodeRules));
            SourceRules = ValidateSourceOrder(sourceRules, nameof(sourceRules));
            ItemKeyRules = ValidateItemKeyOrder(itemKeyRules, nameof(itemKeyRules));
            CollectionRules = ValidateCollectionOrder(collectionRules, nameof(collectionRules));
            NormalizationRules = ValidateNormalizationOrder(
                normalizationRules, nameof(normalizationRules));
            RequireCompleteForScope = requireCompleteForScope;
            ExtensionPolicies = ValidateExtensionOrder(extensionPolicies, nameof(extensionPolicies));
            for (var index = 0; index < projectableFromVersions.Count; index++)
            {
                var version = projectableFromVersions[index];
                var isOlder = version.Major < reference.Version.Major ||
                    (version.Major == reference.Version.Major &&
                     version.Minor < reference.Version.Minor);
                if (!isOlder)
                {
                    throw new ArgumentException(
                        "Projectable versions must be strictly older than the profile's own version.",
                        nameof(projectableFromVersions));
                }

                if (index > 0)
                {
                    var previous = projectableFromVersions[index - 1];
                    var ascending = previous.Major < version.Major ||
                        (previous.Major == version.Major && previous.Minor < version.Minor);
                    if (!ascending)
                    {
                        throw new ArgumentException(
                            "Projectable versions must be ascending and unique.",
                            nameof(projectableFromVersions));
                    }
                }
            }

            ProjectableFromVersions = projectableFromVersions;
        }

        public ReplayComparisonProfileRef Reference { get; }

        public ViewContractRef RecordView { get; }

        public string Scope { get; }

        public RedactionPolicyId RedactionPolicy { get; }

        /// <summary>Open node-matching vocabulary; v2.0 reserves "author-key".</summary>
        public string NodeMatching { get; }

        /// <summary>Empty means every field of every node is compared (default-strict).</summary>
        public ValueArray<ComparedNodeRule> NodeRules { get; }

        public ValueArray<ComparedSourceRule> SourceRules { get; }

        /// <summary>Stable item-key pairing for dynamic collections in strict scope.</summary>
        public ValueArray<ItemKeyRule> ItemKeyRules { get; }

        public ValueArray<CollectionRule> CollectionRules { get; }

        public ValueArray<NormalizationRule> NormalizationRules { get; }

        /// <summary>The completeness requirement (observation-state.md §3) for v2.0.</summary>
        public bool RequireCompleteForScope { get; }

        public ValueArray<ExtensionPolicy> ExtensionPolicies { get; }

        /// <summary>Older profile versions projectable onto this one; empty = none.</summary>
        public ValueArray<ContractVersion> ProjectableFromVersions { get; }

        private static ValueArray<ComparedNodeRule> ValidateRuleOrder(
            ValueArray<ComparedNodeRule> rules, string parameterName)
        {
            for (var index = 1; index < rules.Count; index++)
            {
                if (string.CompareOrdinal(rules[index - 1].RoleCode, rules[index].RoleCode) >= 0)
                {
                    throw new ArgumentException(
                        "Node rules must be ordinal-sorted by role and unique.", parameterName);
                }
            }

            return rules;
        }

        private static ValueArray<ComparedSourceRule> ValidateSourceOrder(
            ValueArray<ComparedSourceRule> rules, string parameterName)
        {
            for (var index = 1; index < rules.Count; index++)
            {
                if (string.CompareOrdinal(
                    rules[index - 1].Source.Value, rules[index].Source.Value) >= 0)
                {
                    throw new ArgumentException(
                        "Source rules must be ordinal-sorted by key and unique.", parameterName);
                }
            }

            return rules;
        }

        private static ValueArray<ItemKeyRule> ValidateItemKeyOrder(
            ValueArray<ItemKeyRule> rules, string parameterName)
        {
            for (var index = 1; index < rules.Count; index++)
            {
                if (string.CompareOrdinal(
                    rules[index - 1].CollectionPath, rules[index].CollectionPath) >= 0)
                {
                    throw new ArgumentException(
                        "Item-key rules must be ordinal-sorted by collection path and unique.",
                        parameterName);
                }
            }

            return rules;
        }

        private static ValueArray<CollectionRule> ValidateCollectionOrder(
            ValueArray<CollectionRule> rules, string parameterName)
        {
            for (var index = 1; index < rules.Count; index++)
            {
                if (string.CompareOrdinal(rules[index - 1].FieldPath, rules[index].FieldPath) >= 0)
                {
                    throw new ArgumentException(
                        "Collection rules must be ordinal-sorted by field path and unique.",
                        parameterName);
                }
            }

            return rules;
        }

        private static ValueArray<NormalizationRule> ValidateNormalizationOrder(
            ValueArray<NormalizationRule> rules, string parameterName)
        {
            for (var index = 1; index < rules.Count; index++)
            {
                if (string.CompareOrdinal(rules[index - 1].FieldPath, rules[index].FieldPath) >= 0)
                {
                    throw new ArgumentException(
                        "Normalization rules must be ordinal-sorted by field path and unique.",
                        parameterName);
                }
            }

            return rules;
        }

        private static ValueArray<ExtensionPolicy> ValidateExtensionOrder(
            ValueArray<ExtensionPolicy> policies, string parameterName)
        {
            for (var index = 1; index < policies.Count; index++)
            {
                if (string.CompareOrdinal(
                    policies[index - 1].ExtensionId, policies[index].ExtensionId) >= 0)
                {
                    throw new ArgumentException(
                        "Extension policies must be ordinal-sorted by id and unique.", parameterName);
                }
            }

            return policies;
        }
    }
}
