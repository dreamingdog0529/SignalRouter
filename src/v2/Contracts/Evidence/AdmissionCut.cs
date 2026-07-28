using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// E2 — one per admitted mutation interaction; durable before any UI effect of
    /// that interaction begins (guarantees.md §5.2). A keyless resolved target is
    /// representable — whether it refuses admission or closes the artifact
    /// <c>Incomplete(UnkeyedTarget)</c> is the recording's open policy, evaluated by
    /// the reader, not by this type.
    /// </summary>
    public sealed class AdmissionCut : EvidenceCut
    {
        public AdmissionCut(
            EvidenceSequence sequence,
            RequestId requestId,
            LogicalOrder logicalOrder,
            SemanticFingerprint fingerprint,
            CapabilityInvocation invocation,
            RecordedArguments arguments,
            ResolvedTarget resolvedTarget,
            IdentityEnvelope envelope)
            : base(sequence)
        {
            if (requestId.IsDefault)
            {
                throw new ArgumentException("E2 requires a non-default RequestId.", nameof(requestId));
            }

            if (fingerprint.IsDefault)
            {
                throw new ArgumentException("E2 requires a non-default fingerprint.", nameof(fingerprint));
            }

            if (resolvedTarget.IsDefault)
            {
                throw new ArgumentException("E2 requires a non-default resolved target.", nameof(resolvedTarget));
            }

            if (invocation == null)
            {
                throw new ArgumentNullException(nameof(invocation));
            }

            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            // The recorded form and the invocation's redacted digest are the same
            // identity; a cut whose two halves disagree would be unreplayable.
            if (!InvocationCanonicalizer.DigestOf(arguments).Equals(invocation.Arguments))
            {
                throw new ArgumentException(
                    "The recorded arguments do not re-digest to the invocation's argument digest.",
                    nameof(arguments));
            }

            RequestId = requestId;
            LogicalOrder = logicalOrder;
            Fingerprint = fingerprint;
            Invocation = invocation;
            Arguments = arguments;
            ResolvedTarget = resolvedTarget;
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        }

        public override EvidenceCutKind Kind => EvidenceCutKind.AdmissionCut;

        public RequestId RequestId { get; }

        public LogicalOrder LogicalOrder { get; }

        public SemanticFingerprint Fingerprint { get; }

        public CapabilityInvocation Invocation { get; }

        /// <summary>
        /// The portable replay input: the admitted arguments in recorded form
        /// (typed values; sensitive values as secret references). Re-digesting
        /// this form yields <see cref="CapabilityInvocation.Arguments"/> (ADR 0015).
        /// </summary>
        public RecordedArguments Arguments { get; }

        public ResolvedTarget ResolvedTarget { get; }

        public IdentityEnvelope Envelope { get; }
    }
}
