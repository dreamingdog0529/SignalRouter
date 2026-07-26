using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.ReferenceAdapter
{
    /// <summary>
    /// The reference adapter's fixed world vocabulary: identities the bootstrap
    /// registers and the TCK harness exposes. One place, so the executor, the
    /// ingress, and the harness can never disagree about a name.
    /// </summary>
    public static class ReferenceWorld
    {
        public static readonly SecurityDomainId AgentDomain = new SecurityDomainId("agent-domain");

        public static readonly SecurityDomainId HumanDomain = new SecurityDomainId("human-domain");

        public static readonly SecurityDomainId RecordDomain = new SecurityDomainId("record-domain");

        public static readonly Principal Agent = new Principal(Principal.WellKnownKinds.AgentSession, "tck-agent");

        public static readonly Principal Human = new Principal(Principal.WellKnownKinds.LocalUser, "tck-user");

        public static readonly AuthorKey TargetKey = new AuthorKey("tck-target");

        public static readonly CapabilityContractRef SetLabel =
            new CapabilityContractRef(new CapabilityContractId("SetLabel"), new ContractVersion(1, 0));

        public static readonly CapabilityContractRef SlowSetLabel =
            new CapabilityContractRef(new CapabilityContractId("SlowSetLabel"), new ContractVersion(1, 0));

        public static readonly CompletionProfileRef AppliedProfile =
            new CompletionProfileRef(new CompletionProfileId("Applied"), new ContractVersion(1, 0));

        public static readonly CompletionProfileRef FrameCommittedProfile =
            new CompletionProfileRef(new CompletionProfileId("FrameCommitted"), new ContractVersion(1, 0));

        public static readonly StateSourceKey CounterSource = new StateSourceKey("tck-counter");

        public static readonly PredicateContractRef CountAtLeastOne =
            new PredicateContractRef(new PredicateContractId("tck-count-ge-1"), new ContractVersion(1, 0));

        public static readonly PredicateContractRef CountAtLeastTwo =
            new PredicateContractRef(new PredicateContractId("tck-count-ge-2"), new ContractVersion(1, 0));

        public const string ManagedInputClass = "synthetic-click";

        public const string ObservedInputClass = "external-scene-mutation";

        /// <summary>
        /// The self-declaration (adapter-conformance.md §4): a synthetic
        /// Update/LateUpdate frame with the fence at LateUpdate, the fast capability
        /// on the fence-entailing Applied profile within one frame, the slow
        /// capability on FrameCommitted within four frames, and the two input
        /// classes the TCK verifies.
        /// </summary>
        public static AdapterDescriptor Descriptor => new AdapterDescriptor(
            "reference-adapter",
            new ContractVersion(1, 0),
            ValueList<FramePhase>.From(new[] { FramePhase.Update, FramePhase.LateUpdate }),
            FramePhase.LateUpdate,
            ValueList<CapabilityProfileSupport>.From(new[]
            {
                new CapabilityProfileSupport(
                    SetLabel, ValueList<CompletionProfileRef>.From(new[] { AppliedProfile })),
                new CapabilityProfileSupport(
                    SlowSetLabel, ValueList<CompletionProfileRef>.From(new[] { FrameCommittedProfile })),
            }),
            syncExecutionBoundMilliseconds: 5,
            ValueList<CompletionLatencyBound>.From(new[]
            {
                new CompletionLatencyBound(AppliedProfile, maxFrames: 1),
                new CompletionLatencyBound(FrameCommittedProfile, maxFrames: 4),
            }),
            ValueList<InputClassification>.From(new[]
            {
                new InputClassification(ManagedInputClass, InputClass.Managed),
                new InputClassification(ObservedInputClass, InputClass.Observed),
            }));
    }
}
