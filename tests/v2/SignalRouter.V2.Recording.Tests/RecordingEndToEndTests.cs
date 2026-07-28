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

        internal World(RecordingCoordinatorOptions? options = null)
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
            Runtime.Start(Executor);
        }

        internal void Submit(string request) => Runtime.Ingress.Submit(new IntentSubmission(
            new RequestId(request),
            Invoke,
            TargetReference.ForKey(new AuthorKey("save")),
            InvocationPayload.Empty,
            new IdentityEnvelope(Agent, IngressPath.Mcp, Provenance.Automation, Causality.Root()),
            observer: null));

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

        // The E4 appends are the next two records (after blob): script the cut
        // append to fault. Blob first, then cut — fault the second append.
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.Committed); // after-view blob
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.Fault);     // E4 cut
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
}
