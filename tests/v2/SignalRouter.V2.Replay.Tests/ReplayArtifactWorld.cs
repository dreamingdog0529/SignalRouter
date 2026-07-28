using System;
using System.Collections.Generic;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Codec.CanonicalState;
using SignalRouter.V2.Codec.Recording;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;
using SignalRouter.V2.Recording;

namespace SignalRouter.V2.Replay.Tests;

/// <summary>
/// Burns real artifacts for the pre-scan tests: a real kernel with the
/// production canonical-state codec and the durable coordinator — the same
/// vertical the recording E2E proves — so every scanned artifact is one the
/// system actually wrote.
/// </summary>
internal sealed class ReplayArtifactWorld
{
    internal static readonly SecurityDomainId AgentDomain = new("agent-domain");
    internal static readonly SecurityDomainId RecordDomain = new("record-domain");
    internal static readonly Principal Agent = new(Principal.WellKnownKinds.AgentSession, "agent-1");
    internal static readonly CapabilityContractRef Invoke =
        new(new CapabilityContractId("Invoke"), new ContractVersion(1, 0));
    internal static readonly CompletionProfileRef Applied =
        new(new CompletionProfileId("Applied"), new ContractVersion(1, 0));
    internal static readonly ViewContractRef RecordView =
        new(new ViewContractId("record-standard"), new ContractVersion(1, 0));
    internal static readonly PredicateContractRef CountIsFive =
        new(new PredicateContractId("countIsFive"), new ContractVersion(1, 0));

    internal static readonly ArtifactReadLimits Limits = new(
        maxArtifactBytes: 8L * 1024 * 1024,
        maxRecordCount: 4096,
        maxRecordBytes: 1024 * 1024,
        maxBlobBytes: 1024 * 1024,
        maxStringLength: 64 * 1024);

    internal static PredicateDefinition CountIsFiveDefinition() => new(
        ValueArray<PredicateClause>.From(new[]
        {
            new PredicateClause(
                new ClauseId("c0"),
                new ComparisonExpression(
                    new FieldPath("sources/inventory/count"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of(5L))),
        }));

    internal static ReplayComparisonProfile Profile(ContractVersion? version = null) => new(
        new ReplayComparisonProfileRef(
            new ReplayComparisonProfileId("strict"), version ?? new ContractVersion(1, 0)),
        RecordView,
        "root",
        new RedactionPolicyId("default-redaction"),
        ReplayComparisonProfile.MatchByAuthorKey,
        ValueArray<ComparedNodeRule>.Empty,
        ValueArray<ComparedSourceRule>.Empty,
        ValueArray<ItemKeyRule>.Empty,
        ValueArray<CollectionRule>.Empty,
        ValueArray<NormalizationRule>.Empty,
        // The world bootstraps a revision-bound source with no document, so the
        // base snapshot carries a SourceUnavailable completeness entry by
        // design; both sides share it, and the coinciding unknown region masks
        // out of the walk (recording-replay.md §5.2).
        requireCompleteForScope: false,
        ValueArray<ExtensionPolicy>.Empty,
        ValueArray<ContractVersion>.Empty);

    private sealed class Executor : IEffectExecutor
    {
        private IEffectCompletionSink? sink;

        internal List<EffectRequest> Requests { get; } = new();

        public void Attach(IEffectCompletionSink completionSink) => sink = completionSink;

        public void Detach() => sink = null;

        public EffectAdoption Execute(EffectRequest request)
        {
            Requests.Add(request);
            return EffectAdoption.Adopted;
        }

        public void RequestCancel(EffectPermitToken permit)
        {
        }

        internal void CompleteLast()
        {
            sink!.ReportCompletion(new EffectCompletion(
                Requests[^1].Permit,
                EffectResolution.Succeeded(new CompletionEvidence(
                    Applied, CompletionEvidenceKind.Applied, default)),
                continuations: null));
        }

        internal void CompleteCancelledLast()
        {
            sink!.ReportCompletion(new EffectCompletion(
                Requests[^1].Permit,
                EffectResolution.Cancelled(CancellationPhase.DuringEffect, "PartialEffect"),
                continuations: null));
        }
    }

    /// <summary>
    /// The deterministic replayable effect: publish the configured inventory
    /// count (request-caused — attributable, no barrier) and complete. The same
    /// executor drives the recording world and the replay twin, so effect
    /// ordering reproduces by construction.
    /// </summary>
    internal sealed class AutoCompletingExecutor : IEffectExecutor
    {
        private IEffectCompletionSink? sink;

        internal KernelRuntime? Runtime { get; set; }

        internal long? PublishCountOnEffect { get; set; }

        public void Attach(IEffectCompletionSink completionSink) => sink = completionSink;

        public void Detach() => sink = null;

        public EffectAdoption Execute(EffectRequest request)
        {
            if (PublishCountOnEffect.HasValue)
            {
                var answer = Runtime!.Ingress.PublishSourceDocument(new SourcePublication(
                    new StateSourceKey("inventory"),
                    new SourceDocument(ValueArray<NamedField>.From(new[]
                    {
                        new NamedField("count", FieldValue.Of(PublishCountOnEffect.Value)),
                    })),
                    EventCausation.OfRequest(request.Permit.Request)));
                Assert.That(answer, Is.EqualTo(PublicationAnswer.Accepted));
            }

            sink!.ReportCompletion(new EffectCompletion(
                request.Permit,
                EffectResolution.Succeeded(new CompletionEvidence(
                    Applied, CompletionEvidenceKind.Applied, default)),
                continuations: null));
            return EffectAdoption.Adopted;
        }

        public void RequestCancel(EffectPermitToken permit)
        {
        }
    }

    private sealed class LifecycleObserver : IRecordingObserver
    {
        internal List<string> Answers { get; } = new();

        public void OnOpened(OperationId recording) => Answers.Add("Opened");

        public void OnOpenRefused(OperationId recording, string reasonCode) =>
            Answers.Add("OpenRefused:" + reasonCode);

        public void OnClosed(OperationId recording, RecordingCloseReason reason) =>
            Answers.Add("Closed");

        public void OnFailed(OperationId recording, string reasonCode) =>
            Answers.Add("Failed:" + reasonCode);
    }

    private sealed class WaitObserver : IWaitObserver
    {
        public void OnResolved(OperationId operation, PredicateResolution resolution)
        {
        }
    }

    private sealed class AssertionObserver : IAssertionObserver
    {
        public void OnEvaluated(ValueArray<PredicateEvaluationResult> results)
        {
        }
    }

    private sealed class ManualClock : IMonotonicClock
    {
        public long Now => 0;
    }

    private readonly Executor executor = new();
    private readonly LifecycleObserver observer = new();
    private OperationId recording;
    private long logicalNow = 100;

    internal KernelRuntime Runtime { get; }

    internal MemoryArtifactStore Store { get; } = new();

    internal ReplayArtifactWorld(
        bool sensitiveArgument = false,
        ExternalMutationPolicy externalMutationPolicy = ExternalMutationPolicy.BarrierContinue,
        long? autoPublishCount = null)
    {
        var coordinator = new DurableEvidenceCoordinator(
            Store,
            new RecordingCoordinatorOptions(
                Profile(),
                allowNonDurableStore: true,
                externalMutationPolicy: externalMutationPolicy));
        Runtime = BuildRuntime(coordinator, sensitiveArgument, "incarnation-1");
        if (autoPublishCount.HasValue)
        {
            var auto = new AutoCompletingExecutor
            {
                Runtime = Runtime,
                PublishCountOnEffect = autoPublishCount,
            };
            Runtime.Start(auto);
        }
        else
        {
            Runtime.Start(executor);
        }
    }

    /// <summary>One bootstrap for the recording world and the replay twin: bootstrap equivalence by construction.</summary>
    internal static KernelRuntime BuildRuntime(
        IEvidenceCoordinator? coordinator, bool sensitiveArgument, string incarnation)
    {
        var options = new KernelOptions(
            new ManualClock(),
            new byte[] { 9, 9, 9, 9 },
            ValueArray<PrincipalDomainBinding>.From(new[]
            {
                new PrincipalDomainBinding(Principal.WellKnownKinds.AgentSession, AgentDomain),
            }),
            RecordDomain,
            canonicalStateCodec: new CanonicalStateCodec());
        var runtime = new KernelRuntime(new RuntimeIncarnationId(incarnation), options, coordinator);
        runtime.Bootstrap.RegisterCapabilityContract(new CapabilityContractDescriptor(
            Invoke,
            sensitiveArgument
                ? new ArgumentSchema(ValueArray<ArgumentField>.From(new[]
                {
                    new ArgumentField("token", FieldType.String, required: true, Sensitivity.Sensitive),
                }))
                : ArgumentSchema.Empty,
            precondition: null,
            Applied,
            postcondition: null));
        runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            RecordView, ViewFamily.Record, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        runtime.Bootstrap.RegisterNode(new NodeRegistration(
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
            new ExposurePolicy(ValueArray<SecurityDomainId>.From(new[]
            {
                AgentDomain, RecordDomain,
            }))));
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
        runtime.Bootstrap.RegisterPredicateContract(CountIsFive, CountIsFiveDefinition());
        return runtime;
    }

    internal void Open()
    {
        recording = Runtime.Recording.OpenRecording(
            new RecordingOpenRequest(
                Profile().Reference, RecordView, "root", new RedactionPolicyId("default-redaction")),
            observer);
        PumpUntilIdle();
        Assert.That(observer.Answers, Does.Contain("Opened"));
    }

    internal void Close()
    {
        Runtime.Recording.CloseRecording(recording, observer);
        PumpUntilIdle();
        Assert.That(observer.Answers, Does.Contain("Closed"));
    }

    internal void SubmitAuto(string request, InvocationPayload? payload = null)
    {
        // The auto-completing executor publishes and completes inside the pump.
        Runtime.Ingress.Submit(new IntentSubmission(
            new RequestId(request),
            Invoke,
            TargetReference.ForKey(new AuthorKey("save")),
            payload ?? InvocationPayload.Empty,
            new IdentityEnvelope(Agent, IngressPath.Mcp, Provenance.Automation, Causality.Root()),
            observer: null));
        PumpUntilIdle();
    }

    internal void ArmWait()
    {
        Runtime.Control.ArmWait(CountIsFive, Agent, long.MaxValue, new WaitObserver());
        PumpUntilIdle();
    }

    internal void SubmitAndComplete(string request, InvocationPayload? payload = null)
    {
        Runtime.Ingress.Submit(new IntentSubmission(
            new RequestId(request),
            Invoke,
            TargetReference.ForKey(new AuthorKey("save")),
            payload ?? InvocationPayload.Empty,
            new IdentityEnvelope(Agent, IngressPath.Mcp, Provenance.Automation, Causality.Root()),
            observer: null));
        PumpUntilIdle();
        executor.CompleteLast();
        PumpUntilIdle();
    }

    internal void SubmitWithoutCompleting(string request)
    {
        Runtime.Ingress.Submit(new IntentSubmission(
            new RequestId(request),
            Invoke,
            TargetReference.ForKey(new AuthorKey("save")),
            InvocationPayload.Empty,
            new IdentityEnvelope(Agent, IngressPath.Mcp, Provenance.Automation, Causality.Root()),
            observer: null));
        PumpUntilIdle();
    }

    internal void CompletePending()
    {
        executor.CompleteLast();
        PumpUntilIdle();
    }

    internal void CompleteCancelled()
    {
        // The adapter honors a mid-effect cancel: the effect terminates
        // Cancelled(DuringEffect) after having started.
        PumpUntilIdle();
        executor.CompleteCancelledLast();
        PumpUntilIdle();
    }

    internal void ReportExternal(string hint)
    {
        Runtime.Ingress.ReportObservedExternal(new ObservedExternalReport(hint, null, null));
        PumpUntilIdle();
    }

    internal void SubmitAndCancelBeforeEffect(string request)
    {
        // Admit in exactly one turn, then cancel while the interaction still
        // waits in the admitted queue — a BeforeEffect cancellation with no
        // permit ever minted.
        Runtime.Ingress.Submit(new IntentSubmission(
            new RequestId(request),
            Invoke,
            TargetReference.ForKey(new AuthorKey("save")),
            InvocationPayload.Empty,
            new IdentityEnvelope(Agent, IngressPath.Mcp, Provenance.Automation, Causality.Root()),
            observer: null));
        Runtime.Pump(new PumpBudget(
            1, long.MaxValue, new LogicalTime(logicalNow++), FramePhase.Update));
        Runtime.Control.RequestCancel(new RequestId(request));
        PumpUntilIdle();
    }

    internal void TearDown()
    {
        Runtime.Control.TearDownIncarnation();
        PumpUntilIdle();
    }

    internal void ArmWaitWithTimeout(long timeoutAtLogicalTime)
    {
        Runtime.Control.ArmWait(CountIsFive, Agent, timeoutAtLogicalTime, new WaitObserver());
        PumpUntilIdle();
    }

    internal void AdvanceLogicalTime(long to)
    {
        logicalNow = to;
        PumpUntilIdle();
    }

    internal void EvaluateAssertion()
    {
        Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(new[] { CountIsFive }),
            Agent,
            new AssertionObserver()));
        PumpUntilIdle();
    }

    internal void PublishCount(long count)
    {
        var answer = Runtime.Ingress.PublishSourceDocument(new SourcePublication(
            new StateSourceKey("inventory"),
            new SourceDocument(ValueArray<NamedField>.From(new[]
            {
                new NamedField("count", FieldValue.Of(count)),
            })),
            EventCausation.OfRequest(new RequestId("pub-cause"))));
        Assert.That(answer, Is.EqualTo(PublicationAnswer.Accepted));
        PumpUntilIdle();
    }

    internal byte[] Artifact() => Store.ReadAll(recording.Value, Limits.MaxArtifactBytes);

    private void PumpUntilIdle(int maxPumps = 24)
    {
        for (var i = 0; i < maxPumps; i++)
        {
            var report = Runtime.Pump(new PumpBudget(
                64, long.MaxValue, new LogicalTime(logicalNow++), FramePhase.Update));
            if (!report.WorkRemaining)
            {
                return;
            }
        }

        throw new InvalidOperationException("The kernel did not become idle.");
    }
}
