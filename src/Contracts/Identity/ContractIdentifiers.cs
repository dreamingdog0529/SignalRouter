using System;

namespace SignalRouter.Contracts
{
    /// <summary>Names one capability contract family (semantic-model.md §2.2).</summary>
    public readonly struct CapabilityContractId : IEquatable<CapabilityContractId>
    {
        private readonly string? value;

        public CapabilityContractId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default CapabilityContractId carries no value.");

        public bool Equals(CapabilityContractId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is CapabilityContractId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(CapabilityContractId left, CapabilityContractId right) => left.Equals(right);

        public static bool operator !=(CapabilityContractId left, CapabilityContractId right) => !left.Equals(right);
    }

    /// <summary>Names one completion profile (semantic-model.md §2.2, adapter-conformance.md §4).</summary>
    public readonly struct CompletionProfileId : IEquatable<CompletionProfileId>
    {
        private readonly string? value;

        public CompletionProfileId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default CompletionProfileId carries no value.");

        public bool Equals(CompletionProfileId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is CompletionProfileId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(CompletionProfileId left, CompletionProfileId right) => left.Equals(right);

        public static bool operator !=(CompletionProfileId left, CompletionProfileId right) => !left.Equals(right);
    }

    /// <summary>Names the projection rules producing an observation view (observation-state.md §1).</summary>
    public readonly struct ViewContractId : IEquatable<ViewContractId>
    {
        private readonly string? value;

        public ViewContractId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default ViewContractId carries no value.");

        public bool Equals(ViewContractId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ViewContractId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(ViewContractId left, ViewContractId right) => left.Equals(right);

        public static bool operator !=(ViewContractId left, ViewContractId right) => !left.Equals(right);
    }

    /// <summary>Names the schema contract of a state-source document (semantic-model.md §8).</summary>
    public readonly struct StateSourceContractId : IEquatable<StateSourceContractId>
    {
        private readonly string? value;

        public StateSourceContractId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default StateSourceContractId carries no value.");

        public bool Equals(StateSourceContractId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is StateSourceContractId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(StateSourceContractId left, StateSourceContractId right) => left.Equals(right);

        public static bool operator !=(StateSourceContractId left, StateSourceContractId right) => !left.Equals(right);
    }

    /// <summary>Names one registered predicate contract (verification.md §2).</summary>
    public readonly struct PredicateContractId : IEquatable<PredicateContractId>
    {
        private readonly string? value;

        public PredicateContractId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default PredicateContractId carries no value.");

        public bool Equals(PredicateContractId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is PredicateContractId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(PredicateContractId left, PredicateContractId right) => left.Equals(right);

        public static bool operator !=(PredicateContractId left, PredicateContractId right) => !left.Equals(right);
    }

    /// <summary>Names one replay comparison profile (recording-replay.md §5).</summary>
    public readonly struct ReplayComparisonProfileId : IEquatable<ReplayComparisonProfileId>
    {
        private readonly string? value;

        public ReplayComparisonProfileId(string value)
        {
            this.value = ContractGrammar.ValidateIdentifier(value, nameof(value));
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default ReplayComparisonProfileId carries no value.");

        public bool Equals(ReplayComparisonProfileId other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ReplayComparisonProfileId other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(ReplayComparisonProfileId left, ReplayComparisonProfileId right) => left.Equals(right);

        public static bool operator !=(ReplayComparisonProfileId left, ReplayComparisonProfileId right) => !left.Equals(right);
    }
}
