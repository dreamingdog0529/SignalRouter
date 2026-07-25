using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// "On whose authority?" — one of the four orthogonal identity-envelope fields
    /// (semantic-model.md §6). The kind vocabulary is open; well-known kinds cover the
    /// spec's example values.
    /// </summary>
    public sealed class Principal : IEquatable<Principal>
    {
        public Principal(string kind, string id)
        {
            Kind = ContractGrammar.ValidateCode(kind, nameof(kind));
            Id = ContractGrammar.ValidateIdentifier(id, nameof(id));
        }

        /// <summary>Well-known principal kinds (semantic-model.md §6 example values).</summary>
        public static class WellKnownKinds
        {
            public const string LocalUser = "LocalUser";
            public const string AgentSession = "AgentSession";
            public const string TestHarness = "TestHarness";
        }

        public string Kind { get; }

        public string Id { get; }

        public bool Equals(Principal? other) =>
            other != null &&
            string.Equals(Kind, other.Kind, StringComparison.Ordinal) &&
            string.Equals(Id, other.Id, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as Principal);

        public override int GetHashCode() => ContractGrammar.CombineHashes(
            StringComparer.Ordinal.GetHashCode(Kind), StringComparer.Ordinal.GetHashCode(Id));

        public override string ToString() => $"{Kind}:{Id}";
    }
}
