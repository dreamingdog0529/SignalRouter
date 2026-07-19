using System;
using System.Collections.Generic;

namespace SignalRouter
{
    internal static class InteractionContract
    {
        public static void RequireTargetId(string value, string parameterName)
        {
            RequireIdentifier(value, parameterName);
        }

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

        public static void RequireOptionalIdentifier(string? value, string parameterName)
        {
            if (value != null)
            {
                RequireIdentifier(value, parameterName);
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

        public static int CombineHashCodes(int first, int second, int third)
        {
            return CombineHashCodes(CombineHashCodes(first, second), third);
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

        public static int GetSequenceHashCode<T>(
            IReadOnlyList<T> values,
            IEqualityComparer<T>? comparer = null)
        {
            comparer = comparer ?? EqualityComparer<T>.Default;
            unchecked
            {
                var hash = 17;
                for (var index = 0; index < values.Count; index++)
                {
                    hash = (hash * 31) + comparer.GetHashCode(values[index]);
                }

                return hash;
            }
        }
    }
}
