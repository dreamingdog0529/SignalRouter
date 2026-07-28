using System;
using System.Collections.Generic;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// The recording lifecycle state machine (ADR 0015): open/close fences are a
/// dedicated admission freeze that drains every fence member before E1/E7,
/// Pending commits retry across pumps, observers are answered exactly once,
/// close requests flow only through the polled channel, the unkeyed-target
/// policy refuses before E2, and teardown notifies the coordinator while
/// observation services are still addressable.
/// </summary>
public sealed class RecordingStateMachineTests
{
    internal sealed class ScriptedRecordingCoordinator : IRecordingCoordinator
    {
        internal IRecordObservationServices? Bound { get; private set; }

        internal EvidenceReadiness OpenAnswer { get; set; } = EvidenceReadiness.Ready;

        internal EvidenceReadiness CloseAnswer { get; set; } = EvidenceReadiness.Ready;

        internal List<OpenEvidence> Opens { get; } = new();

        internal List<CloseEvidence> Closes { get; } = new();

        internal List<TerminalEvidence> Terminals { get; } = new();

        internal bool TornDown { get; private set; }

        internal bool AddressableAtTeardown { get; private set; }

        public IncompleteReason? CloseRequested { get; set; }

        public RecordingAdmissionPolicy AdmissionPolicy =>
            RecordingAdmissionPolicy.RefuseUnkeyedTargets;

        public void Bind(IRecordObservationServices services)
        {
            if (Bound != null)
            {
                throw new KernelFaultException("Bind is valid exactly once.");
            }

            Bound = services;
        }

        public EvidenceReadiness PrepareOpenEvidence(OpenEvidence evidence)
        {
            if (OpenAnswer == EvidenceReadiness.Ready)
            {
                Opens.Add(evidence);
            }

            return OpenAnswer;
        }

        public EvidenceReadiness CommitCloseEvidence(CloseEvidence evidence)
        {
            if (CloseAnswer == EvidenceReadiness.Ready)
            {
                Closes.Add(evidence);
            }

            return CloseAnswer;
        }

        public void NotifyTeardown()
        {
            TornDown = true;
            AddressableAtTeardown = Bound!.CanAddress;
        }

        public EvidenceReadiness PrepareAdmissionEvidence(AdmissionEvidence evidence) =>
            EvidenceReadiness.Ready;

        public EvidenceReadiness PrepareEffectPermit(PermitEvidence evidence) =>
            EvidenceReadiness.Ready;

        public EvidenceReadiness CommitTerminalEvidence(TerminalEvidence evidence)
        {
            Terminals.Add(evidence);
            return EvidenceReadiness.Ready;
        }
    }

    private sealed class RecordingObserver : IRecordingObserver
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

    private static RecordingOpenRequest Request() => new(
        new ReplayComparisonProfileRef(
            new ReplayComparisonProfileId("strict"), new ContractVersion(1, 0)),
        new ViewContractRef(new ViewContractId("record-standard"), new ContractVersion(1, 0)),
        "root",
        new RedactionPolicyId("default-redaction"));

    private static (KernelFixture Fixture, ScriptedRecordingCoordinator Coordinator) Build()
    {
        var coordinator = new ScriptedRecordingCoordinator();
        var fixture = new KernelFixture(
            coordinator: coordinator, codec: new TestCanonicalStateCodec(), start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            new ViewContractRef(new ViewContractId("record-standard"), new ContractVersion(1, 0)),
            ViewFamily.Record, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Start(fixture.Executor);
        return (fixture, coordinator);
    }

    [Test]
    public void BindHappensOnceAtStartBeforeAnyCallback()
    {
        var (_, coordinator) = Build();
        Assert.That(coordinator.Bound, Is.Not.Null);
    }

    [Test]
    public void OpeningDrainsInFlightWorkBeforeE1()
    {
        var (fixture, coordinator) = Build();
        var observer = new RecordingObserver();

        // r-1 is mid-effect when the open arrives; r-2 waits in the mailbox
        // behind the fence.
        fixture.Submit("r-1");
        fixture.PumpUntilIdle();
        fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.Submit("r-2");
        fixture.Pump();

        Assert.That(observer.Answers, Is.Empty, "the fence is draining, not done");
        Assert.That(coordinator.Opens, Is.Empty);

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();

        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened" }));
        Assert.That(coordinator.Opens, Has.Count.EqualTo(1));
        Assert.That(
            coordinator.Terminals[0].Request, Is.EqualTo(new RequestId("r-1")),
            "the pre-fence member drained to its terminal before E1");
        Assert.That(
            fixture.TraceKinds().FindAll(kind => kind == "Admitted"), Has.Count.EqualTo(2),
            "the held admission proceeded after the fence lifted");
        Assert.That(coordinator.Opens[0].BaseSnapshot.Snapshot.IsAddressed, Is.True);
        Assert.That(coordinator.Opens[0].Catalog.CompletionBindings.Count, Is.GreaterThan(0));
    }

    [Test]
    public void PendingOpenEvidenceRetriesAcrossPumpsAndAnswersOnce()
    {
        var (fixture, coordinator) = Build();
        var observer = new RecordingObserver();
        coordinator.OpenAnswer = EvidenceReadiness.Pending;
        fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.Pump();
        fixture.Pump();
        Assert.That(observer.Answers, Is.Empty);

        coordinator.OpenAnswer = EvidenceReadiness.Ready;
        fixture.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened" }));
    }

    [Test]
    public void AnOrderlyCloseDrainsResolvesWaitsAndCommitsE7()
    {
        var (fixture, coordinator) = Build();
        var observer = new RecordingObserver();
        var recording = fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();

        var waitObserver = new RecordingWaitObserver();
        fixture.Runtime.Control.ArmWait(
            KernelFixture.LabelExists, KernelFixture.Agent,
            timeoutAtLogicalTime: long.MaxValue, waitObserver);
        fixture.Pump();

        fixture.Runtime.Recording.CloseRecording(recording, observer);
        fixture.PumpUntilIdle();

        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(observer.ClosedReason!.Value.IsCompleted, Is.True);
        Assert.That(coordinator.Closes, Has.Count.EqualTo(1));
        Assert.That(
            waitObserver.Resolutions[0].Resolution, Is.EqualTo(PredicateResolution.Cancelled),
            "armed waits resolve as Cancelled before E7 (guarantees.md §5.9)");
    }

    [Test]
    public void ACloseRequestFromTheCoordinatorDrivesTheCloseFence()
    {
        var (fixture, coordinator) = Build();
        var observer = new RecordingObserver();
        fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();

        coordinator.CloseRequested = new IncompleteReason("SizeLimit");
        fixture.PumpUntilIdle();

        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Closed" }));
        Assert.That(observer.ClosedReason!.Value.IsCompleted, Is.False);
        Assert.That(observer.ClosedReason.Value.Reason.Value, Is.EqualTo("SizeLimit"));
    }

    [Test]
    public void AnUnkeyedTargetIsRefusedWhileRecording()
    {
        var coordinator = new ScriptedRecordingCoordinator();
        var fixture = new KernelFixture(
            coordinator: coordinator, codec: new TestCanonicalStateCodec(), start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            new ViewContractRef(new ViewContractId("record-standard"), new ContractVersion(1, 0)),
            ViewFamily.Record, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        var keyless = fixture.Runtime.Bootstrap.RegisterNode(new NodeRegistration(
            authorKey: null,
            NodeRole.Button,
            parent: null,
            ValueArray<NodeAttribute>.Empty,
            ValueArray<CapabilityDeclaration>.From(new[]
            {
                new CapabilityDeclaration(KernelFixture.Invoke, initiallyAvailable: true),
            }),
            new ExposurePolicy(ValueArray<SecurityDomainId>.From(new[]
            {
                KernelFixture.AgentDomain, KernelFixture.RecordDomain,
            }))));
        fixture.Runtime.Start(fixture.Executor);

        var observer = new RecordingObserver();
        fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened" }));

        var submission = new RecordingObserverlessSubmit(fixture, keyless);
        Assert.That(submission.Rejected[0].Reason.Value, Is.EqualTo("UnkeyedTarget"));
    }

    private sealed class RecordingObserverlessSubmit
    {
        internal List<(RequestId Request, RejectionReason Reason)> Rejected { get; }

        internal RecordingObserverlessSubmit(KernelFixture fixture, NodeRef keyless)
        {
            var observer = new RecordingObserver2();
            fixture.Runtime.Ingress.Submit(new IntentSubmission(
                new RequestId("r-keyless"),
                KernelFixture.Invoke,
                TargetReference.ForNode(keyless),
                InvocationPayload.Empty,
                new IdentityEnvelope(
                    KernelFixture.Agent, IngressPath.Mcp, Provenance.Automation, Causality.Root()),
                observer));
            fixture.PumpUntilIdle();
            Rejected = observer.Rejected;
        }

        private sealed class RecordingObserver2 : ISubmissionObserver
        {
            internal List<(RequestId Request, RejectionReason Reason)> Rejected { get; } = new();

            public void OnAccepted(RequestId request)
            {
            }

            public void OnRejected(RequestId request, RejectionReason reason) =>
                Rejected.Add((request, reason));
        }
    }

    [Test]
    public void OpenRefusalsAnswerHonestly()
    {
        // No recording coordinator at all.
        var plain = new KernelFixture(codec: new TestCanonicalStateCodec());
        var observer = new RecordingObserver();
        plain.Runtime.Recording.OpenRecording(Request(), observer);
        plain.PumpUntilIdle();
        Assert.That(observer.Answers, Is.EqualTo(new[] { "OpenRefused:NoRecordingCoordinator" }));

        // No codec.
        var codecless = new KernelFixture(coordinator: new ScriptedRecordingCoordinator());
        var observer2 = new RecordingObserver();
        codecless.Runtime.Recording.OpenRecording(Request(), observer2);
        codecless.PumpUntilIdle();
        Assert.That(observer2.Answers, Is.EqualTo(new[] { "OpenRefused:CodecUnavailable" }));

        // Unknown record view.
        var (fixture, _) = Build();
        var observer3 = new RecordingObserver();
        fixture.Runtime.Recording.OpenRecording(
            new RecordingOpenRequest(
                Request().Profile,
                new ViewContractRef(new ViewContractId("nope"), new ContractVersion(1, 0)),
                "root",
                Request().RedactionPolicy),
            observer3);
        fixture.PumpUntilIdle();
        Assert.That(observer3.Answers, Is.EqualTo(new[] { "OpenRefused:UnknownRecordView" }));

        // Already recording.
        var observer4 = new RecordingObserver();
        fixture.Runtime.Recording.OpenRecording(Request(), observer4);
        fixture.PumpUntilIdle();
        var observer5 = new RecordingObserver();
        fixture.Runtime.Recording.OpenRecording(Request(), observer5);
        fixture.PumpUntilIdle();
        Assert.That(observer4.Answers, Is.EqualTo(new[] { "Opened" }));
        Assert.That(observer5.Answers, Is.EqualTo(new[] { "OpenRefused:AlreadyRecording" }));
    }

    [Test]
    public void TeardownNotifiesTheCoordinatorWhileStillAddressable()
    {
        var (fixture, coordinator) = Build();
        var observer = new RecordingObserver();
        fixture.Runtime.Recording.OpenRecording(Request(), observer);
        fixture.PumpUntilIdle();

        fixture.Runtime.Control.TearDownIncarnation();
        fixture.PumpUntilIdle();

        Assert.That(coordinator.TornDown, Is.True);
        Assert.That(
            coordinator.AddressableAtTeardown, Is.True,
            "the coordinator's Incomplete(IncarnationChanged) attempt needs live services");
        Assert.That(observer.Answers, Is.EqualTo(new[] { "Opened", "Failed:IncarnationChanged" }));
    }
}
