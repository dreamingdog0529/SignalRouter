using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The reason of a <c>Rejected</c> interaction outcome. The vocabulary is open
    /// (guarantees.md §3.5, §10): the canonical codes below are reserved; unknown
    /// codes are presented verbatim and never branched on. Rejection causes the spec
    /// deliberately leaves unnamed (authorization refusal, capability unavailability,
    /// incarnation mismatch, …) receive codes together with the kernel's
    /// security-exposure review.
    /// </summary>
    public readonly struct RejectionReason : IEquatable<RejectionReason>
    {
        private readonly string? value;

        public RejectionReason(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        /// <summary>Duplicate <see cref="RequestId"/> with a different semantic fingerprint.</summary>
        public static RejectionReason RequestIdConflict => new RejectionReason("RequestIdConflict");

        /// <summary>Mutation-lane or RecoveryIndex capacity refused the admission (guarantees.md §8).</summary>
        public static RejectionReason CapacityExhausted => new RejectionReason("CapacityExhausted");

        /// <summary>Nested submission from inside an effect handler.</summary>
        public static RejectionReason ReentrantDispatch => new RejectionReason("ReentrantDispatch");

        /// <summary>
        /// The target cannot be resolved to exactly one node for this principal:
        /// unregistered, ambiguous, or not exposed — deliberately indistinguishable
        /// (existence concealment, guarantees.md §3.5).
        /// </summary>
        public static RejectionReason TargetNotFound => new RejectionReason("TargetNotFound");

        /// <summary>
        /// The node is visible but the capability is not invocable there: disabled,
        /// or not declared / not exposed — merged for the same concealment reason.
        /// </summary>
        public static RejectionReason CapabilityUnavailable => new RejectionReason("CapabilityUnavailable");

        /// <summary>A contract-declared validation precondition evaluated false during Validating.</summary>
        public static RejectionReason PreconditionFailed => new RejectionReason("PreconditionFailed");

        /// <summary>The submission references a stale RuntimeIncarnationId (kernel-execution.md §10).</summary>
        public static RejectionReason IncarnationMismatch => new RejectionReason("IncarnationMismatch");

        /// <summary>The strict open policy of an active recording refused a keyless target (guarantees.md §5.2).</summary>
        public static RejectionReason UnkeyedTarget => new RejectionReason("UnkeyedTarget");

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default RejectionReason carries no value.");

        /// <summary>True when this is one of the guarantees.md §3.5 reserved codes.</summary>
        public bool IsCanonical =>
            Equals(RequestIdConflict) || Equals(CapacityExhausted) || Equals(ReentrantDispatch) ||
            Equals(TargetNotFound) || Equals(CapabilityUnavailable) || Equals(PreconditionFailed) ||
            Equals(IncarnationMismatch) || Equals(UnkeyedTarget);

        public bool Equals(RejectionReason other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is RejectionReason other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(RejectionReason left, RejectionReason right) => left.Equals(right);

        public static bool operator !=(RejectionReason left, RejectionReason right) => !left.Equals(right);
    }
}
