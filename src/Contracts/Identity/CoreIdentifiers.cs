using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// One submitted request, assigned by the caller before dispatch; deduplicated
    /// within an incarnation plus retention window (semantic-model.md §4).
    /// </summary>
    public readonly struct RequestId : IEquatable<RequestId>
    {
        private readonly string? value;

        public RequestId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default RequestId carries no value.");

        public bool Equals(RequestId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is RequestId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(RequestId left, RequestId right) => left.Equals(right);

        public static bool operator !=(RequestId left, RequestId right) => !left.Equals(right);
    }

    /// <summary>
    /// A long-running operation (wait, recording, replay); lives until resolved plus
    /// retention (semantic-model.md §4).
    /// </summary>
    public readonly struct OperationId : IEquatable<OperationId>
    {
        private readonly string? value;

        public OperationId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default OperationId carries no value.");

        public bool Equals(OperationId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is OperationId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(OperationId left, OperationId right) => left.Equals(right);

        public static bool operator !=(OperationId left, OperationId right) => !left.Equals(right);
    }

    /// <summary>
    /// One live runtime instance; the namespace of <see cref="NodeRef"/>s and request
    /// identity. A domain reload creates a new incarnation (semantic-model.md §4).
    /// </summary>
    public readonly struct RuntimeIncarnationId : IEquatable<RuntimeIncarnationId>
    {
        private readonly string? value;

        public RuntimeIncarnationId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default RuntimeIncarnationId carries no value.");

        public bool Equals(RuntimeIncarnationId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is RuntimeIncarnationId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(RuntimeIncarnationId left, RuntimeIncarnationId right) => left.Equals(right);

        public static bool operator !=(RuntimeIncarnationId left, RuntimeIncarnationId right) => !left.Equals(right);
    }

    /// <summary>
    /// The application-author-assigned persistent identity — the only identity that
    /// persists across incarnations. Compared ordinally, never normalized
    /// (semantic-model.md §3.2).
    /// </summary>
    public readonly struct AuthorKey : IEquatable<AuthorKey>
    {
        private readonly string? value;

        public AuthorKey(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default AuthorKey carries no value.");

        public bool Equals(AuthorKey other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is AuthorKey other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(AuthorKey left, AuthorKey right) => left.Equals(right);

        public static bool operator !=(AuthorKey left, AuthorKey right) => !left.Equals(right);
    }

    /// <summary>
    /// The stable, ordinal-compared identity of one registered domain state source;
    /// persists across incarnations (semantic-model.md §8).
    /// </summary>
    public readonly struct StateSourceKey : IEquatable<StateSourceKey>
    {
        private readonly string? value;

        public StateSourceKey(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default StateSourceKey carries no value.");

        public bool Equals(StateSourceKey other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is StateSourceKey other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(StateSourceKey left, StateSourceKey right) => left.Equals(right);

        public static bool operator !=(StateSourceKey left, StateSourceKey right) => !left.Equals(right);
    }
}
