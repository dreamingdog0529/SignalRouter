using System;

namespace SignalRouter.Contracts
{
    /// <summary>The kind of a status-query answer (guarantees.md §3.4).</summary>
    public enum QueryAnswerKind
    {
        Pending,
        Terminal,
        RuntimeUnavailable,
        OutcomeUnknown,
    }

    /// <summary>
    /// A status-query answer. Fabricating a terminal for an unreachable runtime is
    /// prohibited (guarantees.md §3.4): only <see cref="Terminal"/> carries an
    /// interaction outcome, and there is no path from
    /// <see cref="QueryAnswerKind.RuntimeUnavailable"/> to one.
    /// </summary>
    public readonly struct QueryAnswer : IEquatable<QueryAnswer>
    {
        private readonly InteractionOutcome terminal;

        private QueryAnswer(QueryAnswerKind kind, InteractionOutcome terminal)
        {
            Kind = kind;
            this.terminal = terminal;
        }

        public static QueryAnswer Pending => new QueryAnswer(QueryAnswerKind.Pending, default);

        public static QueryAnswer RuntimeUnavailable => new QueryAnswer(QueryAnswerKind.RuntimeUnavailable, default);

        public static QueryAnswer OutcomeUnknown => new QueryAnswer(QueryAnswerKind.OutcomeUnknown, default);

        public static QueryAnswer Terminal(InteractionOutcome outcome)
        {
            if (outcome == InteractionOutcome.OutcomeUnknown)
            {
                throw new ArgumentException(
                    "A terminal answer never carries OutcomeUnknown; answer OutcomeUnknown itself instead.",
                    nameof(outcome));
            }

            return new QueryAnswer(QueryAnswerKind.Terminal, outcome);
        }

        public QueryAnswerKind Kind { get; }

        public InteractionOutcome TerminalOutcome =>
            Kind == QueryAnswerKind.Terminal
                ? terminal
                : throw new InvalidOperationException("Only a Terminal answer carries an interaction outcome.");

        public bool Equals(QueryAnswer other) => Kind == other.Kind && terminal == other.terminal;

        public override bool Equals(object? obj) => obj is QueryAnswer other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes((int)Kind, (int)terminal);

        public override string ToString() =>
            Kind == QueryAnswerKind.Terminal ? $"Terminal({terminal})" : Kind.ToString();

        public static bool operator ==(QueryAnswer left, QueryAnswer right) => left.Equals(right);

        public static bool operator !=(QueryAnswer left, QueryAnswer right) => !left.Equals(right);
    }
}
