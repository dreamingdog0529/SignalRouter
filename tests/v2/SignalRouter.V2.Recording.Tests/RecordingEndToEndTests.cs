using System;
using System.Collections.Generic;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Codec.CanonicalState;
using SignalRouter.V2.Codec.Recording;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;

namespace SignalRouter.V2.Recording.Tests;

/// <summary>
/// The vertical proof of the recording track (ADR 0015/0016): a real kernel
/// with the production canonical-state codec, the durable coordinator, and the
/// artifact codec — record a session, read the artifact back, and let the real
/// EvidenceSemantics tables classify it. Failure rows: an E4 sink fault leaves
/// the true terminal standing while the artifact degrades; capacity closes
/// Incomplete(SizeLimit); a non-durable store is refused at open.
/// </summary>
public sealed class RecordingEndToEndTests
{
    private static readonly SecurityDomainId AgentDomain = new("agent-domain");
    private static readonly SecurityDomainId RecordDomain = new("record-domain");
    private static readonly Principal Agent = new(Principal.WellKnownKinds.AgentSession, "agent-1");
    private static readonly CapabilityContractRef Invoke =
        new(new CapabilityContractId("Invoke"), new ContractVersion(1, 0));
    private static readonly CompletionProfileRef Applied =
        new(new CompletionProfileId("Applied"), new ContractVersion(1, 0));
    private static readonly ViewContractRef RecordView =
        new(new ViewContractId("record-standard"), new ContractVersion(1, 0));

    private static readonly PredicateContractRef CountIsFive =
        new(new PredicateContractId("countIsFive"), new ContractVersion(1, 0));

    private static PredicateDefinition CountIsFiveDefinition() => new(
        ValueArray<PredicateClause>.From(new[]
        {
            new PredicateClause(
                new ClauseId("c0"),
                new ComparisonExpression(
                    new FieldPath("sources/inventory/count"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of(5L))),
        }));

    private static readonly ArtifactReadLimits Limits = new(
        maxArtifactBytes: 8L * 1024 * 1024,
        maxRecordCount: 4096,
        maxRecordBytes: 1024 * 1024,
        maxBlobBytes: 1024 * 1024,
        maxStringLength: 64 * 1024);

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
    }

    private sealed class Observer : IRecordingObserver
    {
        internal List<string> Answers { get; } = new();

        internal RecordingCloseReason? ClosedReason { get; private set; }

        public void OnOpened(OperationId recording) => Answers.Add("Opened");

        public void OnOpenRefused(OperationId recording, string reasonCode) =>
            Answers.Add("OpenRefused:" + reasonCode);

        public void OnClosed(OperationId recording, RecordingCloseReason reason)
        {
            ClosedReason = reason;
            Answers.Add("Closed");
        }

        public void OnFailed(OperationId recording, string reasonCode) =>
            Answers.Add("Failed:" + reasonCode);
    }

    private sealed class World
    {
        internal KernelRuntime Runtime { get; }

        internal Executor Executor { get; } = new();

        internal MemoryArtifactStore Store { get; } = new();

        internal DurableEvidenceCoordinator Coordinator { get; }

        private long logicalNow = 100;

        internal World(
            RecordingCoordinatorOptions? options = null,
            ISampledSourceReader? sampledReader = null)
        {
            Coordinator = new DurableEvidenceCoordinator(
                Store,
                options ?? new RecordingCoordinatorOptions(Profile(), allowNonDurableStore: true));
            var kernelOptions = new KernelOptions(
                new ManualClock(),
                new byte[] { 9, 9, 9, 9 },
                ValueArray<PrincipalDomainBinding>.From(new[]
                {
                    new PrincipalDomainBinding(Principal.WellKnownKinds.AgentSession, AgentDomain),
                }),
                RecordDomain,
                canonicalStateCodec: new CanonicalStateCodec());
            Runtime = new KernelRuntime(
                new RuntimeIncarnationId("incarnation-1"), kernelOptions, Coordinator);
            Runtime.Bootstrap.RegisterCapabilityContract(new CapabilityContractDescriptor(
                Invoke, ArgumentSchema.Empty, precondition: null, Applied, postcondition: null));
            Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
                RecordView, ViewFamily.Record, "root",
                maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
            Runtime.Bootstrap.RegisterNode(new NodeRegistration(
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
            Runtime.Bootstrap.RegisterStateSource(new StateSourceRegistration(
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
            Runtime.Bootstrap.RegisterPredicateContract(CountIsFive, CountIsFiveDefinition());
            if (sampledReader != null)
            {
                Runtime.Bootstrap.RegisterStateSource(new StateSourceRegistration(
                    new StateSourceKey("gauge"),
                    new StateSourceContractDescriptor(
                        new StateSourceContractRef(
                            new StateSourceContractId("gauge"), new ContractVersion(1, 0)),
                        ValueArray<SourceFieldSchema>.From(new[]
                        {
                            new SourceFieldSchema("level", FieldType.Integer, Sensitivity.Standard),
                        }),
                        agentVisible: true,
                        recordVisible: true,
                        maxDocumentBytes: 256),
                    StateSourceClass.Sampled,
                    sampledReader,
                    freshnessBoundLogicalTime: long.MaxValue));
            }

            Runtime.Start(Executor);
        }

        internal void PublishCount(long count, EventCausation causation)
        {
            var answer = Runtime.Ingress.PublishSourceDocument(new SourcePublication(
                new StateSourceKey("inventory"),
                new SourceDocument(ValueArray<NamedField>.From(new[]
                {
                    new NamedField("count", FieldValue.Of(count)),
                })),
                causation));
            Assert.That(answer, Is.EqualTo(PublicationAnswer.Accepted));
        }

        internal void Submit(string request) => Runtime.Ingress.Submit(new IntentSubmission(
            new RequestId(request),
            Invoke,
            TargetReference.ForKey(new AuthorKey("save")),
            InvocationPayload.Empty,
            new IdentityEnvelope(Agent, IngressPath.Mcp, Provenance.Automation, Causality.Root()),
            observer: null));

        internal void PumpOnce() => Runtime.Pump(new PumpBudget(
            64, long.MaxValue, new LogicalTime(logicalNow++), FramePhase.Update));

        internal void PumpUntilIdle(int maxPumps = 24)
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

        private sealed class ManualClock : IMonotonicClock
        {
            public long Now => 0;
        }
    }

    private static ReplayComparisonProfile Profile() => new(
        new ReplayComparisonProfileRef(
            new ReplayComparisonProfileId("strict"), new ContractVersion(1, 0)),
        RecordView,
        "root",
        new RedactionPolicyId("default-redaction"),
        ReplayComparisonProfile.MatchByAuthorKey,
        ValueArray<ComparedNodeRule>.Empty,
        ValueArray<ComparedSourceRule>.Empty,
        ValueArray<ItemKeyRule>.Empty,
        ValueArray<CollectionRule>.Empty,
        ValueArray<NormalizationRule>.Empty,
        requireCompleteForScope: true,
        ValueArray<ExtensionPolicy>.Empty,
        ValueArray<ContractVersion>.Empty);

    private static RecordingOpenRequest OpenRequest() => new(
        Profile().Reference, RecordView, "root", new RedactionPolicyId("default-redaction"));

    [Test]
    public void ARecordedSessionReadsBackAndClassifiesCompleted()
    {
        var world = new World();
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened" }));

        world.Submit("r-1");
        world.PumpUntilIdle();
        world.Executor.CompleteLast();
        world.PumpUntilIdle();

        world.Runtime.Recording.CloseRecording(recording, observer);
        world.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(observer.ClosedReason!.Value.IsCompleted, Is.True);

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.TruncatedTail, Is.False);
        Assert.That(result.IntegrityFailure, Is.False, result.IntegrityDetail);
        Assert.That(result.Profile, Is.Not.Null);
        Assert.That(
            result.Cuts.Count, Is.EqualTo(5),
            "E1, E2, E3, E4, E7 — one admitted interaction");
        Assert.That(result.Cuts[0], Is.InstanceOf<RecordingOpened>());
        Assert.That(result.Cuts[1], Is.InstanceOf<AdmissionCut>());
        Assert.That(result.Cuts[2], Is.InstanceOf<EffectPermit>());
        Assert.That(result.Cuts[3], Is.InstanceOf<TerminalCut>());
        Assert.That(result.Cuts[4], Is.InstanceOf<RecordingClosed>());

        var classification = EvidenceSemantics.ClassifyArtifact(result.Facts);
        Assert.That(
            classification.Outcome.Kind, Is.EqualTo(RecordingOutcomeKind.Completed),
            "the vertical proof: record → read → the real decision tables answer Completed");

        var terminal = (TerminalCut)result.Cuts[3];
        Assert.That(terminal.Outcome, Is.EqualTo(InteractionOutcome.Succeeded));
        Assert.That(terminal.CompletionEvidence, Is.Not.Null);
        Assert.That(result.TryGetBlob(terminal.AfterView, out _), Is.True,
            "the after-view blob is carried and digest-verified");
    }

    [Test]
    public void PreOpenWorkIsVacuousAndNeverEntersTheArtifact()
    {
        var world = new World();
        world.Submit("r-before");
        world.PumpUntilIdle();
        world.Executor.CompleteLast();
        world.PumpUntilIdle();

        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();
        world.Runtime.Recording.CloseRecording(recording, observer);
        world.PumpUntilIdle();

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.Cuts.Count, Is.EqualTo(2), "E1 and E7 only — pre-open cuts persist nowhere");
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(result.Facts).Outcome.Kind,
            Is.EqualTo(RecordingOutcomeKind.Completed));
    }

    [Test]
    public void AnE4SinkFaultLeavesTheTerminalStandingAndDegradesTheArtifact()
    {
        var world = new World();
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();

        world.Submit("r-1");
        world.PumpUntilIdle();

        // The after view materializes under the recording's registered view, so
        // an unchanged state dedupes against the E3 before blob — the next
        // append is the E4 cut itself: script it to fault.
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.Fault); // E4 cut
        world.Executor.CompleteLast();
        world.PumpUntilIdle();

        // The true terminal stands (guarantees.md §7)...
        var query = world.Runtime.Queries.Query(new RequestId("r-1"), Agent);
        Assert.That(query, Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));

        // ...and the coordinator's degradation channel closed the artifact.
        world.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(observer.ClosedReason!.Value.IsCompleted, Is.False);
        Assert.That(observer.ClosedReason.Value.Reason.Value, Is.EqualTo("SinkFault"));

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(result.Facts).Outcome.Kind,
            Is.Not.EqualTo(RecordingOutcomeKind.Completed));
    }

    [Test]
    public void CapacityClosesIncompleteSizeLimit()
    {
        var world = new World(new RecordingCoordinatorOptions(
            Profile(),
            maxArtifactBytes: 4096,
            maxEventCount: 4,
            allowNonDurableStore: true));
        var observer = new Observer();
        world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();

        // E1 consumed one cut; each interaction consumes E2+E3+E4.
        world.Submit("r-1");
        world.PumpUntilIdle();
        world.Executor.CompleteLast();
        world.PumpUntilIdle();
        world.Submit("r-2");
        world.PumpUntilIdle();
        if (world.Executor.Requests.Count > 1)
        {
            world.Executor.CompleteLast();
        }

        world.PumpUntilIdle();

        Assert.That(observer.Answers, Does.Contain("Closed"));
        Assert.That(observer.ClosedReason!.Value.IsCompleted, Is.False);
        Assert.That(observer.ClosedReason.Value.Reason.Value, Is.EqualTo("SizeLimit"));
    }

    private sealed class WaitObserver : IWaitObserver
    {
        internal List<PredicateResolution> Resolutions { get; } = new();

        public void OnResolved(OperationId operation, PredicateResolution resolution) =>
            Resolutions.Add(resolution);
    }

    private sealed class AssertionObserver : IAssertionObserver
    {
        internal ValueArray<PredicateEvaluationResult>? Results { get; private set; }

        public void OnEvaluated(ValueArray<PredicateEvaluationResult> results) => Results = results;
    }

    [Test]
    public void WaitAssertionAndBarrierEvidenceReadsBackAndClassifiesCompleted()
    {
        var world = new World();
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened" }));

        // E6a: the wait arms unsatisfied (no inventory document yet).
        var waitObserver = new WaitObserver();
        world.Runtime.Control.ArmWait(
            CountIsFive, Agent, timeoutAtLogicalTime: long.MaxValue, waitObserver);
        world.PumpUntilIdle();
        Assert.That(waitObserver.Resolutions, Is.Empty);

        // E6b: a request-caused publication (controlled work — no barrier)
        // satisfies the wait.
        world.PublishCount(5, EventCausation.OfRequest(new RequestId("r-cause")));
        world.PumpUntilIdle();
        Assert.That(waitObserver.Resolutions, Is.EqualTo(new[] { PredicateResolution.Satisfied }));

        // E8: an evidence-bearing assertion against the record projection.
        var assertionObserver = new AssertionObserver();
        world.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(new[] { CountIsFive }),
            Agent,
            assertionObserver));
        world.PumpUntilIdle();
        Assert.That(
            assertionObserver.Results!.Value[0].Outcome,
            Is.EqualTo(PredicateEvaluationOutcome.Satisfied));

        // E5: an observed external effect, then an orderly close.
        world.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("external-effect", null, null));
        world.Runtime.Recording.CloseRecording(recording, observer);
        world.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(observer.ClosedReason!.Value.IsCompleted, Is.True);

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.TruncatedTail, Is.False);
        Assert.That(result.IntegrityFailure, Is.False, result.IntegrityDetail);
        Assert.That(result.Cuts.Count, Is.EqualTo(6), "E1, E6a, E6b, E8, E5, E7");
        Assert.That(result.Cuts[0], Is.InstanceOf<RecordingOpened>());
        Assert.That(result.Cuts[1], Is.InstanceOf<PredicateArmed>());
        Assert.That(result.Cuts[2], Is.InstanceOf<PredicateResolved>());
        Assert.That(result.Cuts[3], Is.InstanceOf<AssertionEvaluated>());
        Assert.That(result.Cuts[4], Is.InstanceOf<ExternalMutationBarrier>());
        Assert.That(result.Cuts[5], Is.InstanceOf<RecordingClosed>());

        var armed = (PredicateArmed)result.Cuts[1];
        Assert.That(armed.Predicate, Is.EqualTo(CountIsFive));
        Assert.That(
            armed.Operands,
            Is.EqualTo(PredicateCanonicalizer.DigestOf(CountIsFiveDefinition())),
            "replay verifies the digest against its allowlisted catalog (ADR 0015)");
        Assert.That(armed.Scope, Is.EqualTo(RecordView));
        Assert.That(armed.ObservationScope, Is.EqualTo("root"));
        Assert.That(armed.ArmedSequence, Is.EqualTo(new ViewSequence(0)));

        var resolved = (PredicateResolved)result.Cuts[2];
        Assert.That(resolved.Outcome, Is.EqualTo(PredicateResolution.Satisfied));
        Assert.That(resolved.ResolvedSequence, Is.EqualTo(new ViewSequence(1)));
        Assert.That(
            result.TryGetBlob(resolved.WitnessOrFinalObservation, out _), Is.True,
            "the witness blob is carried and digest-verified (guarantees.md §5.6)");

        var assertion = (AssertionEvaluated)result.Cuts[3];
        Assert.That(assertion.Outcome, Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
        Assert.That(assertion.View, Is.EqualTo(RecordView));
        Assert.That(
            result.TryGetBlob(assertion.Snapshot, out _), Is.True,
            "the evaluated snapshot blob is carried (guarantees.md §5.10)");

        var barrier = (ExternalMutationBarrier)result.Cuts[4];
        Assert.That(barrier.SourceHint, Is.EqualTo("external-effect"));
        Assert.That(barrier.LastKnownCleanCut, Is.EqualTo(new EvidenceSequence(3)));
        Assert.That(barrier.FirstObservedCut, Is.EqualTo(new EvidenceSequence(4)));
        Assert.That(barrier.ContaminatedRequests.Count, Is.EqualTo(0));

        Assert.That(
            EvidenceSemantics.ClassifyArtifact(result.Facts).Outcome.Kind,
            Is.EqualTo(RecordingOutcomeKind.Completed),
            "barrier-continue keeps the artifact structurally complete (guarantees.md §5.5)");
    }

    [Test]
    public void TheTerminatePolicyClosesIncompleteExternalMutation()
    {
        var world = new World(new RecordingCoordinatorOptions(
            Profile(),
            allowNonDurableStore: true,
            externalMutationPolicy: ExternalMutationPolicy.Terminate));
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();

        world.Runtime.Ingress.ReportObservedExternal(
            new ObservedExternalReport("external-effect", null, null));
        world.PumpUntilIdle();

        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(observer.ClosedReason!.Value.IsCompleted, Is.False);
        Assert.That(observer.ClosedReason.Value.Reason.Value, Is.EqualTo("ExternalMutation"));

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.Cuts.Count, Is.EqualTo(3), "E1, E5, E7");
        Assert.That(result.Cuts[1], Is.InstanceOf<ExternalMutationBarrier>());
        var classification = EvidenceSemantics.ClassifyArtifact(result.Facts);
        Assert.That(classification.Outcome.Kind, Is.EqualTo(RecordingOutcomeKind.Incomplete));
    }

    [Test]
    public void ANonDurableStoreIsRefusedAtOpenByDefault()
    {
        var world = new World(new RecordingCoordinatorOptions(Profile()));
        var observer = new Observer();
        world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "OpenRefused:OpenFailed" }));
    }

    [Test]
    public void AMismatchedProfileReferenceRefusesTheOpen()
    {
        var world = new World();
        var observer = new Observer();
        world.Runtime.Recording.OpenRecording(
            new RecordingOpenRequest(
                new ReplayComparisonProfileRef(
                    new ReplayComparisonProfileId("other"), new ContractVersion(1, 0)),
                RecordView, "root", new RedactionPolicyId("default-redaction")),
            observer);
        world.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "OpenRefused:OpenFailed" }));
    }

    // ── Delta production and the invalidation token (1.1) ────────────────────

    private (ArtifactReadResult Result, Observer Observer) RecordMutatingSession(World world)
    {
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened" }));

        world.PublishCount(1, EventCausation.OfRequest(new RequestId("r-seed")));
        world.PumpUntilIdle();
        world.Submit("r-1");
        world.PumpUntilIdle();
        world.PublishCount(2, EventCausation.OfRequest(new RequestId("r-1")));
        world.Executor.CompleteLast();
        world.PumpUntilIdle();

        world.Runtime.Recording.CloseRecording(recording, observer);
        world.PumpUntilIdle();
        Assert.That(observer.ClosedReason!.Value.IsCompleted, Is.True);
        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.IntegrityFailure, Is.False, result.IntegrityDetail);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(result.Facts).Outcome.Kind,
            Is.EqualTo(RecordingOutcomeKind.Completed));
        return (result, observer);
    }

    [Test]
    public void ChangedStatesDeltaEncodeTheirBlobsAndReadBackVerified()
    {
        // Base (no document), before (count 1), after (count 2): the two later
        // blobs are near-identical to their predecessors, so both arrive as
        // delta records; the reader reconstructs and digest-verifies each.
        var world = new World();
        var (result, _) = RecordMutatingSession(world);
        Assert.That(result.DeltaBlobCount, Is.EqualTo(2));
        var permit = (EffectPermit)result.Cuts[2];
        Assert.That(result.TryGetBlob(permit.BeforeView, out _), Is.True);
    }

    [Test]
    public void AZeroChainBoundWritesFullBlobsOnly()
    {
        var world = new World(new RecordingCoordinatorOptions(
            Profile(), allowNonDurableStore: true, maxDeltaChainLength: 0));
        var (result, _) = RecordMutatingSession(world);
        Assert.That(result.DeltaBlobCount, Is.Zero);
    }

    [Test]
    public void TheChainBoundForcesAPeriodicFullCheckpoint()
    {
        // With a chain bound of one, the before deltas against the base (depth
        // 1) and the after must then be a full checkpoint — a chain never
        // outruns min(declared, store) (recording-replay.md §4).
        var world = new World(new RecordingCoordinatorOptions(
            Profile(), allowNonDurableStore: true, maxDeltaChainLength: 1));
        var (result, _) = RecordMutatingSession(world);
        Assert.That(result.DeltaBlobCount, Is.EqualTo(1));
    }

    [Test]
    public void AnUnchangedWatermarkReusesTheBeforeCheckpoint()
    {
        // No mutations at all: every permit's before-state is the recorded
        // base, both E3 cuts record the reuse, and no delta records appear —
        // the invalidation token answers without re-materializing
        // (guarantees.md §5.3).
        var world = new World();
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();

        foreach (var request in new[] { "r-1", "r-2" })
        {
            world.Submit(request);
            world.PumpUntilIdle();
            world.Executor.CompleteLast();
            world.PumpUntilIdle();
        }

        world.Runtime.Recording.CloseRecording(recording, observer);
        world.PumpUntilIdle();

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.IntegrityFailure, Is.False, result.IntegrityDetail);
        Assert.That(((EffectPermit)result.Cuts[2]).ReusedCheckpointBlob, Is.True);
        Assert.That(((EffectPermit)result.Cuts[5]).ReusedCheckpointBlob, Is.True);
        Assert.That(result.DeltaBlobCount, Is.Zero);
    }

    [Test]
    public void AFailedOpenLeavesNoDeltaOrTokenStateBehind()
    {
        // The failed open commits its base blob (priming the delta base and
        // the token) before the E1 cut faults; the next artifact must not
        // inherit either — a delta base the new file does not carry throws.
        var world = new World();
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.Committed); // header
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.Committed); // profile
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.Committed); // base blob
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.Fault);     // E1 cut
        var refused = new Observer();
        world.Runtime.Recording.OpenRecording(OpenRequest(), refused);
        world.PumpUntilIdle();
        Assert.That(refused.Answers, Is.EqualTo(new[] { "OpenRefused:OpenFailed" }));

        var (result, _) = RecordMutatingSession(world);
        Assert.That(result.DeltaBlobCount, Is.EqualTo(2));
    }

    [Test]
    public void APendingPermitDecisionNeverOutlivesItsWorld()
    {
        // The E3 cut answers InFlight and the permit parks with its decision
        // cached; a publication lands before the retry. Whatever order the
        // kernel serializes, the committed cut must speak for its own
        // watermark: an unmoved watermark reuses the base checkpoint, a moved
        // one carries a fresh materialization (guarantees.md §5.3) — a cached
        // decision from the old world must never commit against the new one.
        var control = new World();
        var controlObserver = new Observer();
        var controlRecording =
            control.Runtime.Recording.OpenRecording(OpenRequest(), controlObserver);
        control.PumpUntilIdle();
        control.Submit("r-1");
        control.PumpUntilIdle();
        control.Executor.CompleteLast();
        control.PumpUntilIdle();
        control.Runtime.Recording.CloseRecording(controlRecording, controlObserver);
        control.PumpUntilIdle();
        var controlResult = ArtifactReader.Read(
            control.Store.ReadAll(controlRecording.Value, Limits.MaxArtifactBytes), Limits);
        var quietWatermark = ((EffectPermit)controlResult.Cuts[2]).Watermark;

        var world = new World();
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.Committed); // E2 cut
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.InFlight);  // E3 cut parks
        world.Submit("r-1");
        world.PumpOnce();
        world.PublishCount(1, EventCausation.OfRequest(new RequestId("r-1")));
        world.PumpUntilIdle();
        world.Executor.CompleteLast();
        world.PumpUntilIdle();
        world.Runtime.Recording.CloseRecording(recording, observer);
        world.PumpUntilIdle();

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.IntegrityFailure, Is.False, result.IntegrityDetail);
        var opened = (RecordingOpened)result.Cuts[0];
        var permit = (EffectPermit)result.Cuts[2];
        if (permit.Watermark.Equals(quietWatermark))
        {
            Assert.That(permit.ReusedCheckpointBlob, Is.True);
            Assert.That(permit.BeforeView, Is.EqualTo(opened.BaseSnapshot));
        }
        else
        {
            Assert.That(permit.ReusedCheckpointBlob, Is.False,
                "a moved watermark voids the cached reuse decision");
            Assert.That(permit.BeforeView, Is.Not.EqualTo(opened.BaseSnapshot),
                "the committed permit must carry the fresh materialization");
            Assert.That(result.TryGetBlob(permit.BeforeView, out _), Is.True);
        }
    }

    private sealed class MovingSampledReader : ISampledSourceReader
    {
        internal long Level { get; set; }

        public SampledDocument? Read() => new SampledDocument(
            new SourceDocument(ValueArray<NamedField>.From(new[]
            {
                new NamedField("level", FieldValue.Of(Level)),
            })),
            producedAtLogicalTime: 100);
    }

    [Test]
    public void SampledExposureDisablesTheInvalidationToken()
    {
        // A sampled source can move without any revision advance, so an
        // unchanged watermark proves nothing (observation-state.md §7): each
        // permit must re-materialize. The second permit sees the moved gauge —
        // a fresh ContentId, never a reused checkpoint.
        var reader = new MovingSampledReader { Level = 1 };
        var world = new World(sampledReader: reader);
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();

        world.Submit("r-1");
        world.PumpUntilIdle();
        world.Executor.CompleteLast();
        world.PumpUntilIdle();

        reader.Level = 2;
        world.Submit("r-2");
        world.PumpUntilIdle();
        world.Executor.CompleteLast();
        world.PumpUntilIdle();

        world.Runtime.Recording.CloseRecording(recording, observer);
        world.PumpUntilIdle();

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.IntegrityFailure, Is.False, result.IntegrityDetail);
        var first = (EffectPermit)result.Cuts[2];
        var second = (EffectPermit)result.Cuts[5];
        Assert.That(second.BeforeView, Is.Not.EqualTo(first.BeforeView),
            "the moved gauge must be observed — a reused checkpoint here is unsound");
        Assert.That(second.ReusedCheckpointBlob, Is.False);
    }

    // ── TimelineTrack (1.1) ──────────────────────────────────────────────────

    [Test]
    public void UnsatisfiedWaitPollsLandOnTheTimelineLane()
    {
        var world = new World(new RecordingCoordinatorOptions(
            Profile(), allowNonDurableStore: true, timelineByteBudget: 4096));
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();

        var waitObserver = new WaitObserver();
        world.Runtime.Control.ArmWait(
            CountIsFive, Agent, timeoutAtLogicalTime: long.MaxValue, waitObserver);
        world.PumpUntilIdle();

        world.PublishCount(1, EventCausation.OfRequest(new RequestId("r-c")));
        world.PumpUntilIdle();
        world.PublishCount(2, EventCausation.OfRequest(new RequestId("r-c")));
        world.PumpUntilIdle();
        world.PublishCount(5, EventCausation.OfRequest(new RequestId("r-c")));
        world.PumpUntilIdle();
        Assert.That(waitObserver.Resolutions, Is.EqualTo(new[] { PredicateResolution.Satisfied }));

        world.Runtime.Recording.CloseRecording(recording, observer);
        world.PumpUntilIdle();

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.IntegrityFailure, Is.False, result.IntegrityDetail);
        Assert.That(result.Timeline.Count, Is.EqualTo(2), "two unsatisfied polls, then E6b");
        foreach (var entry in result.Timeline)
        {
            Assert.That(entry.Kind, Is.EqualTo(TimelineRecordKinds.WaitPoll));
            Assert.That(entry.Predicate, Is.EqualTo(CountIsFive));
        }

        Assert.That(
            EvidenceSemantics.ClassifyArtifact(result.Facts).Outcome.Kind,
            Is.EqualTo(RecordingOutcomeKind.Completed),
            "the timeline lane never bears on classification");
    }

    [Test]
    public void TheTimelineByteBudgetDropsAndMarksTheLoss()
    {
        // A budget too small for any poll record: both polls drop, and the
        // close marks the loss with one gap record (loss is permitted and
        // marked, recording-replay.md §3).
        var world = new World(new RecordingCoordinatorOptions(
            Profile(), allowNonDurableStore: true, timelineByteBudget: 4));
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();

        var waitObserver = new WaitObserver();
        world.Runtime.Control.ArmWait(
            CountIsFive, Agent, timeoutAtLogicalTime: long.MaxValue, waitObserver);
        world.PumpUntilIdle();
        world.PublishCount(1, EventCausation.OfRequest(new RequestId("r-c")));
        world.PumpUntilIdle();
        world.PublishCount(2, EventCausation.OfRequest(new RequestId("r-c")));
        world.PumpUntilIdle();

        world.Runtime.Recording.CloseRecording(recording, observer);
        world.PumpUntilIdle();

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.IntegrityFailure, Is.False, result.IntegrityDetail);
        Assert.That(result.Timeline.Count, Is.EqualTo(1));
        Assert.That(result.Timeline[0].Kind, Is.EqualTo(TimelineRecordKinds.Gap));
        Assert.That(result.Timeline[0].DroppedCount, Is.EqualTo(2));
    }

    [Test]
    public void ADisabledTimelineLaneWritesNothing()
    {
        var world = new World();
        var observer = new Observer();
        var recording = world.Runtime.Recording.OpenRecording(OpenRequest(), observer);
        world.PumpUntilIdle();

        var waitObserver = new WaitObserver();
        world.Runtime.Control.ArmWait(
            CountIsFive, Agent, timeoutAtLogicalTime: long.MaxValue, waitObserver);
        world.PumpUntilIdle();
        world.PublishCount(1, EventCausation.OfRequest(new RequestId("r-c")));
        world.PumpUntilIdle();

        world.Runtime.Recording.CloseRecording(recording, observer);
        world.PumpUntilIdle();

        var result = ArtifactReader.Read(
            world.Store.ReadAll(recording.Value, Limits.MaxArtifactBytes), Limits);
        Assert.That(result.Timeline.Count, Is.Zero, "the lane is off by default — no gap either");
    }
}
