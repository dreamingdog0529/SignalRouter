using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// Portable-output equivalence oracles (performance-track plan P0a): everything
/// an observer can see — query answers, trace kinds, snapshot ContentIds,
/// lookup answers — must be a function of the registered world and the submitted
/// work, never of internal registration order, handle numbering, or slot reuse.
/// These tests freeze that property before the representation work (P3–P5
/// internal handles, scope indexes, materialization memoization) begins: any
/// internal-order leak into portable output fails here.
/// </summary>
public sealed class RuntimeEquivalenceTests
{
    private static readonly ViewContractRef AgentView =
        new(new ViewContractId("agent-standard"), new ContractVersion(1, 0));

    private sealed class RecordingRegistrationObserver : IRegistrationObserver
    {
        internal List<RegistrationReceipt> Receipts { get; } = new();

        public void OnCompleted(RegistrationReceipt receipt) => Receipts.Add(receipt);
    }

    /// <summary>
    /// A self-built world equivalent to <see cref="KernelFixture"/>'s but with a
    /// parameterizable bootstrap order and optional post-start register/unregister
    /// churn — the two internal degrees of freedom that portable output must not
    /// depend on.
    /// </summary>
    private sealed class World
    {
        internal ManualClock Clock { get; } = new();

        internal ScriptedExecutor Executor { get; } = new();

        internal KernelRuntime Runtime { get; }

        private long logicalNow = 100;

        private readonly ExposurePolicy visibleToAll = new(ValueArray<SecurityDomainId>.From(new[]
        {
            KernelFixture.AgentDomain, KernelFixture.HumanDomain, KernelFixture.RecordDomain,
        }));

        internal World(bool reversedBootstrap, bool churn)
        {
            var options = new KernelOptions(
                Clock,
                new byte[] { 1, 2, 3, 4 },
                ValueArray<PrincipalDomainBinding>.From(new[]
                {
                    new PrincipalDomainBinding(
                        Principal.WellKnownKinds.AgentSession, KernelFixture.AgentDomain),
                    new PrincipalDomainBinding(
                        Principal.WellKnownKinds.LocalUser, KernelFixture.HumanDomain),
                    new PrincipalDomainBinding(
                        Principal.WellKnownKinds.TestHarness, KernelFixture.RecordDomain),
                }),
                KernelFixture.RecordDomain,
                // The real codec, not TestCanonicalStateCodec: ADR 0012 keeps the
                // temporal legs out of the payload, so churn-advanced revisions
                // re-address unchanged visible state to the same ContentId — the
                // exact property these equivalence oracles pin. (The FNV test
                // double renders the revision into its payload and would diverge.)
                canonicalStateCodec: new SignalRouter.V2.Codec.CanonicalState.CanonicalStateCodec());
            Runtime = new KernelRuntime(new RuntimeIncarnationId("incarnation-1"), options);

            // The capability contract always registers first (nodes declare it);
            // the permuted set is the nodes, the source, and the view.
            Runtime.Bootstrap.RegisterCapabilityContract(new CapabilityContractDescriptor(
                KernelFixture.Invoke, ArgumentSchema.Empty, precondition: null,
                KernelFixture.Applied, postcondition: null));

            var steps = new Action[]
            {
                () => Runtime.Bootstrap.RegisterNode(new NodeRegistration(
                    new AuthorKey("save"),
                    NodeRole.Button,
                    parent: null,
                    ValueArray<NodeAttribute>.From(new[]
                    {
                        new NodeAttribute("label", FieldValue.Of("Save"), Sensitivity.Standard),
                    }),
                    ValueArray<CapabilityDeclaration>.From(new[]
                    {
                        new CapabilityDeclaration(KernelFixture.Invoke, initiallyAvailable: true),
                    }),
                    visibleToAll)),
                () => Runtime.Bootstrap.RegisterNode(new NodeRegistration(
                    new AuthorKey("secret"),
                    NodeRole.Button,
                    parent: null,
                    ValueArray<NodeAttribute>.Empty,
                    ValueArray<CapabilityDeclaration>.From(new[]
                    {
                        new CapabilityDeclaration(KernelFixture.Invoke, initiallyAvailable: true),
                    }),
                    ExposurePolicy.Hidden)),
                () => Runtime.Bootstrap.RegisterStateSource(new StateSourceRegistration(
                    new StateSourceKey("inventory"),
                    new StateSourceContractDescriptor(
                        new StateSourceContractRef(
                            new StateSourceContractId("inventory"), new ContractVersion(1, 0)),
                        ValueArray<SourceFieldSchema>.From(new[]
                        {
                            new SourceFieldSchema("count", FieldType.Integer, Sensitivity.Standard),
                            new SourceFieldSchema("secret", FieldType.String, Sensitivity.Sensitive),
                        }),
                        agentVisible: true,
                        recordVisible: true,
                        maxDocumentBytes: 4096),
                    StateSourceClass.RevisionBound)),
                () => Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
                    AgentView, ViewFamily.Agent, "root",
                    maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false)),
            };
            if (reversedBootstrap)
            {
                Array.Reverse(steps);
            }

            foreach (var step in steps)
            {
                step();
            }

            Runtime.Start(Executor);

            if (churn)
            {
                // Register-then-unregister dummies: internal handle numbering and
                // slot reuse diverge from the pristine runtime; portable output
                // must not.
                var observer = new RecordingRegistrationObserver();
                for (var i = 0; i < 3; i++)
                {
                    Runtime.Registry.Register(new NodeRegistration(
                        new AuthorKey("churn-" + i),
                        NodeRole.Button,
                        parent: null,
                        ValueArray<NodeAttribute>.Empty,
                        ValueArray<CapabilityDeclaration>.Empty,
                        visibleToAll), observer);
                }

                PumpUntilIdle();
                Assert.That(observer.Receipts.Count, Is.EqualTo(3));
                foreach (var receipt in observer.Receipts)
                {
                    Assert.That(receipt.Succeeded, Is.True);
                    Runtime.Registry.Unregister(receipt.Node!.Value, null);
                }

                PumpUntilIdle();
            }

            // Both worlds register the same post-start node LAST: in the churned
            // world it occupies a recycled slot freed by the unregistered dummies,
            // in the pristine world a fresh one — real slot reuse, same visible
            // world either way.
            var lateObserver = new RecordingRegistrationObserver();
            Runtime.Registry.Register(new NodeRegistration(
                new AuthorKey("late"),
                NodeRole.Button,
                parent: null,
                ValueArray<NodeAttribute>.From(new[]
                {
                    new NodeAttribute("marker", FieldValue.Of("fresh"), Sensitivity.Standard),
                }),
                ValueArray<CapabilityDeclaration>.Empty,
                visibleToAll), lateObserver);
            PumpUntilIdle();
            Assert.That(lateObserver.Receipts.Single().Succeeded, Is.True);
        }

        internal void PumpUntilIdle(int maxPumps = 16)
        {
            for (var i = 0; i < maxPumps; i++)
            {
                var report = Runtime.Pump(new PumpBudget(
                    maxTurns: 64, deadline: long.MaxValue, new LogicalTime(logicalNow), FramePhase.Update));
                if (!report.WorkRemaining)
                {
                    return;
                }
            }

            throw new InvalidOperationException("The kernel did not become idle.");
        }

        internal void RunStandardEpisode()
        {
            var answer = Runtime.Ingress.PublishSourceDocument(new SourcePublication(
                new StateSourceKey("inventory"),
                new SourceDocument(ValueArray<NamedField>.From(new[]
                {
                    new NamedField("count", FieldValue.Of(3L)),
                })),
                EventCausation.None));
            Assert.That(answer, Is.EqualTo(PublicationAnswer.Accepted));
            PumpUntilIdle();

            // A refused effect is a deterministic zero-effect terminal — no
            // completion choreography, identical in every runtime.
            Executor.Behavior = ScriptedExecutor.Mode.Refuse;
            Runtime.Ingress.Submit(new IntentSubmission(
                new RequestId("r1"),
                KernelFixture.Invoke,
                TargetReference.ForKey(new AuthorKey("save")),
                InvocationPayload.Empty,
                new IdentityEnvelope(
                    KernelFixture.Agent, IngressPath.Mcp, Provenance.Automation, Causality.Root()),
                new RecordingObserver()));
            PumpUntilIdle();
        }

        internal PinnedSnapshot Pin()
        {
            var observer = new RecordingSnapshotObserver();
            Runtime.Control.RequestSnapshot(AgentView, KernelFixture.Agent, "root", observer);
            PumpUntilIdle();
            Assert.That(observer.Refused, Is.Empty, "expected a pinned answer");
            return observer.Pinned.Single().Snapshot;
        }

        /// <summary>
        /// Every observer-visible field of every trace event — an internal-order
        /// leak into causation, request, operation, order, or revision must fail
        /// the equivalence assertion, not just a changed kind.
        /// </summary>
        internal List<string> TraceRendering()
        {
            var events = new List<string>();
            foreach (var semanticEvent in Runtime.Trace.Snapshot())
            {
                events.Add(string.Join("|",
                    semanticEvent.Kind.Value,
                    semanticEvent.Incarnation.Value,
                    semanticEvent.Causation.ToString(),
                    semanticEvent.Request?.Value ?? "-",
                    semanticEvent.Operation?.Value ?? "-",
                    semanticEvent.Order?.ToString() ?? "-",
                    semanticEvent.Revision?.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                    semanticEvent.DetailCode ?? "-"));
            }

            return events;
        }
    }

    [Test]
    public void BootstrapOrderNeverLeaksIntoPortableOutput()
    {
        var forward = new World(reversedBootstrap: false, churn: false);
        var reversed = new World(reversedBootstrap: true, churn: false);
        forward.RunStandardEpisode();
        reversed.RunStandardEpisode();

        Assert.That(
            reversed.Runtime.Queries.Query(new RequestId("r1"), KernelFixture.Agent),
            Is.EqualTo(forward.Runtime.Queries.Query(new RequestId("r1"), KernelFixture.Agent)),
            "query answers are portable");
        Assert.That(
            reversed.TraceRendering(), Is.EqualTo(forward.TraceRendering()),
            "the full trace event sequence is portable");

        var forwardPin = forward.Pin();
        var reversedPin = reversed.Pin();
        Assert.That(forwardPin.Snapshot.IsAddressed, Is.True);
        Assert.That(
            reversedPin.Snapshot.ContentId, Is.EqualTo(forwardPin.Snapshot.ContentId),
            "the snapshot ContentId is a function of visible state, not registration order");
        Assert.That(
            reversedPin.Materialization.Nodes.Select(node => node.Key.Value).ToArray(),
            Is.EqualTo(forwardPin.Materialization.Nodes.Select(node => node.Key.Value).ToArray()));
    }

    [Test]
    public void HandleChurnAndSlotReuseNeverLeakIntoPortableOutput()
    {
        var pristine = new World(reversedBootstrap: false, churn: false);
        var churned = new World(reversedBootstrap: false, churn: true);
        pristine.RunStandardEpisode();
        churned.RunStandardEpisode();

        Assert.That(
            churned.Runtime.Queries.Query(new RequestId("r1"), KernelFixture.Agent),
            Is.EqualTo(pristine.Runtime.Queries.Query(new RequestId("r1"), KernelFixture.Agent)));

        var pristinePin = pristine.Pin();
        var churnedPin = churned.Pin();
        Assert.That(
            churnedPin.Snapshot.ContentId, Is.EqualTo(pristinePin.Snapshot.ContentId),
            "unregistered churn nodes leave no residue in the snapshot");
        Assert.That(
            churnedPin.Lookup.Lookup(new FieldPath("nodes/save/attributes/label")),
            Is.EqualTo(FieldLookup.Present(FieldValue.Of("Save"))));
        Assert.That(
            churnedPin.Lookup.Lookup(new FieldPath("nodes/churn-0/attributes/label")),
            Is.EqualTo(FieldLookup.OutOfScope),
            "an unregistered node answers exactly like one that never existed");
        Assert.That(
            churnedPin.Lookup.Lookup(new FieldPath("nodes/late/attributes/marker")),
            Is.EqualTo(FieldLookup.Present(FieldValue.Of("fresh"))),
            "the node in the recycled slot serves its own record, not a stale one");
    }

    [Test]
    public void RepeatedReadsAtOneRevisionAreIdentical()
    {
        var world = new World(reversedBootstrap: false, churn: false);
        world.RunStandardEpisode();

        var first = world.Pin();
        var second = world.Pin();
        Assert.That(second.Snapshot.ContentId, Is.EqualTo(first.Snapshot.ContentId),
            "materialization at one revision is deterministic");
        Assert.That(
            second.Snapshot.Basis.Revision, Is.EqualTo(first.Snapshot.Basis.Revision));
        foreach (var path in new[]
        {
            "nodes/save/attributes/label",
            "nodes/save",
            "sources/inventory/count",
            "nodes/secret/attributes/label",
        })
        {
            Assert.That(
                second.Lookup.Lookup(new FieldPath(path)),
                Is.EqualTo(first.Lookup.Lookup(new FieldPath(path))),
                $"lookup at '{path}' must answer identically at one revision");
        }
    }

    [Test]
    public void AHiddenNodeAnswersExactlyLikeAnUnregisteredOne()
    {
        // Existence concealment at the lookup level (guarantees.md §3.5): the
        // registered-but-hidden `secret` node and a never-registered node must be
        // observationally identical to the agent domain.
        var world = new World(reversedBootstrap: false, churn: false);
        world.RunStandardEpisode();
        var pin = world.Pin();

        var hidden = pin.Lookup.Lookup(new FieldPath("nodes/secret/attributes/label"));
        var unregistered = pin.Lookup.Lookup(new FieldPath("nodes/never-existed/attributes/label"));
        Assert.That(hidden, Is.EqualTo(unregistered));
        Assert.That(hidden, Is.EqualTo(FieldLookup.OutOfScope));

        var hiddenCount = pin.Lookup.CountCollection(new FieldPath("nodes/secret"));
        var unregisteredCount = pin.Lookup.CountCollection(new FieldPath("nodes/never-existed"));
        Assert.That(hiddenCount, Is.EqualTo(unregisteredCount));
    }
}
