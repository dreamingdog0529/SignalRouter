using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace SignalRouter.Protocol
{
    // Protocol-local argument validation. Core's InteractionContract is internal
    // to SignalRouter.Core; the protocol assembly keeps its own copy instead of
    // widening Core's internals across the package boundary. Unlike Core, wire
    // identifiers are additionally length-capped because they arrive from an
    // untrusted peer and become ledger keys (design §19).
    internal static class ProtocolContract
    {
        public static void RequireIdentifier(string value, string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("The value must not be empty.", parameterName);
            }

            if (value.Length > ProtocolLimits.MaxIdentifierChars)
            {
                throw new ArgumentException(
                    "The value must not exceed "
                    + ProtocolLimits.MaxIdentifierChars.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    + " characters.",
                    parameterName);
            }

            if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1]))
            {
                throw new ArgumentException(
                    "The value must not have leading or trailing whitespace.",
                    parameterName);
            }

            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsControl(value[index]))
                {
                    throw new ArgumentException(
                        "The value must not contain control characters.",
                        parameterName);
                }
            }
        }

        public static string RequireIdentifierValue(string value, string parameterName)
        {
            RequireIdentifier(value, parameterName);
            return value;
        }

        public static void RequireOptionalIdentifier(string? value, string parameterName)
        {
            if (value != null)
            {
                RequireIdentifier(value, parameterName);
            }
        }

        // Single-line human-readable text (peer versions, error messages): bounded,
        // trimmed, and free of control characters so it is always safe to log.
        public static void RequireText(string value, int maxChars, string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("The value must not be empty.", parameterName);
            }

            if (value.Length > maxChars)
            {
                throw new ArgumentException(
                    "The value must not exceed "
                    + maxChars.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " characters.",
                    parameterName);
            }

            if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1]))
            {
                throw new ArgumentException(
                    "The value must not have leading or trailing whitespace.",
                    parameterName);
            }

            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsControl(value[index]))
                {
                    throw new ArgumentException(
                        "The value must not contain control characters.",
                        parameterName);
                }
            }
        }

        public static void RequireDefinedEnum<TEnum>(TEnum value, string parameterName)
            where TEnum : struct
        {
            if (!Enum.IsDefined(typeof(TEnum), value))
            {
                throw new ArgumentOutOfRangeException(parameterName, value, "Undefined enum value.");
            }
        }

        public static int CombineHashCodes(int first, int second)
        {
            unchecked
            {
                return (first * 397) ^ second;
            }
        }

        // Normalizes a capability set: bounded count, bounded entry length,
        // ordinal-unique, ordinal-sorted (design §18.3 handshake capabilities).
        public static ReadOnlyCollection<string> CreateCapabilities(
            IEnumerable<string> capabilities,
            string parameterName)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            var copy = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capability in capabilities)
            {
                if (capability == null)
                {
                    throw new ArgumentException(
                        "Capabilities must not contain null.",
                        parameterName);
                }

                RequireIdentifier(capability, parameterName);
                if (capability.Length > ProtocolLimits.MaxCapabilityChars)
                {
                    throw new ArgumentException(
                        "Capability names must not exceed "
                        + ProtocolLimits.MaxCapabilityChars.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        + " characters.",
                        parameterName);
                }

                if (!seen.Add(capability))
                {
                    throw new ArgumentException(
                        "Capability names must be unique.",
                        parameterName);
                }

                copy.Add(capability);
            }

            if (copy.Count > ProtocolLimits.MaxCapabilities)
            {
                throw new ArgumentException(
                    "The capability set must not exceed "
                    + ProtocolLimits.MaxCapabilities.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    + " entries.",
                    parameterName);
            }

            copy.Sort(StringComparer.Ordinal);
            return new ReadOnlyCollection<string>(copy);
        }

        // Opaque payload text (command arguments, registry snapshots) must be a
        // standalone JSON object within the depth budget left by its nesting
        // offset inside the envelope, so the encoded envelope always stays within
        // ProtocolLimits.MaxJsonDepth end to end.
        public static void RequireJsonObject(string json, int maxDepth, string parameterName)
        {
            if (json == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions { MaxDepth = maxDepth });
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    "The value must be a standalone JSON object within the depth budget.",
                    parameterName,
                    exception);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException(
                        "The value must be a standalone JSON object within the depth budget.",
                        parameterName);
                }
            }
        }

        public static bool SequenceEqual<T>(
            IReadOnlyList<T> left,
            IReadOnlyList<T> right,
            IEqualityComparer<T>? comparer = null)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            comparer = comparer ?? EqualityComparer<T>.Default;
            for (var index = 0; index < left.Count; index++)
            {
                if (!comparer.Equals(left[index], right[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
