using SignalRouter.AdapterSdk;
using SignalRouter.Contracts;
using SignalRouter.Kernel;
using SignalRouter.Replay;

namespace SignalRouter.Tck
{
    /// <summary>
    /// What an adapter package implements to become TCK-runnable
    /// (adapter-conformance.md §7.2): a factory for fresh, isolated harness worlds.
    /// Every check creates its own world; nothing is shared between checks.
    /// </summary>
    public interface ITckHarnessFactory
    {
        ITckHarness Create();
    }

    /// <summary>
    /// One bootstrapped adapter+kernel world the TCK drives black-box through the
    /// SDK and runtime surfaces. The harness contract requires:
    ///
    /// - a visible target node (<see cref="VisibleTargetKey"/>) exposed to both
    ///   principals' domains, declaring
    ///   <see cref="MutatingCapability"/> (argument-free, fence-entailing profile)
    ///   and <see cref="SlowCapability"/> (argument-free, spans at least two frames,
    ///   honors cooperative cancellation);
    /// - a revision-bound state source (<see cref="RevisionBoundSource"/>) with one
    ///   integer field named <c>count</c>;
    /// - registered predicate contracts <see cref="CountAtLeastOne"/> and
    ///   <see cref="CountAtLeastTwo"/> over <c>sources/&lt;source&gt;/count</c>;
    /// - a Managed and an Observed input class declared in the descriptor and
    ///   simulated by the two Simulate methods;
    /// - <see cref="HumanPrincipal"/> bound to a domain other than
    ///   <see cref="AgentDomain"/>, used with human-directed provenance.
    /// </summary>
    public interface ITckHarness
    {
        KernelRuntime Runtime { get; }

        AdapterDescriptor Descriptor { get; }

        Principal AgentPrincipal { get; }

        Principal HumanPrincipal { get; }

        SecurityDomainId AgentDomain { get; }

        AuthorKey VisibleTargetKey { get; }

        CapabilityContractRef MutatingCapability { get; }

        CapabilityContractRef SlowCapability { get; }

        StateSourceKey RevisionBoundSource { get; }

        PredicateContractRef CountAtLeastOne { get; }

        PredicateContractRef CountAtLeastTwo { get; }

        /// <summary>The host logical clock's current reading — what the next pump would carry.</summary>
        LogicalTime LogicalNow { get; }

        /// <summary>Simulates one captured input of the declared Managed class, normalized to a submission.</summary>
        void SimulateManagedInput(RequestId request, ISubmissionObserver observer, bool asHuman);

        /// <summary>Simulates one uncapturable change of the declared Observed class.</summary>
        void SimulateExternalMutation();

        /// <summary>Publishes <c>count</c> to the revision-bound source.</summary>
        PublicationAnswer PublishCount(long count);

        /// <summary>Publishes a document violating the source contract (an undeclared field).</summary>
        PublicationAnswer PublishUndeclaredField();

        /// <summary>Drives whole frames (every declared phase, in order); returns frames driven.</summary>
        int DriveFrames(int frames);

        // ── The tier-2 recording and replay obligations (item 5) ─────────────
        // The harness runtime MUST be recording-capable: constructed with a
        // durable evidence coordinator, a canonical-state codec, and the
        // registered record view the profile below names.

        /// <summary>The comparison profile the harness world records and replays under.</summary>
        ReplayComparisonProfile RecordingProfile { get; }

        /// <summary>The runtime's redaction material — replay re-digests resolved secrets with it.</summary>
        byte[] RedactionKey { get; }

        /// <summary>The registered definition of a harness predicate (the replay allowlist's material).</summary>
        PredicateDefinition DefinitionOf(PredicateContractRef predicate);

        /// <summary>The closed recording's artifact bytes.</summary>
        byte[] ReadArtifact(OperationId recording);

        /// <summary>The adapter's twin builder (recording-replay.md §6).</summary>
        IReplayEnvironmentFactory ReplayEnvironments { get; }

        void TearDown();
    }
}
