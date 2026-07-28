using System;
using SignalRouter.AdapterSdk;
using SignalRouter.Codec.Recording;
using SignalRouter.Contracts;
using SignalRouter.Kernel;
using SignalRouter.Recording;

namespace SignalRouter.ReferenceAdapter
{
    /// <summary>
    /// One wired reference world: kernel runtime + node source + ingress + executor
    /// + pump host, bootstrapped and started. The wiring order is the normative
    /// shape from adapter-conformance.md §2 — attach sources, start the runtime
    /// (which attaches the executor to the completion sink), then hand the kernel
    /// to the pump host.
    /// </summary>
    public sealed class ReferenceAdapterHost
    {
        private static int incarnationCounter;

        private readonly ReferenceNodeSource nodeSource;
        private bool tornDown;

        private ReferenceAdapterHost(
            KernelRuntime runtime,
            ReferenceNodeSource nodeSource,
            ReferenceIngress ingress,
            ReferencePumpHost pumpHost,
            MemoryArtifactStore? artifactStore)
        {
            Runtime = runtime;
            this.nodeSource = nodeSource;
            Ingress = ingress;
            PumpHost = pumpHost;
            ArtifactStore = artifactStore;
        }

        public KernelRuntime Runtime { get; }

        public ReferenceIngress Ingress { get; }

        public ReferencePumpHost PumpHost { get; }

        /// <summary>The recording sink backing this world; null when an external coordinator was injected.</summary>
        public MemoryArtifactStore? ArtifactStore { get; }

        /// <summary>
        /// Builds and starts a fresh world. <paramref name="decorateExecutor"/>
        /// exists for TCK self-verification only: bad harnesses wrap the conformant
        /// executor to prove each check can fail. Production hosts pass null.
        /// The world is recording-capable by default — a durable coordinator over
        /// an IN-MEMORY sink (the TCK proves recording capability, not durable
        /// storage), at the standing cost of the admission-evidence projection
        /// even while nothing records; a replay twin injects the driver's
        /// capture coordinator through <paramref name="evidence"/> instead.
        /// </summary>
        public static ReferenceAdapterHost Create(
            Func<IEffectExecutor, IEffectExecutor>? decorateExecutor = null,
            IEvidenceCoordinator? evidence = null)
        {
            var clock = new FrameTickClock();
            var options = new KernelOptions(
                clock,
                ReferenceWorld.RedactionKey,
                ValueArray<PrincipalDomainBinding>.From(new[]
                {
                    new PrincipalDomainBinding(
                        Principal.WellKnownKinds.AgentSession, ReferenceWorld.AgentDomain),
                    new PrincipalDomainBinding(
                        Principal.WellKnownKinds.LocalUser, ReferenceWorld.HumanDomain),
                }),
                ReferenceWorld.RecordDomain,
                canonicalStateCodec: new Codec.CanonicalState.CanonicalStateCodec());
            MemoryArtifactStore? store = null;
            if (evidence == null)
            {
                store = new MemoryArtifactStore();
                evidence = new DurableEvidenceCoordinator(
                    store,
                    new RecordingCoordinatorOptions(
                        ReferenceWorld.RecordingProfile(), allowNonDurableStore: true));
            }

            // Every runtime incarnation gets a unique id: a NodeRef or permit
            // retained across a teardown must read as stale, never resolve into a
            // later world (guarantees.md §6.1).
            var incarnation = System.Threading.Interlocked.Increment(ref incarnationCounter);
            var runtime = new KernelRuntime(
                new RuntimeIncarnationId("reference-incarnation-" + incarnation), options, evidence);

            var nodeSource = new ReferenceNodeSource();
            nodeSource.Attach(runtime.Bootstrap, runtime.Registry);
            var ingress = new ReferenceIngress();
            ingress.Attach(runtime.Ingress);

            var referenceExecutor = new ReferenceEffectExecutor(runtime.Registry, nodeSource.TargetNode);
            var executor = decorateExecutor == null
                ? referenceExecutor
                : decorateExecutor(referenceExecutor) ??
                    throw new InvalidOperationException("The executor decorator returned null.");
            runtime.Start(executor);

            var pumpHost = new ReferencePumpHost(referenceExecutor, clock);
            pumpHost.Attach(runtime);
            return new ReferenceAdapterHost(runtime, nodeSource, ingress, pumpHost, store);
        }

        /// <summary>Tears the incarnation down and detaches every adapter surface. Idempotent.</summary>
        public void TearDown()
        {
            if (tornDown)
            {
                return;
            }

            tornDown = true;
            Runtime.Control.TearDownIncarnation();
            PumpHost.DriveFrames(1);
            Ingress.Detach();
            nodeSource.Detach();
            PumpHost.Detach();
        }
    }
}
