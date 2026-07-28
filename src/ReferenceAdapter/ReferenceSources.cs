using System;
using SignalRouter.AdapterSdk;
using SignalRouter.Contracts;

namespace SignalRouter.ReferenceAdapter
{
    /// <summary>
    /// The reference node surface: initial construction through the synchronous
    /// bootstrap registry (ADR 0010) — the target node, its two capabilities, the
    /// revision-bound counter source, and the two count predicates.
    /// </summary>
    public sealed class ReferenceNodeSource : INodeSource
    {
        private NodeRef targetNode;

        public NodeRef TargetNode =>
            targetNode.IsDefault
                ? throw new InvalidOperationException("The node source is not attached.")
                : targetNode;

        public void Attach(IBootstrapRegistry bootstrap, INodeRegistry registry)
        {
            bootstrap.RegisterCapabilityContract(new CapabilityContractDescriptor(
                ReferenceWorld.SetLabel, ArgumentSchema.Empty, precondition: null,
                ReferenceWorld.AppliedProfile, postcondition: null));
            bootstrap.RegisterCapabilityContract(new CapabilityContractDescriptor(
                ReferenceWorld.SlowSetLabel, ArgumentSchema.Empty, precondition: null,
                ReferenceWorld.FrameCommittedProfile, postcondition: null));

            targetNode = bootstrap.RegisterNode(new NodeRegistration(
                ReferenceWorld.TargetKey,
                NodeRole.Button,
                parent: null,
                ValueArray<NodeAttribute>.From(new[]
                {
                    new NodeAttribute("label", FieldValue.Of("initial"), Sensitivity.Standard),
                }),
                ValueArray<CapabilityDeclaration>.From(new[]
                {
                    new CapabilityDeclaration(ReferenceWorld.SetLabel, initiallyAvailable: true),
                    new CapabilityDeclaration(ReferenceWorld.SlowSetLabel, initiallyAvailable: true),
                }),
                new ExposurePolicy(ValueArray<SecurityDomainId>.From(new[]
                {
                    ReferenceWorld.AgentDomain,
                    ReferenceWorld.HumanDomain,
                    ReferenceWorld.RecordDomain,
                }))));

            bootstrap.RegisterStateSource(new StateSourceRegistration(
                ReferenceWorld.CounterSource,
                new StateSourceContractDescriptor(
                    new StateSourceContractRef(
                        new StateSourceContractId("tck-counter"), new ContractVersion(1, 0)),
                    ValueArray<SourceFieldSchema>.From(new[]
                    {
                        new SourceFieldSchema("count", FieldType.Integer, Sensitivity.Standard),
                    }),
                    agentVisible: true,
                    recordVisible: true,
                    maxDocumentBytes: 4096),
                StateSourceClass.RevisionBound));

            bootstrap.RegisterPredicateContract(
                ReferenceWorld.CountAtLeastOne, ReferenceWorld.CountPredicate(1));
            bootstrap.RegisterPredicateContract(
                ReferenceWorld.CountAtLeastTwo, ReferenceWorld.CountPredicate(2));

            // The record view (item 5): the recording's single comparison surface.
            bootstrap.RegisterViewContract(new ViewContractDescriptor(
                ReferenceWorld.RecordView, ViewFamily.Record, "root",
                maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        }

        public void Detach()
        {
            targetNode = default;
        }
    }

    /// <summary>
    /// The reference ingress surface (adapter-conformance.md §6): a Managed
    /// synthetic click normalized into a submission, an Observed scene mutation
    /// reported as uncapturable, and revision-bound counter publications.
    /// </summary>
    public sealed class ReferenceIngress : IIngressSource
    {
        private IIngressSink? sink;

        private IIngressSink Sink =>
            sink ?? throw new InvalidOperationException("The ingress is not attached.");

        public void Attach(IIngressSink ingressSink) =>
            sink = ingressSink ?? throw new ArgumentNullException(nameof(ingressSink));

        public void Detach() => sink = null;

        /// <summary>One captured synthetic click, normalized per the Managed class contract.</summary>
        public void SimulateManagedClick(
            RequestId request, ISubmissionObserver observer, Principal principal, Provenance provenance)
        {
            Sink.Submit(new IntentSubmission(
                request,
                ReferenceWorld.SetLabel,
                TargetReference.ForKey(ReferenceWorld.TargetKey),
                InvocationPayload.Empty,
                new IdentityEnvelope(principal, IngressPath.PhysicalInput, provenance, Causality.Root()),
                observer));
        }

        /// <summary>One uncapturable scene mutation, reported per the Observed class contract.</summary>
        public void SimulateExternalMutation()
        {
            Sink.ReportObservedExternal(new ObservedExternalReport(
                ReferenceWorld.ObservedInputClass, node: null, authorKey: null));
        }

        public PublicationAnswer Publish(SourceDocument document)
        {
            return Sink.PublishSourceDocument(new SourcePublication(
                ReferenceWorld.CounterSource, document, EventCausation.None));
        }
    }
}
