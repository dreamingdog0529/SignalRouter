using System;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;

namespace SignalRouter.V2.ReferenceAdapter
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
            ReferencePumpHost pumpHost)
        {
            Runtime = runtime;
            this.nodeSource = nodeSource;
            Ingress = ingress;
            PumpHost = pumpHost;
        }

        public KernelRuntime Runtime { get; }

        public ReferenceIngress Ingress { get; }

        public ReferencePumpHost PumpHost { get; }

        /// <summary>
        /// Builds and starts a fresh world. <paramref name="decorateExecutor"/>
        /// exists for TCK self-verification only: bad harnesses wrap the conformant
        /// executor to prove each check can fail. Production hosts pass null.
        /// </summary>
        public static ReferenceAdapterHost Create(
            Func<IEffectExecutor, IEffectExecutor>? decorateExecutor = null)
        {
            var clock = new FrameTickClock();
            var options = new KernelOptions(
                clock,
                new byte[] { 0x52, 0x65, 0x66, 0x41 },
                ValueArray<PrincipalDomainBinding>.From(new[]
                {
                    new PrincipalDomainBinding(
                        Principal.WellKnownKinds.AgentSession, ReferenceWorld.AgentDomain),
                    new PrincipalDomainBinding(
                        Principal.WellKnownKinds.LocalUser, ReferenceWorld.HumanDomain),
                }),
                ReferenceWorld.RecordDomain);
            // Every runtime incarnation gets a unique id: a NodeRef or permit
            // retained across a teardown must read as stale, never resolve into a
            // later world (guarantees.md §6.1).
            var incarnation = System.Threading.Interlocked.Increment(ref incarnationCounter);
            var runtime = new KernelRuntime(
                new RuntimeIncarnationId("reference-incarnation-" + incarnation), options);

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
            return new ReferenceAdapterHost(runtime, nodeSource, ingress, pumpHost);
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
