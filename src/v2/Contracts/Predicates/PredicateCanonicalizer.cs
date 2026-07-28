using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// Derives the operand digest and semantic fingerprint of a registered
    /// predicate definition (guarantees.md §5.6; ADR 0015). Waits arm registered
    /// contracts only, so the digest identifies the pinned definition — replay
    /// resolves the same contract from its allowlisted catalog and verifies the
    /// digest matches. Secret operands contribute their reference identifier,
    /// never a value (they are references by construction). Deterministic by
    /// construction: length-framed segments, kind tags, invariant renderings.
    /// </summary>
    public static class PredicateCanonicalizer
    {
        private const char FieldSeparator = '\u001f';
        private const char RecordSeparator = '\u001e';

        public static ArgumentDigest DigestOf(PredicateDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            var builder = new StringBuilder();
            for (var i = 0; i < definition.Clauses.Count; i++)
            {
                var clause = definition.Clauses[i];
                AppendFramed(builder, clause.Id.Value);
                builder.Append(FieldSeparator);
                AppendExpression(builder, clause.Expression);
                builder.Append(RecordSeparator);
            }

            return new ArgumentDigest(Sha256Hex(builder.ToString()));
        }

        /// <summary>The armed wait's fingerprint: the contract reference plus the definition digest.</summary>
        public static SemanticFingerprint FingerprintOf(
            PredicateContractRef predicate, ArgumentDigest operands)
        {
            if (predicate.IsDefault)
            {
                throw new ArgumentException(
                    "A non-default predicate reference is required.", nameof(predicate));
            }

            if (operands.IsDefault)
            {
                throw new ArgumentException(
                    "A non-default operand digest is required.", nameof(operands));
            }

            var builder = new StringBuilder();
            builder.Append("predicate").Append(FieldSeparator);
            AppendFramed(builder, predicate.Id.Value);
            builder.Append(FieldSeparator)
                .Append(predicate.Version.Major.ToString(CultureInfo.InvariantCulture)).Append('.')
                .Append(predicate.Version.Minor.ToString(CultureInfo.InvariantCulture))
                .Append(RecordSeparator)
                .Append("operands").Append(FieldSeparator).Append(operands.Value);
            return new SemanticFingerprint(Sha256Hex(builder.ToString()));
        }

        private static void AppendExpression(StringBuilder builder, PredicateExpression expression)
        {
            switch (expression)
            {
                case ExistsExpression exists:
                    builder.Append("exists").Append(FieldSeparator);
                    AppendFramed(builder, exists.Path.Value);
                    break;
                case ComparisonExpression comparison:
                    builder.Append("cmp").Append(FieldSeparator);
                    AppendFramed(builder, comparison.Path.Value);
                    builder.Append(FieldSeparator).Append(OperatorTag(comparison.Operator))
                        .Append(FieldSeparator);
                    AppendOperand(builder, comparison.Operand);
                    break;
                case StringMatchExpression match:
                    builder.Append("match").Append(FieldSeparator);
                    AppendFramed(builder, match.Path.Value);
                    builder.Append(FieldSeparator)
                        .Append(match.Match switch
                        {
                            StringMatchKind.Contains => "contains",
                            StringMatchKind.Prefix => "prefix",
                            _ => "suffix",
                        })
                        .Append(FieldSeparator);
                    AppendOperand(builder, match.Operand);
                    break;
                case CountExpression count:
                    builder.Append("count").Append(FieldSeparator);
                    AppendFramed(builder, count.Path.Value);
                    builder.Append(FieldSeparator).Append(OperatorTag(count.Operator))
                        .Append(FieldSeparator)
                        .Append(count.Operand.ToString(CultureInfo.InvariantCulture));
                    break;
                case BooleanExpression boolean:
                    builder.Append(boolean.Operator == BooleanOperator.And ? "and" : "or")
                        .Append(FieldSeparator)
                        .Append(boolean.Operands.Count.ToString(CultureInfo.InvariantCulture));
                    for (var i = 0; i < boolean.Operands.Count; i++)
                    {
                        builder.Append(FieldSeparator);
                        AppendExpression(builder, boolean.Operands[i]);
                    }

                    break;
                case NotExpression not:
                    builder.Append("not").Append(FieldSeparator);
                    AppendExpression(builder, not.Operand);
                    break;
                default:
                    throw new ArgumentException(
                        "Unknown predicate expression kind.", nameof(expression));
            }
        }

        private static void AppendOperand(StringBuilder builder, PredicateOperand operand)
        {
            if (operand.Kind == PredicateOperandKind.SecretReference)
            {
                builder.Append("secret").Append(FieldSeparator);
                AppendFramed(builder, operand.Secret.Value);
                return;
            }

            builder.Append(operand.Kind switch
            {
                PredicateOperandKind.String => "s",
                PredicateOperandKind.Integer => "i",
                PredicateOperandKind.Boolean => "b",
                _ => "f",
            }).Append(FieldSeparator);
            AppendFramed(builder, operand.Kind == PredicateOperandKind.Float
                ? BitConverter.DoubleToInt64Bits(operand.Literal.AsFloat)
                    .ToString("x16", CultureInfo.InvariantCulture)
                : operand.Literal.ToString());
        }

        private static string OperatorTag(ComparisonOperator value) => value switch
        {
            ComparisonOperator.Eq => "eq",
            ComparisonOperator.Ne => "ne",
            ComparisonOperator.Lt => "lt",
            ComparisonOperator.Le => "le",
            ComparisonOperator.Gt => "gt",
            _ => "ge",
        };

        private static void AppendFramed(StringBuilder builder, string text)
        {
            builder.Append(text.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(text);
        }

        private static string Sha256Hex(string material)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            const string hex = "0123456789abcdef";
            var characters = new char[hash.Length * 2];
            for (var i = 0; i < hash.Length; i++)
            {
                characters[i * 2] = hex[hash[i] >> 4];
                characters[(i * 2) + 1] = hex[hash[i] & 0x0F];
            }

            return new string(characters);
        }
    }
}
