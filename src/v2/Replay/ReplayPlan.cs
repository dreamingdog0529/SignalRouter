using System;
using SignalRouter.V2.Codec.Recording;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Replay
{
    /// <summary>How the caller vouches for the artifact's origin (security-resources.md §6).</summary>
    public enum ArtifactProvenance
    {
        Untrusted = 0,

        Trusted = 1,
    }

    /// <summary>
    /// The trust-boundary options (recording-replay.md §7): artifacts from
    /// untrusted sources are refused by default; accepting one is an explicit
    /// opt-in, never an inference.
    /// </summary>
    public sealed class ReplayTrustOptions
    {
        public ReplayTrustOptions(
            ArtifactProvenance provenance,
            bool acceptUntrustedArtifacts = false)
        {
            if (provenance != ArtifactProvenance.Trusted && provenance != ArtifactProvenance.Untrusted)
            {
                // Fail closed: an undefined provenance (deserialized
                // configuration) must never ride the trusted path.
                throw new ArgumentOutOfRangeException(nameof(provenance));
            }

            Provenance = provenance;
            AcceptUntrustedArtifacts = acceptUntrustedArtifacts;
        }

        public ArtifactProvenance Provenance { get; }

        public bool AcceptUntrustedArtifacts { get; }
    }

    /// <summary>One allowlisted predicate: the reference with the definition the target runtime registered.</summary>
    public sealed class PredicateAllowlistEntry
    {
        public PredicateAllowlistEntry(PredicateContractRef reference, PredicateDefinition definition)
        {
            if (reference.IsDefault)
            {
                throw new ArgumentException(
                    "An allowlist entry requires a non-default reference.", nameof(reference));
            }

            Reference = reference;
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        }

        public PredicateContractRef Reference { get; }

        public PredicateDefinition Definition { get; }
    }

    /// <summary>
    /// The target runtime's registered contract surface — the only contracts an
    /// artifact may execute or evaluate against (recording-replay.md §7). The
    /// supported profile document is the replayer's own registration; the
    /// artifact's embedded document resolves against it.
    /// </summary>
    public sealed class ReplayAllowlist
    {
        public ReplayAllowlist(
            ValueArray<CompletionBinding> capabilities,
            ValueArray<StateSourceBinding> stateSources,
            ValueArray<PredicateAllowlistEntry> predicates,
            ReplayComparisonProfile supportedProfile)
        {
            for (var index = 0; index < predicates.Count; index++)
            {
                if (predicates[index] == null)
                {
                    throw new ArgumentException(
                        "Predicate allowlist entries must be non-null.", nameof(predicates));
                }
            }

            Capabilities = capabilities;
            StateSources = stateSources;
            Predicates = predicates;
            SupportedProfile = supportedProfile ??
                throw new ArgumentNullException(nameof(supportedProfile));
        }

        public ValueArray<CompletionBinding> Capabilities { get; }

        public ValueArray<StateSourceBinding> StateSources { get; }

        public ValueArray<PredicateAllowlistEntry> Predicates { get; }

        public ReplayComparisonProfile SupportedProfile { get; }
    }

    /// <summary>The reserved refusal codes of the trust boundary (recording-replay.md §7).</summary>
    public static class ReplayRefusalCodes
    {
        public const string UntrustedProvenance = "UntrustedProvenance";

        public const string ResourceLimit = "ResourceLimit";

        /// <summary>
        /// Integrity in the §7 sense: byte/digest verification, recomputable
        /// closure, and the reader's structural rules — a violating artifact is
        /// never execution input, not a partially replayable one.
        /// </summary>
        public const string ArtifactIntegrity = "ArtifactIntegrity";

        /// <summary>No artifact exists (E1 or its base snapshot never became durable).</summary>
        public const string OpenFailed = "OpenFailed";

        public const string ContractAllowlist = "ContractAllowlist";

        public const string PredicateDigestMismatch = "PredicateDigestMismatch";
    }

    /// <summary>One trust-boundary refusal: a stable code, never exception internals.</summary>
    public sealed class ReplayRefusal
    {
        public ReplayRefusal(string code)
        {
            Code = ContractGrammar.ValidateCode(code, nameof(code));
        }

        public string Code { get; }
    }

    /// <summary>
    /// The §6.1 evidence shape of a replay entry. The shape names what the
    /// evidence contains, never whether execution proceeds — execution
    /// eligibility has exactly one authority, <see cref="ReplayPlan.Stop"/>;
    /// a Completed shape can still sit at or beyond a planned stop.
    /// </summary>
    public enum ReplayEntryKind
    {
        /// <summary>E2 + E3 + E4: re-admit, permit, execute, compare.</summary>
        Completed,

        /// <summary>
        /// E2 + E4 without E3 — a rejection, or any pre-effect terminal such as
        /// Faulted(EvidenceUnavailable): re-dispatch to verify the same terminal
        /// and the zero-effect guarantee. The driver compares the recorded
        /// terminal itself and never branches on this shape alone.
        /// </summary>
        Rejected,

        /// <summary>A BeforeEffect cancellation (no permit): replayed with a synthetic pre-cancelled token.</summary>
        PreCancelled,

        /// <summary>
        /// E2 + a Faulted terminal without E3 — a pre-effect infrastructure
        /// failure (e.g. Faulted(EvidenceUnavailable)): NOT re-dispatched. A
        /// healthy replay environment would not fault, so re-dispatch would
        /// perform the effect the live run never permitted; strict replay stops
        /// before this entry instead.
        /// </summary>
        PreEffectFault,

        /// <summary>E2 + E3 without E4: strict replay stops before this effect.</summary>
        OutcomeUnknown,

        /// <summary>E2 alone: admitted, never permitted, no terminal — nothing to execute.</summary>
        AdmittedOnly,
    }

    /// <summary>One interaction's replay entry: its evidence chain in admission order.</summary>
    public sealed class ReplayEntry
    {
        public ReplayEntry(
            RequestId request,
            ReplayEntryKind kind,
            AdmissionCut admission,
            EffectPermit? permit,
            TerminalCut? terminal)
        {
            Request = request;
            Kind = kind;
            Admission = admission ?? throw new ArgumentNullException(nameof(admission));
            Permit = permit;
            Terminal = terminal;
        }

        public RequestId Request { get; }

        public ReplayEntryKind Kind { get; }

        public AdmissionCut Admission { get; }

        public EffectPermit? Permit { get; }

        public TerminalCut? Terminal { get; }
    }

    /// <summary>Why a planned stop exists at its position.</summary>
    public enum ReplayStopKind
    {
        /// <summary>A contaminated effect, or any execution-bearing position beyond the first barrier (guarantees.md §5.5).</summary>
        Contamination,

        /// <summary>An E6 resolution of TimedOut, Cancelled, or Unknown — timing is out of tier (guarantees.md §5.6, §4).</summary>
        WaitTiming,

        /// <summary>An E6 resolution of Faulted (guarantees.md §5.6).</summary>
        PredicateFault,

        /// <summary>A DuringEffect cancellation or a Cancelled AfterEffect terminal (guarantees.md §5.7).</summary>
        CancellationTiming,

        /// <summary>An E2+E3 shape without E4 (guarantees.md §6.1, §7).</summary>
        OutcomeUnknown,

        /// <summary>A pre-effect infrastructure fault: re-dispatch could perform the unpermitted effect.</summary>
        PreEffectFault,

        /// <summary>A recorded E8 outcome of Unevaluable (guarantees.md §5.10).</summary>
        RecordedUnevaluable,

        /// <summary>A secret reference the resolver cannot answer (recording-replay.md §7).</summary>
        SecretUnresolvable,
    }

    /// <summary>
    /// The earliest planned stop: strict replay executes and compares up to
    /// <see cref="Position"/> and stops there. <see cref="Incomparability"/> is
    /// the reason the stop reports when the spec names one; plain
    /// stop-before-executing stops carry none.
    /// </summary>
    public sealed class PlannedStop
    {
        public PlannedStop(
            EvidenceSequence position, ReplayStopKind kind, IncomparableReason? incomparability)
        {
            Position = position;
            Kind = kind;
            Incomparability = incomparability;
        }

        public EvidenceSequence Position { get; }

        public ReplayStopKind Kind { get; }

        public IncomparableReason? Incomparability { get; }
    }

    /// <summary>
    /// The executable plan a pre-scan produces: the E1 bootstrap material, the
    /// effective profile, the reader-authoritative classification, the entries
    /// in admission order, and the earliest planned stop.
    /// </summary>
    public sealed class ReplayPlan
    {
        public ReplayPlan(
            RecordingOpened opened,
            ReplayComparisonProfile profile,
            ArtifactClassification classification,
            ValueArray<ReplayEntry> entries,
            PlannedStop? stop,
            ArtifactReadResult reading)
        {
            Opened = opened ?? throw new ArgumentNullException(nameof(opened));
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            Classification = classification ?? throw new ArgumentNullException(nameof(classification));
            Entries = entries;
            Stop = stop;
            Reading = reading ?? throw new ArgumentNullException(nameof(reading));
        }

        public RecordingOpened Opened { get; }

        /// <summary>The effective comparison document (post profile resolution/migration).</summary>
        public ReplayComparisonProfile Profile { get; }

        public ArtifactClassification Classification { get; }

        public ValueArray<ReplayEntry> Entries { get; }

        public PlannedStop? Stop { get; }

        /// <summary>The verified reading (cuts + digest-checked blobs) the driver executes from.</summary>
        public ArtifactReadResult Reading { get; }
    }

    /// <summary>A pre-scan answers exactly one of: refused, incomparable-before-start, or planned.</summary>
    public sealed class PreScanResult
    {
        private PreScanResult(
            ReplayRefusal? refusal, IncomparableReason? incomparability, ReplayPlan? plan)
        {
            Refusal = refusal;
            Incomparability = incomparability;
            Plan = plan;
        }

        /// <summary>Trust-boundary refusal: the artifact never becomes replay input.</summary>
        public ReplayRefusal? Refusal { get; }

        /// <summary>The whole comparison is incomparable before any execution (guarantees.md §3.5).</summary>
        public IncomparableReason? Incomparability { get; }

        public ReplayPlan? Plan { get; }

        public static PreScanResult Refused(ReplayRefusal refusal) =>
            new PreScanResult(
                refusal ?? throw new ArgumentNullException(nameof(refusal)), null, null);

        public static PreScanResult Incomparable(IncomparableReason reason) =>
            new PreScanResult(null, reason, null);

        public static PreScanResult Planned(ReplayPlan plan) =>
            new PreScanResult(
                null, null, plan ?? throw new ArgumentNullException(nameof(plan)));
    }
}
