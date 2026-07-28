using System;
using System.Collections.Generic;
using SignalRouter.Contracts;

namespace SignalRouter.AdapterSdk
{
    /// <summary>Managed vs Observed, per declared input class (adapter-conformance.md §6).</summary>
    public enum InputClass
    {
        Managed,
        Observed,
    }

    /// <summary>One declared input classification row; the TCK verifies it behaves as declared.</summary>
    public readonly struct InputClassification : IEquatable<InputClassification>
    {
        public InputClassification(string inputClass, InputClass classification)
        {
            InputClassCode = ContractGrammar.ValidateIdentifier(inputClass, nameof(inputClass));
            Classification = classification;
        }

        public string InputClassCode { get; }

        public InputClass Classification { get; }

        public bool Equals(InputClassification other) =>
            string.Equals(InputClassCode, other.InputClassCode, StringComparison.Ordinal) &&
            Classification == other.Classification;

        public override bool Equals(object? obj) => obj is InputClassification other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(InputClassCode), (int)Classification);

        public static bool operator ==(InputClassification left, InputClassification right) => left.Equals(right);

        public static bool operator !=(InputClassification left, InputClassification right) => !left.Equals(right);
    }

    /// <summary>The completion profiles an adapter supports for one capability (adapter-conformance.md §4).</summary>
    public sealed class CapabilityProfileSupport
    {
        public CapabilityProfileSupport(CapabilityContractRef capability, ValueArray<CompletionProfileRef> profiles)
        {
            if (capability.IsDefault)
            {
                throw new ArgumentException("Support row requires a non-default capability.", nameof(capability));
            }

            if (profiles == null)
            {
                throw new ArgumentNullException(nameof(profiles));
            }

            if (profiles.Count == 0)
            {
                throw new ArgumentException("A supported capability declares at least one profile.", nameof(profiles));
            }

            var seen = new HashSet<CompletionProfileRef>();
            foreach (var profile in profiles)
            {
                if (!seen.Add(profile))
                {
                    throw new ArgumentException("Profiles must be unique per capability.", nameof(profiles));
                }
            }

            Capability = capability;
            Profiles = profiles;
        }

        public CapabilityContractRef Capability { get; }

        public ValueArray<CompletionProfileRef> Profiles { get; }
    }

    /// <summary>The declared maximum effect-completion latency for one profile, as a frame/pump count.</summary>
    public readonly struct CompletionLatencyBound : IEquatable<CompletionLatencyBound>
    {
        public CompletionLatencyBound(CompletionProfileRef profile, int maxFrames)
        {
            if (profile.IsDefault)
            {
                throw new ArgumentException("Bound requires a non-default profile.", nameof(profile));
            }

            if (maxFrames < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFrames), "MaxFrames is at least one.");
            }

            Profile = profile;
            MaxFrames = maxFrames;
        }

        public CompletionProfileRef Profile { get; }

        public int MaxFrames { get; }

        public bool Equals(CompletionLatencyBound other) =>
            Profile.Equals(other.Profile) && MaxFrames == other.MaxFrames;

        public override bool Equals(object? obj) => obj is CompletionLatencyBound other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(Profile.GetHashCode(), MaxFrames);

        public static bool operator ==(CompletionLatencyBound left, CompletionLatencyBound right) => left.Equals(right);

        public static bool operator !=(CompletionLatencyBound left, CompletionLatencyBound right) => !left.Equals(right);
    }

    /// <summary>
    /// The adapter's self-declaration (adapter-conformance.md §4, ADR 0010): frame
    /// phases and fence phase, per-capability profile support, the normative
    /// synchronous execution-time bound (wall clock; TCK enforces the logical form,
    /// tier 3 measures this value), per-profile completion latency in frames, and
    /// the Managed/Observed input classification the TCK verifies.
    /// </summary>
    public sealed class AdapterDescriptor
    {
        public AdapterDescriptor(
            string adapterId,
            ContractVersion version,
            ValueArray<FramePhase> framePhases,
            FramePhase fencePhase,
            ValueArray<CapabilityProfileSupport> capabilities,
            int syncExecutionBoundMilliseconds,
            ValueArray<CompletionLatencyBound> completionLatencies,
            ValueArray<InputClassification> inputClassifications)
        {
            AdapterId = ContractGrammar.ValidateIdentifier(adapterId, nameof(adapterId));
            Version = version;

            if (framePhases == null)
            {
                throw new ArgumentNullException(nameof(framePhases));
            }

            if (framePhases.Count == 0)
            {
                throw new ArgumentException("An adapter declares at least one frame phase.", nameof(framePhases));
            }

            var phases = new HashSet<FramePhase>();
            foreach (var phase in framePhases)
            {
                if (!phases.Add(phase))
                {
                    throw new ArgumentException("Frame phases must be unique.", nameof(framePhases));
                }
            }

            if (fencePhase.IsDefault || !phases.Contains(fencePhase))
            {
                throw new ArgumentException(
                    "The fence phase must be one of the declared frame phases.", nameof(fencePhase));
            }

            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            var capabilityRefs = new HashSet<CapabilityContractRef>();
            foreach (var support in capabilities)
            {
                if (!capabilityRefs.Add(support.Capability))
                {
                    throw new ArgumentException(
                        "Capability support rows must be unique.", nameof(capabilities));
                }
            }

            if (syncExecutionBoundMilliseconds < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(syncExecutionBoundMilliseconds), "The sync bound is at least one millisecond.");
            }

            if (completionLatencies == null)
            {
                throw new ArgumentNullException(nameof(completionLatencies));
            }

            var latencyProfiles = new HashSet<CompletionProfileRef>();
            foreach (var latency in completionLatencies)
            {
                if (!latencyProfiles.Add(latency.Profile))
                {
                    throw new ArgumentException(
                        "Completion latency rows must be unique per profile.", nameof(completionLatencies));
                }
            }

            // Every supported profile has exactly one latency row, and no row is an
            // orphan — the TCK and host rely on a declared MaxFrames for each.
            var supportedProfiles = new HashSet<CompletionProfileRef>();
            foreach (var support in capabilities)
            {
                foreach (var profile in support.Profiles)
                {
                    supportedProfiles.Add(profile);
                }
            }

            foreach (var profile in supportedProfiles)
            {
                if (!latencyProfiles.Contains(profile))
                {
                    throw new ArgumentException(
                        "Every supported completion profile requires a latency bound.",
                        nameof(completionLatencies));
                }
            }

            foreach (var profile in latencyProfiles)
            {
                if (!supportedProfiles.Contains(profile))
                {
                    throw new ArgumentException(
                        "A latency bound must reference a supported completion profile.",
                        nameof(completionLatencies));
                }
            }

            if (inputClassifications == null)
            {
                throw new ArgumentNullException(nameof(inputClassifications));
            }

            var inputClasses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var classification in inputClassifications)
            {
                if (!inputClasses.Add(classification.InputClassCode))
                {
                    throw new ArgumentException(
                        "Input classification rows must be unique.", nameof(inputClassifications));
                }
            }

            FramePhases = framePhases;
            FencePhase = fencePhase;
            Capabilities = capabilities;
            SyncExecutionBoundMilliseconds = syncExecutionBoundMilliseconds;
            CompletionLatencies = completionLatencies;
            InputClassifications = inputClassifications;
        }

        public string AdapterId { get; }

        public ContractVersion Version { get; }

        public ValueArray<FramePhase> FramePhases { get; }

        public FramePhase FencePhase { get; }

        public ValueArray<CapabilityProfileSupport> Capabilities { get; }

        /// <summary>Normative wall-clock bound; tier 3 measures it, and a supported adapter MUST meet it.</summary>
        public int SyncExecutionBoundMilliseconds { get; }

        public ValueArray<CompletionLatencyBound> CompletionLatencies { get; }

        public ValueArray<InputClassification> InputClassifications { get; }
    }
}
