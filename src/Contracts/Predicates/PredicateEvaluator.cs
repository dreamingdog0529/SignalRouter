using System;
using System.Collections.Generic;

namespace SignalRouter.Contracts
{
    /// <summary>The answer of one predicate evaluation, with the per-clause report.</summary>
    public sealed class PredicateEvaluationResult
    {
        public PredicateEvaluationResult(PredicateEvaluationOutcome outcome, ValueArray<ClauseEvaluation> clauses)
        {
            Outcome = outcome;
            Clauses = clauses;
        }

        public PredicateEvaluationOutcome Outcome { get; }

        /// <summary>Per-clause expected/actual reports — diagnostic material, never comparison input (guarantees.md §5.10).</summary>
        public ValueArray<ClauseEvaluation> Clauses { get; }
    }

    /// <summary>
    /// The pure, snapshot-local predicate evaluator (verification.md §2.2–§2.3): a
    /// deterministic function of the definition, the lookup, and the bounds — no
    /// clock, randomness, or ambient input. Composition is three-valued
    /// (verification.md §2.3): a hidden value never becomes <c>False</c>, and an
    /// evaluable sibling that determines the result short-circuits an
    /// <c>Unevaluable</c> one.
    /// </summary>
    public static class PredicateEvaluator
    {
        /// <summary>
        /// The non-canonical open reason answered when the evaluation step budget is
        /// exhausted (the per-evaluation cost bound of security-resources.md §5.1).
        /// </summary>
        public static UnevaluableReason EvaluationBudgetExceeded =>
            new UnevaluableReason("EvaluationBudgetExceeded");

        public static PredicateEvaluationResult Evaluate(
            PredicateDefinition definition,
            IObservationLookup lookup,
            PredicateStructuralBounds bounds)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (lookup == null)
            {
                throw new ArgumentNullException(nameof(lookup));
            }

            if (bounds.IsDefault)
            {
                throw new ArgumentException("Bounds must be non-default.", nameof(bounds));
            }

            var budget = new StepBudget(bounds.MaxEvaluationSteps);
            var clauseReports = new List<ClauseEvaluation>();
            var verdict = Verdict.True();
            foreach (var clause in definition.Clauses)
            {
                var clauseVerdict = EvaluateExpression(clause.Expression, lookup, budget);
                clauseReports.Add(new ClauseEvaluation(
                    clause.Id.Value, "true", clauseVerdict.Render()));
                verdict = Verdict.And(verdict, clauseVerdict);
            }

            return new PredicateEvaluationResult(verdict.ToOutcome(), ValueArray<ClauseEvaluation>.From(clauseReports));
        }

        private static Verdict EvaluateExpression(
            PredicateExpression expression, IObservationLookup lookup, StepBudget budget)
        {
            if (!budget.TrySpend())
            {
                return Verdict.Unevaluable(EvaluationBudgetExceeded);
            }

            switch (expression)
            {
                case ExistsExpression exists:
                {
                    var answer = lookup.Lookup(exists.Path);
                    switch (answer.Kind)
                    {
                        case FieldLookupKind.Present:
                            return Verdict.Of(answer.Value.Kind != FieldValueKind.Null);
                        case FieldLookupKind.Absent:
                            return Verdict.Of(false);
                        default:
                            return Verdict.Unevaluable(answer.ToUnevaluable());
                    }
                }

                case ComparisonExpression comparison:
                {
                    if (comparison.Operand.Kind == PredicateOperandKind.SecretReference)
                    {
                        return Verdict.Unevaluable(UnevaluableReason.Redacted);
                    }

                    var answer = lookup.Lookup(comparison.Path);
                    switch (answer.Kind)
                    {
                        case FieldLookupKind.Present:
                            return CompareValue(answer.Value, comparison.Operator, comparison.Operand.Literal);
                        case FieldLookupKind.Absent:
                            // An absent field holds no value: equality is false and
                            // inequality is true; ordering has nothing to order.
                            return Verdict.Of(comparison.Operator == ComparisonOperator.Ne);
                        default:
                            return Verdict.Unevaluable(answer.ToUnevaluable());
                    }
                }

                case StringMatchExpression match:
                {
                    if (match.Operand.Kind == PredicateOperandKind.SecretReference)
                    {
                        return Verdict.Unevaluable(UnevaluableReason.Redacted);
                    }

                    var answer = lookup.Lookup(match.Path);
                    switch (answer.Kind)
                    {
                        case FieldLookupKind.Present when answer.Value.Kind == FieldValueKind.String:
                        {
                            var subject = answer.Value.AsString;
                            var operand = match.Operand.Literal.AsString;
                            switch (match.Match)
                            {
                                case StringMatchKind.Prefix:
                                    return Verdict.Of(subject.StartsWith(operand, StringComparison.Ordinal));
                                case StringMatchKind.Suffix:
                                    return Verdict.Of(subject.EndsWith(operand, StringComparison.Ordinal));
                                case StringMatchKind.Contains:
                                    return Verdict.Of(subject.IndexOf(operand, StringComparison.Ordinal) >= 0);
                                default:
                                    throw new InvalidOperationException(
                                        "Unknown string-match kind; construction validates the allowlist.");
                            }
                        }

                        case FieldLookupKind.Present:
                        case FieldLookupKind.Absent:
                            return Verdict.Of(false);
                        default:
                            return Verdict.Unevaluable(answer.ToUnevaluable());
                    }
                }

                case CountExpression count:
                {
                    var answer = lookup.CountCollection(count.Path);
                    switch (answer.Kind)
                    {
                        case FieldLookupKind.Present:
                            return CompareOrdered(((long)answer.Count).CompareTo(count.Operand), count.Operator);
                        case FieldLookupKind.Absent:
                            return CompareOrdered(0L.CompareTo(count.Operand), count.Operator);
                        default:
                            return Verdict.Unevaluable(answer.ToUnevaluable());
                    }
                }

                case BooleanExpression boolean:
                {
                    var combined = boolean.Operator == BooleanOperator.And ? Verdict.True() : Verdict.False();
                    foreach (var operand in boolean.Operands)
                    {
                        var next = EvaluateExpression(operand, lookup, budget);
                        combined = boolean.Operator == BooleanOperator.And
                            ? Verdict.And(combined, next)
                            : Verdict.Or(combined, next);
                    }

                    return combined;
                }

                case NotExpression not:
                    return Verdict.Not(EvaluateExpression(not.Operand, lookup, budget));

                default:
                    throw new InvalidOperationException(
                        $"Unknown expression type {expression.GetType().Name}.");
            }
        }

        private static Verdict CompareValue(FieldValue value, ComparisonOperator @operator, FieldValue operand)
        {
            if (value.Kind == FieldValueKind.Null)
            {
                // Null holds no comparable value: it equals nothing and orders nowhere.
                return Verdict.Of(@operator == ComparisonOperator.Ne);
            }

            if (value.Kind != operand.Kind)
            {
                // The runtime value contradicts the declared schema the operand was
                // checked against — a contract-support condition, not falsity.
                return Verdict.Unevaluable(UnevaluableReason.UnsupportedContract);
            }

            switch (value.Kind)
            {
                case FieldValueKind.Boolean:
                    return @operator == ComparisonOperator.Eq
                        ? Verdict.Of(value.AsBoolean == operand.AsBoolean)
                        : Verdict.Of(value.AsBoolean != operand.AsBoolean);
                case FieldValueKind.String:
                    return CompareOrdered(
                        string.CompareOrdinal(value.AsString, operand.AsString), @operator);
                case FieldValueKind.Integer:
                    return CompareOrdered(value.AsInteger.CompareTo(operand.AsInteger), @operator);
                default:
                    return CompareOrdered(value.AsFloat.CompareTo(operand.AsFloat), @operator);
            }
        }

        private static Verdict CompareOrdered(int comparison, ComparisonOperator @operator)
        {
            switch (@operator)
            {
                case ComparisonOperator.Eq:
                    return Verdict.Of(comparison == 0);
                case ComparisonOperator.Ne:
                    return Verdict.Of(comparison != 0);
                case ComparisonOperator.Lt:
                    return Verdict.Of(comparison < 0);
                case ComparisonOperator.Le:
                    return Verdict.Of(comparison <= 0);
                case ComparisonOperator.Gt:
                    return Verdict.Of(comparison > 0);
                case ComparisonOperator.Ge:
                    return Verdict.Of(comparison >= 0);
                default:
                    throw new InvalidOperationException(
                        "Unknown comparison operator; construction validates the allowlist.");
            }
        }

        private sealed class StepBudget
        {
            private int remaining;

            internal StepBudget(int steps)
            {
                remaining = steps;
            }

            internal bool TrySpend()
            {
                if (remaining <= 0)
                {
                    return false;
                }

                remaining--;
                return true;
            }
        }

        /// <summary>
        /// The three-valued evaluation lattice (verification.md §2.3): True, False,
        /// or Unevaluable carrying the first reason in clause order. Composition is
        /// commutative; a determined sibling short-circuits an undetermined one.
        /// </summary>
        private readonly struct Verdict
        {
            private const int TrueState = 1;
            private const int FalseState = 0;
            private const int UnevaluableState = 2;

            private readonly int state;
            private readonly UnevaluableReason reason;

            private Verdict(int state, UnevaluableReason reason)
            {
                this.state = state;
                this.reason = reason;
            }

            internal static Verdict True() => new Verdict(TrueState, default);

            internal static Verdict False() => new Verdict(FalseState, default);

            internal static Verdict Of(bool value) => value ? True() : False();

            internal static Verdict Unevaluable(UnevaluableReason reason) =>
                new Verdict(UnevaluableState, reason);

            internal static Verdict And(Verdict left, Verdict right)
            {
                if (left.state == FalseState || right.state == FalseState)
                {
                    return False();
                }

                if (left.state == UnevaluableState)
                {
                    return left;
                }

                return right;
            }

            internal static Verdict Or(Verdict left, Verdict right)
            {
                if (left.state == TrueState || right.state == TrueState)
                {
                    return True();
                }

                if (left.state == UnevaluableState)
                {
                    return left;
                }

                return right;
            }

            internal static Verdict Not(Verdict operand)
            {
                if (operand.state == UnevaluableState)
                {
                    return operand;
                }

                return operand.state == TrueState ? False() : True();
            }

            internal string Render()
            {
                switch (state)
                {
                    case TrueState:
                        return "true";
                    case FalseState:
                        return "false";
                    default:
                        return $"unevaluable:{reason}";
                }
            }

            internal PredicateEvaluationOutcome ToOutcome()
            {
                switch (state)
                {
                    case TrueState:
                        return PredicateEvaluationOutcome.Satisfied;
                    case FalseState:
                        return PredicateEvaluationOutcome.False;
                    default:
                        return PredicateEvaluationOutcome.Unevaluable(reason);
                }
            }
        }
    }
}
