using System;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;
using SignalRouter.V2.Tck;

namespace SignalRouter.V2.ReferenceAdapter
{
    /// <summary>The reference adapter's TCK harness factory: one fresh world per check.</summary>
    public sealed class ReferenceTckHarnessFactory : ITckHarnessFactory
    {
        private readonly Func<IEffectExecutor, IEffectExecutor>? decorateExecutor;

        public ReferenceTckHarnessFactory(Func<IEffectExecutor, IEffectExecutor>? decorateExecutor = null)
        {
            this.decorateExecutor = decorateExecutor;
        }

        public ITckHarness Create() => new ReferenceTckHarness(ReferenceAdapterHost.Create(decorateExecutor));
    }

    /// <summary>The reference adapter under the TCK harness contract.</summary>
    public sealed class ReferenceTckHarness : ITckHarness
    {
        private readonly ReferenceAdapterHost host;

        public ReferenceTckHarness(ReferenceAdapterHost host)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public KernelRuntime Runtime => host.Runtime;

        public AdapterDescriptor Descriptor => ReferenceWorld.Descriptor;

        public Principal AgentPrincipal => ReferenceWorld.Agent;

        public Principal HumanPrincipal => ReferenceWorld.Human;

        public SecurityDomainId AgentDomain => ReferenceWorld.AgentDomain;

        public AuthorKey VisibleTargetKey => ReferenceWorld.TargetKey;

        public CapabilityContractRef MutatingCapability => ReferenceWorld.SetLabel;

        public CapabilityContractRef SlowCapability => ReferenceWorld.SlowSetLabel;

        public StateSourceKey RevisionBoundSource => ReferenceWorld.CounterSource;

        public PredicateContractRef CountAtLeastOne => ReferenceWorld.CountAtLeastOne;

        public PredicateContractRef CountAtLeastTwo => ReferenceWorld.CountAtLeastTwo;

        public LogicalTime LogicalNow => host.PumpHost.LogicalNow;

        public void SimulateManagedInput(RequestId request, ISubmissionObserver observer, bool asHuman)
        {
            host.Ingress.SimulateManagedClick(
                request,
                observer,
                asHuman ? ReferenceWorld.Human : ReferenceWorld.Agent,
                asHuman ? Provenance.HumanDirected : Provenance.Automation);
        }

        public void SimulateExternalMutation() => host.Ingress.SimulateExternalMutation();

        public PublicationAnswer PublishCount(long count)
        {
            return host.Ingress.Publish(new SourceDocument(ValueArray<NamedField>.From(new[]
            {
                new NamedField("count", FieldValue.Of(count)),
            })));
        }

        public PublicationAnswer PublishUndeclaredField()
        {
            return host.Ingress.Publish(new SourceDocument(ValueArray<NamedField>.From(new[]
            {
                new NamedField("undeclared", FieldValue.Of(1L)),
            })));
        }

        public int DriveFrames(int frames) => host.PumpHost.DriveFrames(frames);

        public void TearDown() => host.TearDown();
    }
}
