using System;
using System.Collections.Generic;

namespace SignalRouter.V2.Contracts
{
    /// <summary>Per-clause validation error kinds (verification.md §4).</summary>
    public enum PredicateValidationErrorKind
    {
        UnknownField,
        TypeMismatch,
        UnsupportedOperator,
        BoundViolation,
    }

    /// <summary>One validation error, anchored to its clause.</summary>
    public sealed class PredicateValidationError
    {
        public PredicateValidationError(ClauseId clause, PredicateValidationErrorKind kind, string description)
        {
            if (clause.IsDefault)
            {
                throw new ArgumentException("An error requires a non-default clause id.", nameof(clause));
            }

            Clause = clause;
            Kind = kind;
            Description = ContractGrammar.ValidateIdentifier(description, nameof(description));
        }

        public ClauseId Clause { get; }

        public PredicateValidationErrorKind Kind { get; }

        public string Description { get; }

        public override string ToString() => $"{Clause}: {Kind} ({Description})";
    }

    /// <summary>The answer of a validation run.</summary>
    public sealed class PredicateValidationResult
    {
        public PredicateValidationResult(ValueList<PredicateValidationError> errors)
        {
            Errors = errors ?? throw new ArgumentNullException(nameof(errors));
        }

        public bool IsValid => Errors.Count == 0;

        public ValueList<PredicateValidationError> Errors { get; }
    }

    /// <summary>
    /// Type-checks a predicate against a catalog without evaluating it
    /// (verification.md §4): unknown fields, type mismatches, unsupported operators,
    /// and structural-bound violations, reported per clause. Free of observation cost
    /// and side effects.
    /// </summary>
    public static class PredicateTypeChecker
    {
        public static PredicateValidationResult Check(
            PredicateDefinition definition,
            PredicateCatalog catalog,
            PredicateStructuralBounds bounds)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (bounds.IsDefault)
            {
                throw new ArgumentException("Bounds must be non-default.", nameof(bounds));
            }

            var errors = new List<PredicateValidationError>();
            if (definition.Depth > bounds.MaxDepth)
            {
                errors.Add(new PredicateValidationError(
                    definition.Clauses[0].Id,
                    PredicateValidationErrorKind.BoundViolation,
                    $"AST depth {definition.Depth} exceeds {bounds.MaxDepth}"));
            }

            if (definition.NodeCount > bounds.MaxNodeCount)
            {
                errors.Add(new PredicateValidationError(
                    definition.Clauses[0].Id,
                    PredicateValidationErrorKind.BoundViolation,
                    $"AST node count {definition.NodeCount} exceeds {bounds.MaxNodeCount}"));
            }

            foreach (var clause in definition.Clauses)
            {
                CheckExpression(clause.Id, clause.Expression, catalog, bounds, errors);
            }

            return new PredicateValidationResult(ValueList<PredicateValidationError>.From(errors));
        }

        private static void CheckExpression(
            ClauseId clause,
            PredicateExpression expression,
            PredicateCatalog catalog,
            PredicateStructuralBounds bounds,
            List<PredicateValidationError> errors)
        {
            switch (expression)
            {
                case ExistsExpression exists:
                    RequireKnown(clause, exists.Path, catalog, errors);
                    break;

                case ComparisonExpression comparison:
                {
                    if (!RequireKnown(clause, comparison.Path, catalog, errors, out var fieldType))
                    {
                        break;
                    }

                    if (fieldType == FieldType.KeyedCollection)
                    {
                        errors.Add(new PredicateValidationError(
                            clause, PredicateValidationErrorKind.UnsupportedOperator,
                            $"Collections are counted, not compared: {comparison.Path}"));
                        break;
                    }

                    CheckOperandLength(clause, comparison.Operand, bounds, errors);
                    var operandType = OperandFieldType(comparison.Operand);
                    if (operandType != null && operandType != fieldType)
                    {
                        errors.Add(new PredicateValidationError(
                            clause, PredicateValidationErrorKind.TypeMismatch,
                            $"Field {comparison.Path} is {fieldType}, operand is {operandType}"));
                    }

                    if (fieldType == FieldType.Boolean &&
                        comparison.Operator != ComparisonOperator.Eq &&
                        comparison.Operator != ComparisonOperator.Ne)
                    {
                        errors.Add(new PredicateValidationError(
                            clause, PredicateValidationErrorKind.UnsupportedOperator,
                            $"Boolean field {comparison.Path} supports only equality"));
                    }

                    break;
                }

                case StringMatchExpression match:
                {
                    if (!RequireKnown(clause, match.Path, catalog, errors, out var fieldType))
                    {
                        break;
                    }

                    CheckOperandLength(clause, match.Operand, bounds, errors);
                    if (fieldType != FieldType.String)
                    {
                        errors.Add(new PredicateValidationError(
                            clause, PredicateValidationErrorKind.TypeMismatch,
                            $"String match requires a string field: {match.Path} is {fieldType}"));
                    }

                    break;
                }

                case CountExpression count:
                {
                    if (!RequireKnown(clause, count.Path, catalog, errors, out var fieldType))
                    {
                        break;
                    }

                    if (fieldType != FieldType.KeyedCollection)
                    {
                        errors.Add(new PredicateValidationError(
                            clause, PredicateValidationErrorKind.UnsupportedOperator,
                            $"Count requires a keyed collection: {count.Path} is {fieldType}"));
                    }

                    break;
                }

                case BooleanExpression boolean:
                    foreach (var operand in boolean.Operands)
                    {
                        CheckExpression(clause, operand, catalog, bounds, errors);
                    }

                    break;

                case NotExpression not:
                    CheckExpression(clause, not.Operand, catalog, bounds, errors);
                    break;

                default:
                    errors.Add(new PredicateValidationError(
                        clause, PredicateValidationErrorKind.UnsupportedOperator,
                        $"Unknown expression type {expression.GetType().Name}"));
                    break;
            }
        }

        private static bool RequireKnown(
            ClauseId clause, FieldPath path, PredicateCatalog catalog, List<PredicateValidationError> errors)
        {
            return RequireKnown(clause, path, catalog, errors, out _);
        }

        private static bool RequireKnown(
            ClauseId clause,
            FieldPath path,
            PredicateCatalog catalog,
            List<PredicateValidationError> errors,
            out FieldType type)
        {
            if (catalog.TryGetType(path, out type))
            {
                return true;
            }

            errors.Add(new PredicateValidationError(
                clause, PredicateValidationErrorKind.UnknownField, $"Unknown field {path}"));
            return false;
        }

        private static void CheckOperandLength(
            ClauseId clause,
            PredicateOperand operand,
            PredicateStructuralBounds bounds,
            List<PredicateValidationError> errors)
        {
            if (operand.Kind == PredicateOperandKind.String &&
                operand.Literal.AsString.Length > bounds.MaxOperandLength)
            {
                errors.Add(new PredicateValidationError(
                    clause, PredicateValidationErrorKind.BoundViolation,
                    $"Operand length exceeds {bounds.MaxOperandLength} UTF-16 code units"));
            }
        }

        private static FieldType? OperandFieldType(PredicateOperand operand)
        {
            switch (operand.Kind)
            {
                case PredicateOperandKind.String:
                    return FieldType.String;
                case PredicateOperandKind.Integer:
                    return FieldType.Integer;
                case PredicateOperandKind.Boolean:
                    return FieldType.Boolean;
                case PredicateOperandKind.Float:
                    return FieldType.Float;
                default:
                    return null; // secret references type-check at resolution
            }
        }
    }
}
