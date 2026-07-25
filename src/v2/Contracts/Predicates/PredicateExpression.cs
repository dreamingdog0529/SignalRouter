using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>Comparison operators of the predicate allowlist (verification.md §2.2).</summary>
    public enum ComparisonOperator
    {
        Eq,
        Ne,
        Lt,
        Le,
        Gt,
        Ge,
    }

    /// <summary>String-matching operators of the predicate allowlist.</summary>
    public enum StringMatchKind
    {
        Prefix,
        Suffix,
        Contains,
    }

    /// <summary>Boolean composition operators.</summary>
    public enum BooleanOperator
    {
        And,
        Or,
    }

    /// <summary>
    /// The declarative predicate AST (verification.md §2.2). The hierarchy is exactly
    /// the allowlist — existence, typed comparison, string matching, counting over
    /// keyed collections, and boolean composition. Iteration, arithmetic,
    /// cross-snapshot references, and time are not representable. The AST is data;
    /// no code ever crosses the wire.
    /// </summary>
    public abstract class PredicateExpression
    {
        private protected PredicateExpression()
        {
        }

        /// <summary>Total node count of this subtree (structural-bounds accounting).</summary>
        public abstract int NodeCount { get; }

        /// <summary>Maximum depth of this subtree (structural-bounds accounting).</summary>
        public abstract int Depth { get; }
    }

    /// <summary>True when the field at <see cref="Path"/> is present with a non-null value.</summary>
    public sealed class ExistsExpression : PredicateExpression
    {
        public ExistsExpression(FieldPath path)
        {
            if (path.IsDefault)
            {
                throw new ArgumentException("Exists requires a non-default path.", nameof(path));
            }

            Path = path;
        }

        public FieldPath Path { get; }

        public override int NodeCount => 1;

        public override int Depth => 1;
    }

    /// <summary>Typed comparison of the field at <see cref="Path"/> against a literal operand.</summary>
    public sealed class ComparisonExpression : PredicateExpression
    {
        public ComparisonExpression(FieldPath path, ComparisonOperator @operator, PredicateOperand operand)
        {
            if (path.IsDefault)
            {
                throw new ArgumentException("Comparison requires a non-default path.", nameof(path));
            }

            if (@operator < ComparisonOperator.Eq || @operator > ComparisonOperator.Ge)
            {
                throw new ArgumentException(
                    "Unknown comparison operator; the allowlist is closed.", nameof(@operator));
            }

            if (operand.IsDefault)
            {
                throw new ArgumentException("Comparison requires a non-default operand.", nameof(operand));
            }

            if (operand.Kind == PredicateOperandKind.Boolean &&
                @operator != ComparisonOperator.Eq && @operator != ComparisonOperator.Ne)
            {
                throw new ArgumentException(
                    "Boolean operands support only equality comparison.", nameof(@operator));
            }

            Path = path;
            Operator = @operator;
            Operand = operand;
        }

        public FieldPath Path { get; }

        public ComparisonOperator Operator { get; }

        public PredicateOperand Operand { get; }

        public override int NodeCount => 1;

        public override int Depth => 1;
    }

    /// <summary>String prefix/suffix/containment match of the field at <see cref="Path"/>.</summary>
    public sealed class StringMatchExpression : PredicateExpression
    {
        public StringMatchExpression(FieldPath path, StringMatchKind match, PredicateOperand operand)
        {
            if (path.IsDefault)
            {
                throw new ArgumentException("StringMatch requires a non-default path.", nameof(path));
            }

            if (match < StringMatchKind.Prefix || match > StringMatchKind.Contains)
            {
                throw new ArgumentException(
                    "Unknown string-match kind; the allowlist is closed.", nameof(match));
            }

            if (operand.Kind != PredicateOperandKind.String &&
                operand.Kind != PredicateOperandKind.SecretReference)
            {
                throw new ArgumentException(
                    "StringMatch requires a string or secret operand.", nameof(operand));
            }

            Path = path;
            Match = match;
            Operand = operand;
        }

        public FieldPath Path { get; }

        public StringMatchKind Match { get; }

        public PredicateOperand Operand { get; }

        public override int NodeCount => 1;

        public override int Depth => 1;
    }

    /// <summary>Comparison of a keyed collection's element count at <see cref="Path"/>.</summary>
    public sealed class CountExpression : PredicateExpression
    {
        public CountExpression(FieldPath path, ComparisonOperator @operator, long operand)
        {
            if (path.IsDefault)
            {
                throw new ArgumentException("Count requires a non-default path.", nameof(path));
            }

            if (@operator < ComparisonOperator.Eq || @operator > ComparisonOperator.Ge)
            {
                throw new ArgumentException(
                    "Unknown comparison operator; the allowlist is closed.", nameof(@operator));
            }

            if (operand < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(operand), "Count operand must not be negative.");
            }

            Path = path;
            Operator = @operator;
            Operand = operand;
        }

        public FieldPath Path { get; }

        public ComparisonOperator Operator { get; }

        public long Operand { get; }

        public override int NodeCount => 1;

        public override int Depth => 1;
    }

    /// <summary>And/Or composition over two or more sub-expressions.</summary>
    public sealed class BooleanExpression : PredicateExpression
    {
        public BooleanExpression(BooleanOperator @operator, ValueList<PredicateExpression> operands)
        {
            if (operands == null)
            {
                throw new ArgumentNullException(nameof(operands));
            }

            if (operands.Count < 2)
            {
                throw new ArgumentException(
                    "Boolean composition requires at least two operands.", nameof(operands));
            }

            if (@operator < BooleanOperator.And || @operator > BooleanOperator.Or)
            {
                throw new ArgumentException(
                    "Unknown boolean operator; the allowlist is closed.", nameof(@operator));
            }

            Operator = @operator;
            Operands = operands;
        }

        public BooleanOperator Operator { get; }

        public ValueList<PredicateExpression> Operands { get; }

        public override int NodeCount
        {
            get
            {
                var count = 1;
                foreach (var operand in Operands)
                {
                    count += operand.NodeCount;
                }

                return count;
            }
        }

        public override int Depth
        {
            get
            {
                var deepest = 0;
                foreach (var operand in Operands)
                {
                    if (operand.Depth > deepest)
                    {
                        deepest = operand.Depth;
                    }
                }

                return deepest + 1;
            }
        }
    }

    /// <summary>Negation of one sub-expression.</summary>
    public sealed class NotExpression : PredicateExpression
    {
        public NotExpression(PredicateExpression operand)
        {
            Operand = operand ?? throw new ArgumentNullException(nameof(operand));
        }

        public PredicateExpression Operand { get; }

        public override int NodeCount => 1 + Operand.NodeCount;

        public override int Depth => 1 + Operand.Depth;
    }
}
