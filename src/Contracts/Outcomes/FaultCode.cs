using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// The stable application fault code of a <c>Faulted</c> terminal — never an
    /// exception type, message, or stack (guarantees.md §5.4). The vocabulary is open
    /// and application/adapter-defined; two codes are reserved (guarantees.md §3.5).
    /// </summary>
    public readonly struct FaultCode : IEquatable<FaultCode>
    {
        private readonly string? value;

        public FaultCode(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        /// <summary>A capability-contract postcondition terminated the interaction (verification.md §3.4).</summary>
        public static FaultCode CompletionPostconditionNotSatisfied => new FaultCode("CompletionPostconditionNotSatisfied");

        /// <summary>Pre-effect evidence failure: the E3 permit could not be made durable (guarantees.md §3.1).</summary>
        public static FaultCode EvidenceUnavailable => new FaultCode("EvidenceUnavailable");

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default FaultCode carries no value.");

        /// <summary>True when this is one of the guarantees.md §3.5 reserved codes.</summary>
        public bool IsReserved =>
            Equals(CompletionPostconditionNotSatisfied) || Equals(EvidenceUnavailable);

        public bool Equals(FaultCode other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is FaultCode other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(FaultCode left, FaultCode right) => left.Equals(right);

        public static bool operator !=(FaultCode left, FaultCode right) => !left.Equals(right);
    }
}
