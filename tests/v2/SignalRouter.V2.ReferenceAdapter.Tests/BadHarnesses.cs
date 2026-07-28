using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;
using SignalRouter.V2.Tck;

namespace SignalRouter.V2.ReferenceAdapter.Tests;

/// <summary>
/// Deliberately non-conformant worlds (the plan's bad-harness-first rule): each
/// wraps the conformant reference adapter and violates exactly one obligation, so
/// the suite's ability to fail is itself proven before the reference adapter's
/// pass is trusted.
/// </summary>
internal static class BadHarnesses
{
    /// <summary>Reports every completion twice — violates exactly-once (adapter-conformance.md §3).</summary>
    internal sealed class DuplicatingCompletionExecutor : IEffectExecutor
    {
        private readonly IEffectExecutor inner;

        internal DuplicatingCompletionExecutor(IEffectExecutor inner) => this.inner = inner;

        public void Attach(IEffectCompletionSink sink) => inner.Attach(new DuplicatingSink(sink));

        public void Detach() => inner.Detach();

        public EffectAdoption Execute(EffectRequest request) => inner.Execute(request);

        public void RequestCancel(EffectPermitToken permit) => inner.RequestCancel(permit);

        private sealed class DuplicatingSink : IEffectCompletionSink
        {
            private readonly IEffectCompletionSink inner;

            internal DuplicatingSink(IEffectCompletionSink inner) => this.inner = inner;

            public void ReportFenceReached(EffectPermitToken permit) => inner.ReportFenceReached(permit);

            public void ReportCompletion(EffectCompletion completion)
            {
                inner.ReportCompletion(completion);
                inner.ReportCompletion(completion);
            }
        }
    }

    /// <summary>Adopts and then goes silent — violates the declared completion latency.</summary>
    internal sealed class NeverCompletingExecutor : IEffectExecutor
    {
        private readonly IEffectExecutor inner;

        internal NeverCompletingExecutor(IEffectExecutor inner) => this.inner = inner;

        public void Attach(IEffectCompletionSink sink) => inner.Attach(DiscardingSink.Instance);

        public void Detach() => inner.Detach();

        public EffectAdoption Execute(EffectRequest request) => inner.Execute(request);

        public void RequestCancel(EffectPermitToken permit) => inner.RequestCancel(permit);

        private sealed class DiscardingSink : IEffectCompletionSink
        {
            internal static readonly DiscardingSink Instance = new();

            public void ReportFenceReached(EffectPermitToken permit)
            {
            }

            public void ReportCompletion(EffectCompletion completion)
            {
            }
        }
    }

    /// <summary>Throws from Execute — never returns an adoption, violating the sync bound's logical form.</summary>
    internal sealed class ThrowingExecutor : IEffectExecutor
    {
        private readonly IEffectExecutor inner;

        internal ThrowingExecutor(IEffectExecutor inner) => this.inner = inner;

        public void Attach(IEffectCompletionSink sink) => inner.Attach(sink);

        public void Detach() => inner.Detach();

        public EffectAdoption Execute(EffectRequest request) =>
            throw new System.InvalidOperationException("engine call exploded");

        public void RequestCancel(EffectPermitToken permit) => inner.RequestCancel(permit);
    }

    /// <summary>Rewrites successful completions to carry evidence of an unbound profile.</summary>
    internal sealed class WrongEvidenceExecutor : IEffectExecutor
    {
        private readonly IEffectExecutor inner;

        internal WrongEvidenceExecutor(IEffectExecutor inner) => this.inner = inner;

        public void Attach(IEffectCompletionSink sink) => inner.Attach(new RewritingSink(sink));

        public void Detach() => inner.Detach();

        public EffectAdoption Execute(EffectRequest request) => inner.Execute(request);

        public void RequestCancel(EffectPermitToken permit) => inner.RequestCancel(permit);

        private sealed class RewritingSink : IEffectCompletionSink
        {
            private static readonly CompletionProfileRef Unbound = new(
                new CompletionProfileId("AdapterAcknowledged"), new ContractVersion(1, 0));

            private readonly IEffectCompletionSink inner;

            internal RewritingSink(IEffectCompletionSink inner) => this.inner = inner;

            public void ReportFenceReached(EffectPermitToken permit) => inner.ReportFenceReached(permit);

            public void ReportCompletion(EffectCompletion completion)
            {
                if (completion.Resolution.Kind != EffectResolutionKind.Succeeded)
                {
                    inner.ReportCompletion(completion);
                    return;
                }

                inner.ReportCompletion(new EffectCompletion(
                    completion.Permit,
                    EffectResolution.Succeeded(new CompletionEvidence(
                        Unbound, CompletionEvidenceKind.AdapterAcknowledged, default)),
                    completion.Continuations));
            }
        }
    }

    /// <summary>Delegates everything; subclasses override one behavior to sabotage it.</summary>
    internal abstract class DelegatingHarness : ITckHarness
    {
        private readonly ITckHarness inner;

        protected DelegatingHarness(ITckHarness inner) => this.inner = inner;

        protected ITckHarness Inner => inner;

        public KernelRuntime Runtime => inner.Runtime;

        public AdapterDescriptor Descriptor => inner.Descriptor;

        public Principal AgentPrincipal => inner.AgentPrincipal;

        public Principal HumanPrincipal => inner.HumanPrincipal;

        public SecurityDomainId AgentDomain => inner.AgentDomain;

        public AuthorKey VisibleTargetKey => inner.VisibleTargetKey;

        public CapabilityContractRef MutatingCapability => inner.MutatingCapability;

        public CapabilityContractRef SlowCapability => inner.SlowCapability;

        public StateSourceKey RevisionBoundSource => inner.RevisionBoundSource;

        public PredicateContractRef CountAtLeastOne => inner.CountAtLeastOne;

        public PredicateContractRef CountAtLeastTwo => inner.CountAtLeastTwo;

        public LogicalTime LogicalNow => inner.LogicalNow;

        public virtual void SimulateManagedInput(RequestId request, ISubmissionObserver observer, bool asHuman) =>
            inner.SimulateManagedInput(request, observer, asHuman);

        public virtual void SimulateExternalMutation() => inner.SimulateExternalMutation();

        public virtual PublicationAnswer PublishCount(long count) => inner.PublishCount(count);

        public virtual PublicationAnswer PublishUndeclaredField() => inner.PublishUndeclaredField();

        public int DriveFrames(int frames) => inner.DriveFrames(frames);

        public Contracts.ReplayComparisonProfile RecordingProfile => inner.RecordingProfile;

        public byte[] RedactionKey => inner.RedactionKey;

        public Contracts.PredicateDefinition DefinitionOf(Contracts.PredicateContractRef predicate) =>
            inner.DefinitionOf(predicate);

        public byte[] ReadArtifact(Contracts.OperationId recording) => inner.ReadArtifact(recording);

        public Replay.IReplayEnvironmentFactory ReplayEnvironments => inner.ReplayEnvironments;

        public void TearDown() => inner.TearDown();
    }

    /// <summary>Reports the declared Managed class as Observed — the input never becomes a submission.</summary>
    internal sealed class MisclassifyingHarness : DelegatingHarness
    {
        internal MisclassifyingHarness(ITckHarness inner)
            : base(inner)
        {
        }

        public override void SimulateManagedInput(RequestId request, ISubmissionObserver observer, bool asHuman) =>
            Inner.SimulateExternalMutation();
    }

    /// <summary>Publishes contract-violating documents in place of valid counts.</summary>
    internal sealed class InvalidPublishingHarness : DelegatingHarness
    {
        internal InvalidPublishingHarness(ITckHarness inner)
            : base(inner)
        {
        }

        public override PublicationAnswer PublishCount(long count) => Inner.PublishUndeclaredField();
    }

    /// <summary>Wraps the reference factory so every created world carries the sabotage.</summary>
    internal sealed class WrappingFactory : ITckHarnessFactory
    {
        private readonly ITckHarnessFactory inner;
        private readonly System.Func<ITckHarness, ITckHarness> wrap;

        internal WrappingFactory(ITckHarnessFactory inner, System.Func<ITckHarness, ITckHarness> wrap)
        {
            this.inner = inner;
            this.wrap = wrap;
        }

        public ITckHarness Create() => wrap(inner.Create());
    }
}
