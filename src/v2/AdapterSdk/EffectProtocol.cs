using System;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.AdapterSdk
{
    /// <summary>
    /// The kernel-minted, single-use permit gating one adapter effect
    /// (ADR 0010, guarantees.md §5.3). Opaque to adapters: equality and echo only.
    /// A token from a previous incarnation is stale and every message carrying it
    /// is rejected (adapter-conformance.md §3).
    /// </summary>
    public readonly struct EffectPermitToken : IEquatable<EffectPermitToken>
    {
        public EffectPermitToken(RequestId request, RuntimeIncarnationId incarnation, ulong nonce)
        {
            if (request.IsDefault)
            {
                throw new ArgumentException("Permit requires a non-default RequestId.", nameof(request));
            }

            if (incarnation.IsDefault)
            {
                throw new ArgumentException("Permit requires a non-default incarnation.", nameof(incarnation));
            }

            Request = request;
            Incarnation = incarnation;
            Nonce = nonce;
        }

        public RequestId Request { get; }

        public RuntimeIncarnationId Incarnation { get; }

        public ulong Nonce { get; }

        public bool IsDefault => Request.IsDefault;

        public bool Equals(EffectPermitToken other) =>
            Request.Equals(other.Request) &&
            Incarnation.Equals(other.Incarnation) &&
            Nonce == other.Nonce;

        public override bool Equals(object? obj) => obj is EffectPermitToken other && Equals(other);

        public override int GetHashCode()
        {
            var hash = Request.GetHashCode();
            hash = (hash * 397) ^ Incarnation.GetHashCode();
            return (hash * 397) ^ Nonce.GetHashCode();
        }

        public override string ToString() => IsDefault ? "(default)" : $"permit:{Request}#{Nonce}";

        public static bool operator ==(EffectPermitToken left, EffectPermitToken right) => left.Equals(right);

        public static bool operator !=(EffectPermitToken left, EffectPermitToken right) => !left.Equals(right);
    }

    /// <summary>
    /// One effect request handed to the executor after the permit evidence is
    /// ready. Carries the ephemeral typed payload — the sole protected path for
    /// sensitive argument values (security-resources.md §3).
    /// </summary>
    public sealed class EffectRequest
    {
        public EffectRequest(
            CapabilityInvocation invocation,
            InvocationPayload payload,
            NodeRef target,
            EffectPermitToken permit,
            CompletionProfileRef profile)
        {
            if (target.IsDefault)
            {
                throw new ArgumentException("Effect request requires a non-default target.", nameof(target));
            }

            if (permit.IsDefault)
            {
                throw new ArgumentException("Effect request requires a non-default permit.", nameof(permit));
            }

            if (profile.IsDefault)
            {
                throw new ArgumentException("Effect request requires a non-default profile.", nameof(profile));
            }

            Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
            Target = target;
            Permit = permit;
            Profile = profile;
        }

        public CapabilityInvocation Invocation { get; }

        /// <summary>Ephemeral (kernel-execution.md §3): never stored, lifetime ends at terminal.</summary>
        public InvocationPayload Payload { get; }

        public NodeRef Target { get; }

        public EffectPermitToken Permit { get; }

        public CompletionProfileRef Profile { get; }
    }

    /// <summary>
    /// The synchronous answer of <see cref="IEffectExecutor.Execute"/>: adopted, or
    /// refused with a stable fault code. No effect may begin before `Adopted` is
    /// returned (adapter-conformance.md §3) — that rule is what makes a refusal a
    /// zero-effect terminal.
    /// </summary>
    public readonly struct EffectAdoption : IEquatable<EffectAdoption>
    {
        private readonly FaultCode refusal;

        private EffectAdoption(bool adopted, FaultCode refusal)
        {
            IsAdopted = adopted;
            this.refusal = refusal;
        }

        public static EffectAdoption Adopted => new EffectAdoption(true, default);

        public static EffectAdoption Refused(FaultCode code)
        {
            if (code.IsDefault)
            {
                throw new ArgumentException("A refusal requires a stable fault code.", nameof(code));
            }

            return new EffectAdoption(false, code);
        }

        public bool IsAdopted { get; }

        public FaultCode RefusalCode =>
            !IsAdopted && !refusal.IsDefault
                ? refusal
                : throw new InvalidOperationException("Only a refusal carries a fault code.");

        public bool Equals(EffectAdoption other) =>
            IsAdopted == other.IsAdopted && refusal.Equals(other.refusal);

        public override bool Equals(object? obj) => obj is EffectAdoption other && Equals(other);

        public override int GetHashCode() =>
            ((IsAdopted ? 1 : 0) * 397) ^ refusal.GetHashCode();

        public override string ToString() => IsAdopted ? "Adopted" : $"Refused({refusal})";

        public static bool operator ==(EffectAdoption left, EffectAdoption right) => left.Equals(right);

        public static bool operator !=(EffectAdoption left, EffectAdoption right) => !left.Equals(right);
    }

    /// <summary>The kind of an effect resolution.</summary>
    public enum EffectResolutionKind
    {
        Succeeded,
        Faulted,
        Cancelled,
    }

    /// <summary>
    /// The terminal an adopted effect reports through its completion message
    /// (adapter-conformance.md §3): success with the profile's completion evidence,
    /// a fault with a stable code, or a cooperative cancellation with its phase and
    /// disposition. The kernel builds the full terminal evidence from this.
    /// </summary>
    public sealed class EffectResolution
    {
        private readonly CompletionEvidence? evidence;
        private readonly FaultCode faultCode;
        private readonly CancellationPhase cancellationPhase;
        private readonly string? cancellationDisposition;

        private EffectResolution(
            EffectResolutionKind kind,
            CompletionEvidence? evidence,
            FaultCode faultCode,
            CancellationPhase cancellationPhase,
            string? cancellationDisposition)
        {
            Kind = kind;
            this.evidence = evidence;
            this.faultCode = faultCode;
            this.cancellationPhase = cancellationPhase;
            this.cancellationDisposition = cancellationDisposition;
        }

        public static EffectResolution Succeeded(CompletionEvidence evidence)
        {
            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }

            return new EffectResolution(EffectResolutionKind.Succeeded, evidence, default, default, null);
        }

        public static EffectResolution Faulted(FaultCode code)
        {
            if (code.IsDefault)
            {
                throw new ArgumentException("A fault requires a stable code.", nameof(code));
            }

            return new EffectResolution(EffectResolutionKind.Faulted, null, code, default, null);
        }

        public static EffectResolution Cancelled(CancellationPhase phase, string disposition)
        {
            if (phase == CancellationPhase.BeforeEffect)
            {
                throw new ArgumentException(
                    "An adopted effect cannot cancel BeforeEffect; pre-permit cancellation never reaches the executor.",
                    nameof(phase));
            }

            return new EffectResolution(
                EffectResolutionKind.Cancelled, null, default, phase,
                ContractGrammar.ValidateCode(disposition, nameof(disposition)));
        }

        public EffectResolutionKind Kind { get; }

        public CompletionEvidence CompletionEvidence =>
            Kind == EffectResolutionKind.Succeeded
                ? evidence!
                : throw new InvalidOperationException("Only a success carries completion evidence.");

        public FaultCode FaultCode =>
            Kind == EffectResolutionKind.Faulted
                ? faultCode
                : throw new InvalidOperationException("Only a fault carries a fault code.");

        public CancellationPhase CancellationPhase =>
            Kind == EffectResolutionKind.Cancelled
                ? cancellationPhase
                : throw new InvalidOperationException("Only a cancellation carries a phase.");

        public string CancellationDisposition =>
            Kind == EffectResolutionKind.Cancelled
                ? cancellationDisposition!
                : throw new InvalidOperationException("Only a cancellation carries a disposition.");
    }

    /// <summary>
    /// One follow-up invocation an active effect commits (kernel-execution.md §9).
    /// The kernel records commitments in the parent's terminal and admits children
    /// only after that terminal is durable; ordinals follow list order. Like a
    /// submission, it names the capability and target — the kernel derives the
    /// child's fingerprint itself.
    /// </summary>
    public sealed class ContinuationRequest
    {
        public ContinuationRequest(CapabilityContractRef capability, TargetReference target, InvocationPayload payload)
        {
            if (capability.IsDefault)
            {
                throw new ArgumentException("Continuation requires a non-default capability.", nameof(capability));
            }

            if (target.IsDefault)
            {
                throw new ArgumentException("Continuation requires a non-default target.", nameof(target));
            }

            Capability = capability;
            Target = target;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        public CapabilityContractRef Capability { get; }

        public TargetReference Target { get; }

        /// <summary>Ephemeral, like every payload (kernel-execution.md §3).</summary>
        public InvocationPayload Payload { get; }
    }

    /// <summary>
    /// The completion message for an adopted permit — exactly once per token
    /// (adapter-conformance.md §3) — carrying the resolution and the ordered
    /// continuation declarations.
    /// </summary>
    public sealed class EffectCompletion
    {
        public EffectCompletion(
            EffectPermitToken permit,
            EffectResolution resolution,
            ValueList<ContinuationRequest>? continuations = null)
        {
            if (permit.IsDefault)
            {
                throw new ArgumentException("Completion requires a non-default permit.", nameof(permit));
            }

            Permit = permit;
            Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
            Continuations = continuations ?? ValueList<ContinuationRequest>.Empty;
        }

        public EffectPermitToken Permit { get; }

        public EffectResolution Resolution { get; }

        public ValueList<ContinuationRequest> Continuations { get; }
    }
}
