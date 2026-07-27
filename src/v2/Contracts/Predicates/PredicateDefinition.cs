using System;
using System.Collections.Generic;

namespace SignalRouter.V2.Contracts
{
    /// <summary>One clause of a predicate: a stable ID and its expression.</summary>
    public sealed class PredicateClause
    {
        public PredicateClause(ClauseId id, PredicateExpression expression)
        {
            if (id.IsDefault)
            {
                throw new ArgumentException("A clause requires a non-default id.", nameof(id));
            }

            Id = id;
            Expression = expression ?? throw new ArgumentNullException(nameof(expression));
        }

        public ClauseId Id { get; }

        public PredicateExpression Expression { get; }
    }

    /// <summary>
    /// A predicate definition: an ordered, non-empty list of clauses with unique
    /// stable IDs. The predicate is satisfied exactly when every clause is satisfied
    /// (verification.md §2.2 — assertions state expected truth; negative
    /// expectations are written as predicates that evaluate true).
    /// </summary>
    public sealed class PredicateDefinition
    {
        public PredicateDefinition(ValueArray<PredicateClause> clauses)
        {
            if (clauses == null)
            {
                throw new ArgumentNullException(nameof(clauses));
            }

            if (clauses.Count == 0)
            {
                throw new ArgumentException("A predicate requires at least one clause.", nameof(clauses));
            }

            var seen = new HashSet<ClauseId>();
            foreach (var clause in clauses)
            {
                if (!seen.Add(clause.Id))
                {
                    throw new ArgumentException(
                        "Clause ids must be unique within a predicate.", nameof(clauses));
                }
            }

            Clauses = clauses;
        }

        public ValueArray<PredicateClause> Clauses { get; }

        /// <summary>Total AST node count across clauses (structural-bounds accounting).</summary>
        public int NodeCount
        {
            get
            {
                var count = 0;
                foreach (var clause in Clauses)
                {
                    count += clause.Expression.NodeCount;
                }

                return count;
            }
        }

        /// <summary>Deepest clause expression (structural-bounds accounting).</summary>
        public int Depth
        {
            get
            {
                var deepest = 0;
                foreach (var clause in Clauses)
                {
                    if (clause.Expression.Depth > deepest)
                    {
                        deepest = clause.Expression.Depth;
                    }
                }

                return deepest;
            }
        }
    }
}
