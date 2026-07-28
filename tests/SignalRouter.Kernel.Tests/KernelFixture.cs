using System;
using System.Collections.Generic;
using NUnit.Framework;
using SignalRouter.AdapterSdk;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel.Tests;

/// <summary>Disambiguates NUnit's Assert.Throws overloads for lambda arguments.</summary>
internal static class AssertEx
{
    internal static TException Throws<TException>(Action action)
        where TException : Exception =>
        Assert.Throws<TException>(action)!;
}

/// <summary>A test-controlled monotonic clock.</summary>
internal sealed class ManualClock : IMonotonicClock
{
    internal long Value;

    public long Now => Value;
}

/// <summary>Records split-phase admission answers.</summary>
internal sealed class RecordingObserver : ISubmissionObserver
{
    internal List<RequestId> Accepted { get; } = new();

    internal List<(RequestId Request, RejectionReason Reason)> Rejected { get; } = new();

    public void OnAccepted(RequestId request) => Accepted.Add(request);

    public void OnRejected(RequestId request, RejectionReason reason) => Rejected.Add((request, reason));
}

/// <summary>Records wait resolutions.</summary>
internal sealed class RecordingWaitObserver : IWaitObserver
{
    internal List<(OperationId Operation, PredicateResolution Resolution)> Resolutions { get; } = new();

    public void OnResolved(OperationId operation, PredicateResolution resolution) =>
        Resolutions.Add((operation, resolution));
}

/// <summary>Records assertion batch answers.</summary>
internal sealed class RecordingAssertionObserver : IAssertionObserver
{
    internal ValueArray<PredicateEvaluationResult>? Results { get; private set; }

    public void OnEvaluated(ValueArray<PredicateEvaluationResult> results) => Results = results;
}

/// <summary>
/// A deterministic test canonical-state codec: a stable string rendering of the
/// materialization, FNV-1a-64 hashed. Legitimate because the Contracts-level
/// meaning of ContentId is reference + equality only; real canonical encoding
/// arrives with Codec.CanonicalState (item 4).
/// </summary>
internal sealed class TestCanonicalStateCodec : ICanonicalStateCodec
{
    public CanonicalStateResult Encode(ObservationMaterialization materialization)
    {
        var text = new System.Text.StringBuilder();
        var basis = materialization.Basis;
        text.Append(basis.Incarnation).Append('|').Append(basis.Revision.Value)
            .Append('|').Append(basis.View).Append('|').Append(basis.Domain)
            .Append('|').Append(basis.Scope).Append('\n');
        foreach (var node in materialization.Nodes)
        {
            text.Append("n:").Append(node.Key.Value).Append('|').Append(node.Role)
                .Append('|').Append(node.Parent?.Value ?? "-")
                .Append('|').Append(node.VisibleChildCount);
            foreach (var attribute in node.Attributes)
            {
                text.Append('|').Append(attribute.Name).Append('=')
                    .Append(attribute.Redacted ? "<redacted>" : attribute.Value.ToString());
            }

            foreach (var capability in node.Capabilities)
            {
                text.Append('|').Append(capability.Contract).Append(':').Append(capability.Available);
            }

            text.Append('\n');
        }

        foreach (var source in materialization.Sources)
        {
            text.Append("s:").Append(source.Key.Value).Append('|').Append(source.Omission?.ToString() ?? "-");
            foreach (var field in source.Fields)
            {
                text.Append('|').Append(field.Name).Append('=').Append(field.Value.ToString());
            }

            text.Append('\n');
        }

        text.Append("c:").Append(materialization.Completeness.ToString());
        var payload = System.Text.Encoding.UTF8.GetBytes(text.ToString());

        var hash = 14695981039346656037UL;
        foreach (var b in payload)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        var digest = new byte[8];
        for (var i = 0; i < 8; i++)
        {
            digest[i] = (byte)(hash >> (8 * i));
        }

        return new CanonicalStateResult(
            new ContentId("fnv1a64", 1, DigestValue.From(digest)), payload);
    }
}

/// <summary>Records split-phase snapshot answers.</summary>
internal sealed class RecordingSnapshotObserver : ISnapshotObserver
{
    internal List<(OperationId Operation, PinnedSnapshot Snapshot)> Pinned { get; } = new();

    internal List<(OperationId Operation, string Reason)> Refused { get; } = new();

    public void OnPinned(OperationId operation, PinnedSnapshot snapshot) =>
        Pinned.Add((operation, snapshot));

    public void OnRefused(OperationId operation, string reasonCode) =>
        Refused.Add((operation, reasonCode));
}

/// <summary>
/// A scripted effect executor: adopts (or refuses/throws) synchronously and lets
/// tests deliver fences and completions through the attached sink.
/// </summary>
internal sealed class ScriptedExecutor : IEffectExecutor
{
    internal enum Mode
    {
        Capture,
        Refuse,
        Throw,
    }

    private IEffectCompletionSink? sink;

    internal Mode Behavior { get; set; } = Mode.Capture;

    internal FaultCode RefusalCode { get; set; } = new("TargetGone");

    internal List<EffectRequest> Requests { get; } = new();

    internal List<EffectPermitToken> CancelRequests { get; } = new();

    internal Action<EffectRequest>? OnExecute { get; set; }

    internal IEffectCompletionSink Sink => sink ?? throw new InvalidOperationException("Not attached.");

    public void Attach(IEffectCompletionSink completionSink) => sink = completionSink;

    public void Detach() => sink = null;

    public EffectAdoption Execute(EffectRequest request)
    {
        Requests.Add(request);
        OnExecute?.Invoke(request);
        return Behavior switch
        {
            Mode.Refuse => EffectAdoption.Refused(RefusalCode),
            Mode.Throw => throw new InvalidOperationException("executor exploded"),
            _ => EffectAdoption.Adopted,
        };
    }

    public void RequestCancel(EffectPermitToken permit) => CancelRequests.Add(permit);

    internal void CompleteLast(EffectResolution resolution, ValueArray<ContinuationRequest>? continuations = null)
    {
        Sink.ReportCompletion(new EffectCompletion(Requests[^1].Permit, resolution, continuations));
    }

    internal void FenceLast()
    {
        Sink.ReportFenceReached(Requests[^1].Permit);
    }
}

/// <summary>
/// The tier-1 kernel fixture: a bootstrapped runtime with a standard world —
/// expectations in tests are transcribed from the spec, never derived from kernel
/// internals.
/// </summary>
internal sealed class KernelFixture
{
    internal static readonly SecurityDomainId AgentDomain = new("agent-domain");
    internal static readonly SecurityDomainId HumanDomain = new("human-domain");
    internal static readonly SecurityDomainId RecordDomain = new("record-domain");

    internal static readonly Principal Agent = new(Principal.WellKnownKinds.AgentSession, "agent-1");
    internal static readonly Principal Human = new(Principal.WellKnownKinds.LocalUser, "user-1");

    internal static readonly CapabilityContractRef Invoke =
        new(new CapabilityContractId("Invoke"), new ContractVersion(1, 0));

    internal static readonly CompletionProfileRef Applied =
        new(new CompletionProfileId("Applied"), new ContractVersion(1, 0));

    internal static readonly PredicateContractRef LabelExists =
        new(new PredicateContractId("labelExists"), new ContractVersion(1, 0));

    internal ManualClock Clock { get; } = new();

    internal ScriptedExecutor Executor { get; } = new();

    internal KernelRuntime Runtime { get; }

    internal NodeRef SaveNode { get; }

    internal long LogicalNow { get; set; } = 100;

    internal KernelFixture(
        int mutationCapacity = 256,
        int pendingCapacity = 4096,
        int terminalCapacity = 4096,
        long terminalRetention = 300,
        int traceCapacity = 8192,
        int perSourcePending = 16,
        PredicateDefinition? invokePrecondition = null,
        PredicateDefinition? invokePostcondition = null,
        ArgumentSchema? invokeSchema = null,
        CompletionProfileRef? invokeProfile = null,
        IEvidenceCoordinator? coordinator = null,
        int observationBudgetBytes = 256 * 1024,
        int observationBudgetNodes = 2048,
        int maxPinnedSnapshots = 32,
        int maxObservationFieldBytes = 4096,
        ICanonicalStateCodec? codec = null,
        int stateStoreMaxBlobBytes = 1024 * 1024,
        long stateStoreMaxTotalBytes = 64L * 1024 * 1024,
        int timelineRetentionEntries = 128,
        long timelineRetentionBytes = 8L * 1024 * 1024,
        bool start = true)
    {
        var options = new KernelOptions(
            Clock,
            new byte[] { 1, 2, 3, 4 },
            ValueArray<PrincipalDomainBinding>.From(new[]
            {
                new PrincipalDomainBinding(Principal.WellKnownKinds.AgentSession, AgentDomain),
                new PrincipalDomainBinding(Principal.WellKnownKinds.LocalUser, HumanDomain),
                new PrincipalDomainBinding(Principal.WellKnownKinds.TestHarness, RecordDomain),
            }),
            RecordDomain,
            mailboxMutationCapacity: mutationCapacity,
            recoveryIndexPendingCapacity: pendingCapacity,
            recoveryIndexTerminalCapacity: terminalCapacity,
            recoveryIndexTerminalRetentionLogicalTime: terminalRetention,
            traceRingCapacity: traceCapacity,
            sourcePublicationPendingPerSource: perSourcePending,
            observationBudgetBytes: observationBudgetBytes,
            observationBudgetNodes: observationBudgetNodes,
            maxPinnedSnapshots: maxPinnedSnapshots,
            maxObservationFieldBytes: maxObservationFieldBytes,
            canonicalStateCodec: codec,
            stateStoreMaxBlobBytes: stateStoreMaxBlobBytes,
            stateStoreMaxTotalBytes: stateStoreMaxTotalBytes,
            timelineRetentionEntries: timelineRetentionEntries,
            timelineRetentionBytes: timelineRetentionBytes);
        Runtime = new KernelRuntime(new RuntimeIncarnationId("incarnation-1"), options, coordinator);

        var visibleToAll = new ExposurePolicy(ValueArray<SecurityDomainId>.From(new[]
        {
            AgentDomain, HumanDomain, RecordDomain,
        }));

        Runtime.Bootstrap.RegisterCapabilityContract(new CapabilityContractDescriptor(
            Invoke, invokeSchema ?? ArgumentSchema.Empty, invokePrecondition,
            invokeProfile ?? Applied, invokePostcondition));

        SaveNode = Runtime.Bootstrap.RegisterNode(new NodeRegistration(
            new AuthorKey("save"),
            NodeRole.Button,
            parent: null,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Save"), Sensitivity.Standard),
            }),
            ValueArray<CapabilityDeclaration>.From(new[]
            {
                new CapabilityDeclaration(Invoke, initiallyAvailable: true),
            }),
            visibleToAll));

        // A registered-but-hidden node: exposure default-deny.
        Runtime.Bootstrap.RegisterNode(new NodeRegistration(
            new AuthorKey("secret"),
            NodeRole.Button,
            parent: null,
            ValueArray<NodeAttribute>.Empty,
            ValueArray<CapabilityDeclaration>.From(new[]
            {
                new CapabilityDeclaration(Invoke, initiallyAvailable: true),
            }),
            ExposurePolicy.Hidden));

        Runtime.Bootstrap.RegisterStateSource(new StateSourceRegistration(
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
            StateSourceClass.RevisionBound));

        Runtime.Bootstrap.RegisterPredicateContract(LabelExists, new PredicateDefinition(
            ValueArray<PredicateClause>.From(new[]
            {
                new PredicateClause(
                    new ClauseId("c0"),
                    new ComparisonExpression(
                        new FieldPath("nodes/save/attributes/label"),
                        ComparisonOperator.Eq,
                        PredicateOperand.Of("Saved"))),
            })));

        if (start)
        {
            Runtime.Start(Executor);
        }
    }

    internal RecordingObserver Submit(
        string request,
        string targetKey = "save",
        Principal? principal = null,
        Provenance provenance = Provenance.Automation,
        InvocationPayload? payload = null)
    {
        var observer = new RecordingObserver();
        Runtime.Ingress.Submit(new IntentSubmission(
            new RequestId(request),
            Invoke,
            TargetReference.ForKey(new AuthorKey(targetKey)),
            payload ?? InvocationPayload.Empty,
            new IdentityEnvelope(
                principal ?? Agent,
                IngressPath.Mcp,
                provenance,
                Causality.Root()),
            observer));
        return observer;
    }

    internal PumpReport Pump(int maxTurns = 64)
    {
        return Runtime.Pump(new PumpBudget(
            maxTurns, deadline: long.MaxValue, new LogicalTime(LogicalNow), FramePhase.Update));
    }

    internal void PumpUntilIdle(int maxPumps = 16)
    {
        for (var i = 0; i < maxPumps; i++)
        {
            var report = Pump();
            if (!report.WorkRemaining)
            {
                return;
            }
        }

        throw new InvalidOperationException("The kernel did not become idle.");
    }

    internal QueryAnswer Query(string request, Principal? principal = null) =>
        Runtime.Queries.Query(new RequestId(request), principal ?? Agent);

    internal List<string> TraceKinds()
    {
        var kinds = new List<string>();
        foreach (var semanticEvent in Runtime.Trace.Snapshot())
        {
            kinds.Add(semanticEvent.Kind.Value +
                (semanticEvent.DetailCode == null ? "" : ":" + semanticEvent.DetailCode));
        }

        return kinds;
    }

    internal void PublishInventory(long count, EventCausation? causation = null)
    {
        var answer = Runtime.Ingress.PublishSourceDocument(new SourcePublication(
            new StateSourceKey("inventory"),
            new SourceDocument(ValueArray<NamedField>.From(new[]
            {
                new NamedField("count", FieldValue.Of(count)),
            })),
            causation ?? EventCausation.None));
        Assert.That(answer, Is.EqualTo(PublicationAnswer.Accepted));
    }
}
