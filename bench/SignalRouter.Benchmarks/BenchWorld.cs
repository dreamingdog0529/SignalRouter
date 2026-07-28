using System;
using System.Globalization;
using SignalRouter.AdapterSdk;
using SignalRouter.Codec.CanonicalState;
using SignalRouter.Contracts;
using SignalRouter.Kernel;

namespace SignalRouter.Benchmarks;

/// <summary>
/// A bootstrapped kernel world for measurement: N flat visible nodes (two
/// attributes and one capability each), one revision-bound source, one
/// registered agent view, one never-satisfied predicate, and a refusing
/// executor so every interaction reaches a deterministic zero-effect terminal
/// without completion choreography. Shared by the benchmarks and the
/// allocation-characterization tests so both measure exactly the same world.
/// Hierarchy depth is deliberately not exercised yet — flat parents keep the
/// scope-walk cost out of this baseline (noted in BASELINE.md).
/// </summary>
public sealed class BenchWorld
{
    public static readonly SecurityDomainId AgentDomain = new("agent-domain");
    public static readonly SecurityDomainId RecordDomain = new("record-domain");

    public static readonly Principal Agent = new(Principal.WellKnownKinds.AgentSession, "agent-1");

    public static readonly CapabilityContractRef Invoke =
        new(new CapabilityContractId("Invoke"), new ContractVersion(1, 0));

    public static readonly CompletionProfileRef Applied =
        new(new CompletionProfileId("Applied"), new ContractVersion(1, 0));

    public static readonly ViewContractRef AgentView =
        new(new ViewContractId("agent-standard"), new ContractVersion(1, 0));

    public static readonly PredicateContractRef NeverSatisfied =
        new(new PredicateContractId("neverSatisfied"), new ContractVersion(1, 0));

    private sealed class FixedClock : IMonotonicClock
    {
        public long Now => 0;
    }

    private sealed class RefusingExecutor : IEffectExecutor
    {
        public void Attach(IEffectCompletionSink completionSink)
        {
        }

        public void Detach()
        {
        }

        public EffectAdoption Execute(EffectRequest request) =>
            EffectAdoption.Refused(new FaultCode("BenchRefusal"));

        public void RequestCancel(EffectPermitToken permit)
        {
        }
    }

    private sealed class NullSubmissionObserver : ISubmissionObserver
    {
        public static readonly NullSubmissionObserver Instance = new();

        public void OnAccepted(RequestId request)
        {
        }

        public void OnRejected(RequestId request, RejectionReason reason)
        {
        }
    }

    private sealed class NullSnapshotObserver : ISnapshotObserver
    {
        public static readonly NullSnapshotObserver Instance = new();

        public void OnPinned(OperationId operation, PinnedSnapshot snapshot)
        {
        }

        public void OnRefused(OperationId operation, string reasonCode)
        {
        }
    }

    private sealed class CountingSnapshotObserver : ISnapshotObserver
    {
        public int PinnedCount;

        public PinnedSnapshot? Last;

        public void OnPinned(OperationId operation, PinnedSnapshot snapshot)
        {
            PinnedCount++;
            Last = snapshot;
        }

        public void OnRefused(OperationId operation, string reasonCode) =>
            throw new InvalidOperationException("Snapshot refused: " + reasonCode);
    }

    private sealed class NullWaitObserver : IWaitObserver
    {
        public static readonly NullWaitObserver Instance = new();

        public void OnResolved(OperationId operation, PredicateResolution resolution)
        {
        }
    }

    private int requestSequence;

    public KernelRuntime Runtime { get; }

    private BenchWorld(KernelRuntime runtime)
    {
        Runtime = runtime;
    }

    public static BenchWorld Create(int nodeCount, bool withCodec)
    {
        var options = new KernelOptions(
            new FixedClock(),
            new byte[] { 1, 2, 3, 4 },
            ValueArray<PrincipalDomainBinding>.From(new[]
            {
                new PrincipalDomainBinding(Principal.WellKnownKinds.AgentSession, AgentDomain),
                new PrincipalDomainBinding(Principal.WellKnownKinds.TestHarness, RecordDomain),
            }),
            RecordDomain,
            // Budgets AND materialization ceilings sized so the 2048-node world
            // materializes completely - truncation would silently shrink the
            // very work being measured, and a truncated projection additionally
            // makes the timeline checkpoint feed retry on every pump.
            observationBudgetBytes: 8 * 1024 * 1024,
            observationBudgetNodes: 4096,
            maxMaterializationNodes: 4096,
            maxMaterializationBytes: 8 * 1024 * 1024,
            canonicalStateCodec: withCodec ? new CanonicalStateCodec() : null);
        var runtime = new KernelRuntime(new RuntimeIncarnationId("bench-incarnation"), options);

        runtime.Bootstrap.RegisterCapabilityContract(new CapabilityContractDescriptor(
            Invoke, ArgumentSchema.Empty, precondition: null, Applied, postcondition: null));

        var visible = new ExposurePolicy(ValueArray<SecurityDomainId>.From(new[]
        {
            AgentDomain, RecordDomain,
        }));
        for (var i = 0; i < nodeCount; i++)
        {
            var ordinal = i.ToString("D5", CultureInfo.InvariantCulture);
            runtime.Bootstrap.RegisterNode(new NodeRegistration(
                new AuthorKey("node-" + ordinal),
                NodeRole.Button,
                parent: null,
                ValueArray<NodeAttribute>.From(new[]
                {
                    new NodeAttribute("label", FieldValue.Of("Label " + ordinal), Sensitivity.Standard),
                    new NodeAttribute("value", FieldValue.Of((long)i), Sensitivity.Standard),
                }),
                ValueArray<CapabilityDeclaration>.From(new[]
                {
                    new CapabilityDeclaration(Invoke, initiallyAvailable: true),
                }),
                visible));
        }

        runtime.Bootstrap.RegisterStateSource(new StateSourceRegistration(
            new StateSourceKey("inventory"),
            new StateSourceContractDescriptor(
                new StateSourceContractRef(
                    new StateSourceContractId("inventory"), new ContractVersion(1, 0)),
                ValueArray<SourceFieldSchema>.From(new[]
                {
                    new SourceFieldSchema("count", FieldType.Integer, Sensitivity.Standard),
                }),
                agentVisible: true,
                recordVisible: true,
                maxDocumentBytes: 4096),
            StateSourceClass.RevisionBound));

        runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            AgentView, ViewFamily.Agent, "root",
            maxNodes: 4096, maxFieldBytes: 4096, includeKeylessNodes: false));

        // Compares the first node's label to a value it never has: the wait
        // stays armed through every reevaluation.
        runtime.Bootstrap.RegisterPredicateContract(NeverSatisfied, new PredicateDefinition(
            ValueArray<PredicateClause>.From(new[]
            {
                new PredicateClause(
                    new ClauseId("c0"),
                    new ComparisonExpression(
                        new FieldPath("nodes/node-00000/attributes/label"),
                        ComparisonOperator.Eq,
                        PredicateOperand.Of("never"))),
            })));

        runtime.Start(new RefusingExecutor());
        var world = new BenchWorld(runtime);

        // Publish the source once and settle: an unpublished revision-bound
        // source materializes as `SourceUnavailable` incompleteness, and the
        // un-checkpointed bootstrap revisions would otherwise leak the initial
        // materialize+encode into the first measured operation.
        world.PublishInventory(0);
        world.PumpUntilIdle();
        return world;
    }

    public PumpReport Pump() =>
        Runtime.Pump(new PumpBudget(
            maxTurns: 64, deadline: long.MaxValue, new LogicalTime(100), FramePhase.Update));

    public void PumpUntilIdle(int maxPumps = 256)
    {
        for (var i = 0; i < maxPumps; i++)
        {
            if (!Pump().WorkRemaining)
            {
                return;
            }
        }

        throw new InvalidOperationException("The kernel did not become idle.");
    }

    /// <summary>Submits one interaction that terminates zero-effect (executor refusal).</summary>
    public void SubmitOne()
    {
        requestSequence++;
        Runtime.Ingress.Submit(new IntentSubmission(
            new RequestId("req-" + requestSequence.ToString(CultureInfo.InvariantCulture)),
            Invoke,
            TargetReference.ForKey(new AuthorKey("node-00000")),
            InvocationPayload.Empty,
            new IdentityEnvelope(Agent, IngressPath.Mcp, Provenance.Automation, Causality.Root()),
            NullSubmissionObserver.Instance));
    }

    /// <summary>Drives <paramref name="count"/> interactions to terminal, filling the RecoveryIndex.</summary>
    public void FillTerminals(int count)
    {
        for (var i = 0; i < count; i++)
        {
            SubmitOne();
            PumpUntilIdle();
        }
    }

    public OperationId RequestSnapshot() =>
        Runtime.Control.RequestSnapshot(AgentView, Agent, "root", NullSnapshotObserver.Instance);

    public void ReleaseSnapshot(OperationId operation) =>
        Runtime.Control.ReleaseSnapshot(operation);

    /// <summary>
    /// One verified snapshot round-trip; throws on refusal, truncation, or a
    /// wrong node count. A truncated snapshot would measure a smaller world
    /// than advertised — and keep the checkpoint feed retrying every pump.
    /// </summary>
    public void VerifySnapshotSucceeds(int expectedNodes)
    {
        var observer = new CountingSnapshotObserver();
        var operation = Runtime.Control.RequestSnapshot(AgentView, Agent, "root", observer);
        PumpUntilIdle();
        if (observer.PinnedCount != 1)
        {
            throw new InvalidOperationException("Expected exactly one pinned snapshot.");
        }

        var snapshot = observer.Last!;
        if (!snapshot.Snapshot.Completeness.IsComplete)
        {
            throw new InvalidOperationException(
                "The verification snapshot is incomplete; the bench world must materialize fully.");
        }

        if (snapshot.Materialization.Nodes.Count != expectedNodes)
        {
            throw new InvalidOperationException(
                $"Expected {expectedNodes} materialized nodes, got {snapshot.Materialization.Nodes.Count}.");
        }

        ReleaseSnapshot(operation);
        PumpUntilIdle();
    }

    public void ArmWaits(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Runtime.Control.ArmWait(
                NeverSatisfied, Agent, timeoutAtLogicalTime: long.MaxValue, NullWaitObserver.Instance);
        }

        PumpUntilIdle();
    }

    /// <summary>Publishes the same document again: a revision advance with unchanged content.</summary>
    public void PublishInventory(long count)
    {
        var answer = Runtime.Ingress.PublishSourceDocument(new SourcePublication(
            new StateSourceKey("inventory"),
            new SourceDocument(ValueArray<NamedField>.From(new[]
            {
                new NamedField("count", FieldValue.Of(count)),
            })),
            EventCausation.None));
        if (answer != PublicationAnswer.Accepted)
        {
            throw new InvalidOperationException("Publication refused: " + answer);
        }
    }
}
