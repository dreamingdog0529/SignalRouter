using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The shared identifier grammar and bounded-string rules of the v2 contract
    /// surface (ADR 0007 permits sharing exactly these primitives). Identifiers are
    /// compared ordinally and never normalized (semantic-model.md §3.2, §4);
    /// reason codes are stable, case-sensitive code-like identifiers
    /// (guarantees.md §3.5).
    /// </summary>
    public static class ContractGrammar
    {
        /// <summary>Upper bound for free-form identifier values.</summary>
        public const int MaxIdentifierLength = 1024;

        /// <summary>Upper bound for reason and evidence-kind codes.</summary>
        public const int MaxCodeLength = 128;

        /// <summary>
        /// Validates a free-form identifier: non-null, non-empty, bounded, and free of
        /// control characters. The grammar is deliberately permissive — author keys and
        /// request identifiers are caller-chosen — but never unbounded.
        /// </summary>
        public static string ValidateIdentifier(string value, string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("Identifier must not be empty.", parameterName);
            }

            if (value.Length > MaxIdentifierLength)
            {
                throw new ArgumentException(
                    $"Identifier must not exceed {MaxIdentifierLength} characters.",
                    parameterName);
            }

            foreach (var character in value)
            {
                if (char.IsControl(character))
                {
                    throw new ArgumentException(
                        "Identifier must not contain control characters.",
                        parameterName);
                }
            }

            return value;
        }

        /// <summary>
        /// Validates a reason or evidence-kind code: non-null, non-empty, bounded, and
        /// restricted to ASCII letters and digits (guarantees.md §3.5 codes are
        /// code-like identifiers such as <c>SizeLimit</c>).
        /// </summary>
        public static string ValidateCode(string value, string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("Code must not be empty.", parameterName);
            }

            if (value.Length > MaxCodeLength)
            {
                throw new ArgumentException(
                    $"Code must not exceed {MaxCodeLength} characters.",
                    parameterName);
            }

            foreach (var character in value)
            {
                var isAsciiLetterOrDigit =
                    (character >= 'A' && character <= 'Z') ||
                    (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9');
                if (!isAsciiLetterOrDigit)
                {
                    throw new ArgumentException(
                        "Code must contain only ASCII letters and digits.",
                        parameterName);
                }
            }

            return value;
        }

        /// <summary>Combines hash codes without depending on System.HashCode ordering guarantees across runtimes.</summary>
        public static int CombineHashes(int first, int second)
        {
            unchecked
            {
                return (first * 397) ^ second;
            }
        }
    }
}
