using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// One divergent position in a first-divergence report: the compared path,
    /// an open detail code, and the two renderings — built from recording-safe
    /// fields only, so a secret can never leak through a diff
    /// (recording-replay.md §6). Absence, null, unknown, and redaction are four
    /// distinct comparator inputs; renderings spell them out rather than
    /// collapsing them.
    /// </summary>
    public sealed class SemanticDiffEntry : IEquatable<SemanticDiffEntry>
    {
        /// <summary>
        /// A diff path composes several identifier segments ("nodes/&lt;key&gt;/attributes/&lt;name&gt;"),
        /// so its bound is a small multiple of one identifier's — a maximum-length
        /// AuthorKey must still produce a reportable divergence, never a throw.
        /// </summary>
        public const int MaxPathLength = 4 * ContractGrammar.MaxIdentifierLength;

        public SemanticDiffEntry(string path, string detailCode, string recorded, string actual)
        {
            Path = ValidatePath(path, nameof(path));
            DetailCode = ContractGrammar.ValidateCode(detailCode, nameof(detailCode));
            Recorded = ContractGrammar.ValidateScalarText(recorded, nameof(recorded));
            Actual = ContractGrammar.ValidateScalarText(actual, nameof(actual));
        }

        private static string ValidatePath(string value, string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("A diff path must not be empty.", parameterName);
            }

            if (value.Length > MaxPathLength)
            {
                throw new ArgumentException(
                    $"A diff path must not exceed {MaxPathLength} characters.", parameterName);
            }

            foreach (var character in value)
            {
                if (char.IsControl(character))
                {
                    throw new ArgumentException(
                        "A diff path must not contain control characters.", parameterName);
                }
            }

            return ContractGrammar.ValidateScalarText(value, parameterName);
        }

        public string Path { get; }

        /// <summary>Open vocabulary (e.g. "ValueMismatch", "NodeMissing").</summary>
        public string DetailCode { get; }

        public string Recorded { get; }

        public string Actual { get; }

        public bool Equals(SemanticDiffEntry? other) =>
            other != null &&
            string.Equals(Path, other.Path, StringComparison.Ordinal) &&
            string.Equals(DetailCode, other.DetailCode, StringComparison.Ordinal) &&
            string.Equals(Recorded, other.Recorded, StringComparison.Ordinal) &&
            string.Equals(Actual, other.Actual, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as SemanticDiffEntry);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(Path),
                StringComparer.Ordinal.GetHashCode(DetailCode));
            hash = ContractGrammar.CombineHashes(hash, StringComparer.Ordinal.GetHashCode(Recorded));
            return ContractGrammar.CombineHashes(hash, StringComparer.Ordinal.GetHashCode(Actual));
        }
    }

    /// <summary>
    /// The structured half of a <c>Diverged</c> answer: at least one entry, in
    /// comparison order. A diff never exists for <c>Equal</c> or
    /// <c>Incomparable</c> (guarantees.md §3.3).
    /// </summary>
    public sealed class SemanticDiff
    {
        public SemanticDiff(ValueArray<SemanticDiffEntry> entries)
        {
            if (entries.Count == 0)
            {
                throw new ArgumentException("A diff carries at least one entry.", nameof(entries));
            }

            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index] == null)
                {
                    throw new ArgumentException("Diff entries must be non-null.", nameof(entries));
                }
            }

            Entries = entries;
        }

        public ValueArray<SemanticDiffEntry> Entries { get; }
    }
}
