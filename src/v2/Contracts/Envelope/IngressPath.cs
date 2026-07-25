using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// "Through which entry path?" — one of the four orthogonal identity-envelope
    /// fields (semantic-model.md §6). The vocabulary is open; the well-known values
    /// cover the spec's example paths.
    /// </summary>
    public readonly struct IngressPath : IEquatable<IngressPath>
    {
        private readonly string? value;

        public IngressPath(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        public static IngressPath PhysicalInput => new IngressPath("PhysicalInput");

        public static IngressPath Accessibility => new IngressPath("Accessibility");

        public static IngressPath Mcp => new IngressPath("Mcp");

        public static IngressPath Replay => new IngressPath("Replay");

        public static IngressPath InProcessApi => new IngressPath("InProcessApi");

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default IngressPath carries no value.");

        public bool Equals(IngressPath other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is IngressPath other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(IngressPath left, IngressPath right) => left.Equals(right);

        public static bool operator !=(IngressPath left, IngressPath right) => !left.Equals(right);
    }
}
