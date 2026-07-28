using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// Why a recording artifact's contract was not fully met (guarantees.md §3.2,
    /// §3.5). Open vocabulary; canonical codes reserved below.
    /// </summary>
    public readonly struct IncompleteReason : IEquatable<IncompleteReason>
    {
        private readonly string? value;

        public IncompleteReason(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        /// <summary>The recording reached a configured capacity bound (guarantees.md §8).</summary>
        public static IncompleteReason SizeLimit => new IncompleteReason("SizeLimit");

        /// <summary>An E5 barrier terminated the artifact per the open policy (guarantees.md §5.5).</summary>
        public static IncompleteReason ExternalMutation => new IncompleteReason("ExternalMutation");

        /// <summary>The recording sink faulted while the artifact was still writable (guarantees.md §7).</summary>
        public static IncompleteReason SinkFault => new IncompleteReason("SinkFault");

        /// <summary>A contract was registered during the active recording (guarantees.md §5.1).</summary>
        public static IncompleteReason ContractChanged => new IncompleteReason("ContractChanged");

        /// <summary>A permitted invocation resolved to a keyless node under the strict open policy (guarantees.md §5.2).</summary>
        public static IncompleteReason UnkeyedTarget => new IncompleteReason("UnkeyedTarget");

        /// <summary>The runtime incarnation changed while the artifact was writable (guarantees.md §7).</summary>
        public static IncompleteReason IncarnationChanged => new IncompleteReason("IncarnationChanged");

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default IncompleteReason carries no value.");

        /// <summary>True when this is one of the guarantees.md §3.5 reserved codes.</summary>
        public bool IsCanonical =>
            Equals(SizeLimit) || Equals(ExternalMutation) || Equals(SinkFault) ||
            Equals(ContractChanged) || Equals(UnkeyedTarget) || Equals(IncarnationChanged);

        public bool Equals(IncompleteReason other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is IncompleteReason other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(IncompleteReason left, IncompleteReason right) => left.Equals(right);

        public static bool operator !=(IncompleteReason left, IncompleteReason right) => !left.Equals(right);
    }
}
