using System;
using System.Collections.Generic;
using SignalRouter.AdapterSdk;
using SignalRouter.Codec.Recording;
using SignalRouter.Comparison;
using SignalRouter.Contracts;
using SignalRouter.Kernel;
using SignalRouter.Replay;

namespace SignalRouter.Tck
{
    /// <summary>Suite-wide knobs; every bound is a ceiling on driving, never a semantic expectation.</summary>
    public sealed class TckOptions
    {
        public TckOptions(int quiescenceFrameBound = 64)
        {
            if (quiescenceFrameBound < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(quiescenceFrameBound), "The quiescence bound is at least one frame.");
            }

            QuiescenceFrameBound = quiescenceFrameBound;
        }

        public int QuiescenceFrameBound { get; }
    }

    /// <summary>A check body signals a conformance failure with a stable detail message.</summary>
    internal sealed class TckCheckException : Exception
    {
        internal TckCheckException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// TCK 1.0 Core Profile (adapter-conformance.md §7.2): black-box checks driven
    /// through the SDK and runtime surfaces. The formerly staged obligations —
    /// replay-environment isolation and the fixture/reset contract — are live
    /// with the recording and replay module, so a conformant adapter reaches
    /// <see cref="TckAggregate.Passed"/>.
    /// </summary>
    public static class TckSuite
    {
        public const string Version = "tck-core-1.0";

        public static TckReport Run(ITckHarnessFactory factory, TckOptions? options = null)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            var suiteOptions = options ?? new TckOptions();
            var checks = new List<TckCheckResult>
            {
                RunCheck(factory, "registration-runtime-receipts", "registration-identity",
                    harness => CheckRegistrationReceipts(harness)),
                RunCheck(factory, "effect-exactly-once-completion", "effect-protocol",
                    harness => CheckExactlyOnceCompletion(harness)),
                RunCheck(factory, "completion-within-declared-frames", "completion-profile",
                    harness => CheckCompletionLatency(harness)),
                RunCheck(factory, "cooperative-cancellation", "effect-protocol",
                    harness => CheckCooperativeCancellation(harness)),
                RunCheck(factory, "managed-input-classification", "input-classification",
                    harness => CheckManagedClassification(harness)),
                RunCheck(factory, "observed-input-classification", "input-classification",
                    harness => CheckObservedClassification(harness)),
                RunCheck(factory, "gating-blocks-foreign-human-intent", "gating",
                    harness => CheckGating(harness)),
                RunCheck(factory, "pump-budget-enforced", "pump-contract",
                    harness => CheckPumpBudget(harness)),
                RunCheck(factory, "sync-adoption-logical-bound", "sync-bound",
                    harness => CheckSyncAdoption(harness)),
                RunCheck(factory, "contamination-trace-on-external-mutation", "contamination",
                    harness => CheckContamination(harness, suiteOptions)),
                RunCheck(factory, "source-publication-atomicity", "source-publication",
                    harness => CheckSourcePublication(harness)),
                RunCheck(factory, "predicate-obligation-repeatable", "predicate-obligation",
                    harness => CheckPredicateObligations(harness)),
                RunCheck(factory, "replay-environment-isolation", "replay-isolation",
                    harness => CheckReplayEnvironmentIsolation(harness, suiteOptions)),
                RunCheck(factory, "fixture-reset-contract", "fixture-reset",
                    harness => CheckFixtureResetContract(harness, suiteOptions)),
            };

            return new TckReport(Version, ValueArray<TckCheckResult>.From(checks));
        }

        // ── Check bodies ─────────────────────────────────────────────────────

        private static void CheckRegistrationReceipts(ITckHarness harness)
        {
            var duplicate = new CollectingRegistrationObserver();
            harness.Runtime.Registry.Register(new NodeRegistration(
                harness.VisibleTargetKey, NodeRole.Container, parent: null,
                ValueArray<NodeAttribute>.Empty, ValueArray<CapabilityDeclaration>.Empty,
                ExposurePolicy.Hidden), duplicate);

            var fresh = new CollectingRegistrationObserver();
            harness.Runtime.Registry.Register(new NodeRegistration(
                new AuthorKey("tck-fresh-node"), NodeRole.Container, parent: null,
                ValueArray<NodeAttribute>.Empty, ValueArray<CapabilityDeclaration>.Empty,
                ExposurePolicy.Hidden), fresh);
            harness.DriveFrames(1);

            Require(duplicate.Receipt != null, "the duplicate registration must be answered with a receipt");
            Require(!duplicate.Receipt!.Succeeded,
                "re-registering an existing AuthorKey must fail in the receipt (semantic-model.md §3.2)");
            Require(fresh.Receipt != null && fresh.Receipt.Succeeded && fresh.Receipt.Node.HasValue,
                "a fresh AuthorKey must register and answer its NodeRef");
        }

        private static void CheckExactlyOnceCompletion(ITckHarness harness)
        {
            var observer = Submit(harness, harness.MutatingCapability, "tck-r1");
            harness.DriveFrames(DeclaredMaxFrames(harness, harness.MutatingCapability));

            Require(observer.Accepted.Count == 1 && observer.Rejected.Count == 0,
                "the submission must be accepted split-phase");
            var answer = harness.Runtime.Queries.Query(new RequestId("tck-r1"), harness.AgentPrincipal);
            Require(answer.Equals(QueryAnswer.Terminal(InteractionOutcome.Succeeded)),
                "the mutating capability must reach Terminal(Succeeded); observed " + answer);

            foreach (var kind in TraceKinds(harness))
            {
                Require(!kind.Contains("CompletionRejected") && !kind.Contains("FenceRejected"),
                    "the kernel rejected an effect-protocol message — the adapter violated " +
                    "exactly-once completion / at-most-one fence per adopted permit " +
                    "(adapter-conformance.md §3); trace: " + kind);
            }
        }

        private static void CheckCompletionLatency(ITckHarness harness)
        {
            Submit(harness, harness.MutatingCapability, "tck-fast");
            harness.DriveFrames(DeclaredMaxFrames(harness, harness.MutatingCapability));
            RequireTerminal(harness, "tck-fast", "the mutating capability's declared MaxFrames");

            Submit(harness, harness.SlowCapability, "tck-slow");
            harness.DriveFrames(DeclaredMaxFrames(harness, harness.SlowCapability));
            RequireTerminal(harness, "tck-slow", "the slow capability's declared MaxFrames");
        }

        private static void CheckCooperativeCancellation(ITckHarness harness)
        {
            Submit(harness, harness.SlowCapability, "tck-cancel");
            harness.DriveFrames(1);
            harness.Runtime.Control.RequestCancel(new RequestId("tck-cancel"));
            harness.DriveFrames(DeclaredMaxFrames(harness, harness.SlowCapability) + 2);

            var answer = harness.Runtime.Queries.Query(new RequestId("tck-cancel"), harness.AgentPrincipal);
            Require(answer.Equals(QueryAnswer.Terminal(InteractionOutcome.Cancelled)),
                "a cooperative cancel of the adopted slow effect must reach Terminal(Cancelled); observed " + answer);
        }

        private static void CheckManagedClassification(ITckHarness harness)
        {
            RequireDeclaredClass(harness, InputClass.Managed);
            var observer = new CollectingSubmissionObserver();
            harness.SimulateManagedInput(new RequestId("tck-managed"), observer, asHuman: false);
            harness.DriveFrames(DeclaredMaxFrames(harness, harness.MutatingCapability) + 1);

            Require(observer.Accepted.Count == 1 && observer.Rejected.Count == 0,
                "a Managed input must be captured and normalized into an accepted submission " +
                "(adapter-conformance.md §6)");
            Require(HasTrace(harness, "Admitted"),
                "the normalized Managed input must be admitted through the mailbox");
        }

        private static void CheckObservedClassification(ITckHarness harness)
        {
            RequireDeclaredClass(harness, InputClass.Observed);
            var admittedBefore = CountTrace(harness, "Admitted");
            harness.SimulateExternalMutation();
            harness.DriveFrames(2);

            Require(CountTrace(harness, "Admitted") == admittedBefore,
                "an Observed input must never be normalized into a submission (adapter-conformance.md §6.2)");
            Require(HasTrace(harness, "ObservedExternal") || HasTrace(harness, "ContaminationObserved"),
                "the uncapturable mutation must surface as an ObservedExternal report");
        }

        private static void CheckGating(ITckHarness harness)
        {
            harness.Runtime.Control.AcquireExclusiveControl(harness.AgentDomain);
            harness.DriveFrames(1);

            var blocked = new CollectingSubmissionObserver();
            harness.SimulateManagedInput(new RequestId("tck-human-1"), blocked, asHuman: true);
            harness.DriveFrames(1);
            Require(blocked.Rejected.Count == 1 && blocked.Accepted.Count == 0,
                "under exclusive control, foreign human intent must be rejected at admission " +
                "(kernel-execution.md §7)");
            Require(HasTrace(harness, "HumanIntentBlocked"),
                "the blocked human intent must be traced as HumanIntentBlocked");

            harness.Runtime.Control.ReleaseExclusiveControl();
            harness.DriveFrames(1);
            var allowed = new CollectingSubmissionObserver();
            harness.SimulateManagedInput(new RequestId("tck-human-2"), allowed, asHuman: true);
            harness.DriveFrames(DeclaredMaxFrames(harness, harness.MutatingCapability) + 1);
            Require(allowed.Accepted.Count == 1,
                "after release, the same human intent must be admitted");
        }

        private static void CheckPumpBudget(ITckHarness harness)
        {
            for (var i = 1; i <= 3; i++)
            {
                Require(harness.PublishCount(i) == PublicationAnswer.Accepted,
                    "queueing publications for the budget probe must be accepted");
            }

            var report = harness.Runtime.Pump(new PumpBudget(
                maxTurns: 1, deadline: long.MaxValue, harness.LogicalNow,
                harness.Descriptor.FramePhases[0]));
            Require(report.TurnsExecuted <= 1,
                "the pump must never exceed MaxTurns (kernel-execution.md §6); executed " + report.TurnsExecuted);
            Require(report.SourcePublicationQueueDepth == 0 || report.WorkRemaining,
                "the report must tell the truth: queued publications imply WorkRemaining");
        }

        private static void CheckSyncAdoption(ITckHarness harness)
        {
            Submit(harness, harness.MutatingCapability, "tck-sync");
            harness.DriveFrames(DeclaredMaxFrames(harness, harness.MutatingCapability));
            // The logical form of the sync bound: Execute returned an adoption within
            // the pump that dispatched it. The permit trace alone cannot prove that —
            // the kernel emits it before calling the executor — so the check also
            // demands the Succeeded terminal only a returned adoption can produce.
            // The wall-clock value (SyncExecutionBoundMilliseconds) is measured at tier 3.
            Require(HasTrace(harness, "EffectPermitted"),
                "the submission must reach effect dispatch (adapter-conformance.md §3)");
            var answer = harness.Runtime.Queries.Query(new RequestId("tck-sync"), harness.AgentPrincipal);
            Require(answer.Equals(QueryAnswer.Terminal(InteractionOutcome.Succeeded)),
                "Execute must return Adopted synchronously and the effect must succeed — " +
                "a throwing or deferring executor cannot produce this terminal; observed " + answer);
        }

        private static void CheckContamination(ITckHarness harness, TckOptions options)
        {
            var recording = OpenRecording(harness, options);
            Submit(harness, harness.SlowCapability, "tck-window");
            harness.DriveFrames(1);
            harness.SimulateExternalMutation();
            harness.DriveFrames(1);

            Require(HasTrace(harness, "ContaminationObserved"),
                "an Observed mutation landing inside the effect window must contaminate " +
                "(observation-state.md §7.2)");
            DriveUntilIdle(harness, options);

            // The evidence promotion (guarantees.md §5.5): the mutation is a
            // durable E5 barrier naming the contaminated interaction, not just
            // a trace line.
            CloseRecording(harness, recording, options);
            var reading = Codec.Recording.ArtifactReader.Read(
                harness.ReadArtifact(recording), ReadLimits());
            var contaminated = false;
            for (var index = 0; index < reading.Cuts.Count; index++)
            {
                if (reading.Cuts[index] is ExternalMutationBarrier barrier)
                {
                    for (var request = 0; request < barrier.ContaminatedRequests.Count; request++)
                    {
                        contaminated |= barrier.ContaminatedRequests[request]
                            .Equals(new RequestId("tck-window"));
                    }
                }
            }

            Require(contaminated,
                "the recording must carry an E5 barrier marking the mid-effect " +
                "interaction contaminated (guarantees.md §5.5)");
        }

        private static void CheckSourcePublication(ITckHarness harness)
        {
            var first = new CollectingWaitObserver();
            harness.Runtime.Control.ArmWait(
                harness.CountAtLeastOne, harness.AgentPrincipal, long.MaxValue, first);
            harness.DriveFrames(1);
            Require(first.Resolutions.Count == 0,
                "nothing is published yet — the count>=1 wait must stay armed");

            Require(harness.PublishCount(1) == PublicationAnswer.Accepted,
                "a contract-conforming publication must be accepted");
            harness.DriveFrames(1);
            Require(first.Resolutions.Count == 1 &&
                first.Resolutions[0].Resolution == PredicateResolution.Satisfied,
                "adopting the publication must advance the revision and resolve the wait " +
                "(observation-state.md §7.1)");

            harness.PublishUndeclaredField();
            harness.DriveFrames(1);
            var second = new CollectingWaitObserver();
            harness.Runtime.Control.ArmWait(
                harness.CountAtLeastTwo, harness.AgentPrincipal, long.MaxValue, second);
            harness.DriveFrames(1);
            Require(second.Resolutions.Count == 0,
                "the contract-violating publication must not have swapped any part of the document");

            Require(harness.PublishCount(2) == PublicationAnswer.Accepted,
                "the source must keep accepting valid publications after a violation");
            harness.DriveFrames(1);
            Require(second.Resolutions.Count == 1 &&
                second.Resolutions[0].Resolution == PredicateResolution.Satisfied,
                "the next valid publication must adopt atomically and resolve count>=2");
        }

        private static void CheckPredicateObligations(ITckHarness harness)
        {
            Require(harness.PublishCount(1) == PublicationAnswer.Accepted,
                "publishing the probe document must be accepted");
            harness.DriveFrames(1);

            var first = EvaluateBatch(harness);
            var second = EvaluateBatch(harness);
            Require(first[0].Equals(PredicateEvaluationOutcome.Satisfied),
                "count>=1 must evaluate Satisfied against the published document; observed " + first[0]);
            Require(first[1].Equals(PredicateEvaluationOutcome.False),
                "count>=2 must evaluate False (a decided answer, not Unevaluable); observed " + first[1]);
            Require(first[0].Equals(second[0]) && first[1].Equals(second[1]),
                "re-evaluating the same batch against unchanged state must answer identically");
        }

        private static void CheckReplayEnvironmentIsolation(ITckHarness harness, TckOptions options)
        {
            // Record one interaction on the live harness world.
            var recording = OpenRecording(harness, options);
            Submit(harness, harness.MutatingCapability, "tck-replayed");
            harness.DriveFrames(DeclaredMaxFrames(harness, harness.MutatingCapability) + 1);
            RequireTerminal(harness, "tck-replayed", "the recorded interaction");
            CloseRecording(harness, recording, options);

            var artifact = harness.ReadArtifact(recording);
            var allowlist = BuildAllowlist(harness, artifact);
            var scan = ReplayPreScan.Scan(
                artifact, ReadLimits(), allowlist, new ComparisonVocabulary(),
                secretResolver: null, new ReplayTrustOptions(ArtifactProvenance.Trusted));
            Require(scan.Plan != null,
                "the recorded artifact must pass the trust boundary; refused: " +
                scan.Refusal?.Code);
            Require(scan.Plan!.Stop == null,
                "a clean recording must plan no strict-replay stop");

            // Replay into the adapter's twin; the live world must stay untouched.
            var traceBefore = TraceKinds(harness);
            var report = new ReplayDriver(
                new Codec.CanonicalState.CanonicalStateCodec(), new ComparisonVocabulary())
                .Execute(
                    scan.Plan, allowlist, harness.ReplayEnvironments,
                    secretResolver: null, harness.RedactionKey, ReplayMode.StrictSemantic);
            Require(report.Outcome.Equals(ReplayComparisonOutcome.Equal),
                "the twin must replay the recording all-Equal (recording-replay.md §6); " +
                "answered " + report.Outcome + " detail=" + report.DetailCode);

            // Content and order, not a count: a same-length substitution is
            // still shared state.
            var traceAfter = TraceKinds(harness);
            var untouched = traceAfter.Count == traceBefore.Count;
            for (var index = 0; untouched && index < traceAfter.Count; index++)
            {
                untouched = string.Equals(traceAfter[index], traceBefore[index], StringComparison.Ordinal);
            }

            Require(untouched,
                "replay isolation: the twin shares no state with the live runtime — " +
                "the live trace must not move (recording-replay.md §6)");
        }

        private static void CheckFixtureResetContract(ITckHarness harness, TckOptions options)
        {
            // An empty recording fixes the base the fixture must reproduce.
            var recording = OpenRecording(harness, options);
            CloseRecording(harness, recording, options);
            var reading = ArtifactReader.Read(harness.ReadArtifact(recording), ReadLimits());
            RecordingOpened? opened = null;
            for (var index = 0; index < reading.Cuts.Count; index++)
            {
                opened ??= reading.Cuts[index] as RecordingOpened;
            }

            Require(opened != null, "the closed recording must carry its E1");

            // Two independent environments: the fixture contract reproduces the
            // recorded base exactly, creation after creation (verification.md
            // §5.3). The base is the world the adapter's declared fixture
            // produces — this check pins the fixture's determinism against the
            // recorded E1, not the factory's ability to reproduce an arbitrary
            // mutated state (a recording taken over a diverged base simply
            // fails its base comparison at replay). The first twin is genuinely
            // dirtied before disposal, so a factory that hands back a live
            // world instead of resetting cannot pass the second round.
            using (var first = harness.ReplayEnvironments.Create(
                opened!, NoOpEvidenceCoordinator.Instance))
            {
                RequireRecordedBase(harness, first, opened!, "the first twin");
                DirtyTwin(harness, first, opened!);
            }

            using (var second = harness.ReplayEnvironments.Create(
                opened!, NoOpEvidenceCoordinator.Instance))
            {
                RequireRecordedBase(
                    harness, second, opened!, "a fresh twin created after a dirtied one");
            }
        }

        private static void RequireRecordedBase(
            ITckHarness harness, IReplayEnvironment environment, RecordingOpened opened,
            string whichTwin)
        {
            Require(environment.Runtime.RecordObservation.TryMaterializeView(
                    harness.RecordingProfile.RecordView, harness.RecordingProfile.Scope,
                    null, out var baseView, out _),
                "the twin's record view must materialize");
            Require(baseView!.Snapshot.ContentId.Equals(opened.BaseSnapshot),
                "the fixture must reproduce the recorded base ContentId, reset after " +
                "reset (verification.md §5.3); " + whichTwin);
        }

        private static void DirtyTwin(
            ITckHarness harness, IReplayEnvironment environment, RecordingOpened opened)
        {
            SubmitTo(environment.Runtime, harness, harness.MutatingCapability, "tck-fixture-dirty");
            for (var i = 0; i < DeclaredMaxFrames(harness, harness.MutatingCapability) + 2; i++)
            {
                environment.Advance();
            }

            Require(environment.Runtime.Queries
                    .Query(new RequestId("tck-fixture-dirty"), harness.AgentPrincipal)
                    .Equals(QueryAnswer.Terminal(InteractionOutcome.Succeeded)),
                "the mutating capability must complete inside the twin");
            Require(environment.Runtime.RecordObservation.TryMaterializeView(
                    harness.RecordingProfile.RecordView, harness.RecordingProfile.Scope,
                    null, out var dirtied, out _),
                "the dirtied twin's record view must materialize");
            Require(!dirtied!.Snapshot.ContentId.Equals(opened.BaseSnapshot),
                "the mutating capability must move the twin off the recorded base — " +
                "otherwise reset cannot be distinguished from reuse; the recording " +
                "profile's view must cover the declared mutating capability's effect");
        }

        private static OperationId OpenRecording(ITckHarness harness, TckOptions options)
        {
            var observer = new CollectingRecordingObserver();
            var profile = harness.RecordingProfile;
            var recording = harness.Runtime.Recording.OpenRecording(
                new RecordingOpenRequest(
                    profile.Reference, profile.RecordView, profile.Scope, profile.RedactionPolicy),
                observer);
            DriveUntilIdle(harness, options);
            Require(observer.Opened,
                "the harness runtime must be recording-capable (ITckHarness contract); " +
                "refused: " + observer.RefusalCode);
            return recording;
        }

        private static void CloseRecording(
            ITckHarness harness, OperationId recording, TckOptions options)
        {
            var observer = new CollectingRecordingObserver();
            harness.Runtime.Recording.CloseRecording(recording, observer);
            DriveUntilIdle(harness, options);
            Require(observer.Closed, "the recording must close; failed: " + observer.RefusalCode);
        }

        private static ReplayAllowlist BuildAllowlist(ITckHarness harness, byte[] artifact)
        {
            var reading = ArtifactReader.Read(artifact, ReadLimits());
            RecordingOpened? opened = null;
            for (var index = 0; index < reading.Cuts.Count; index++)
            {
                opened ??= reading.Cuts[index] as RecordingOpened;
            }

            Require(opened != null, "the artifact must carry its E1");
            var predicates = new PredicateAllowlistEntry[opened!.PredicateContracts.Count];
            for (var index = 0; index < opened.PredicateContracts.Count; index++)
            {
                predicates[index] = new PredicateAllowlistEntry(
                    opened.PredicateContracts[index],
                    harness.DefinitionOf(opened.PredicateContracts[index]));
            }

            return new ReplayAllowlist(
                opened.CompletionBindings,
                opened.StateSourceContracts,
                ValueArray<PredicateAllowlistEntry>.From(predicates),
                harness.RecordingProfile);
        }

        private static ArtifactReadLimits ReadLimits() => new ArtifactReadLimits(
            maxArtifactBytes: 8L * 1024 * 1024,
            maxRecordCount: 4096,
            maxRecordBytes: 1024 * 1024,
            maxBlobBytes: 1024 * 1024,
            maxStringLength: 64 * 1024);

        private sealed class CollectingRecordingObserver : IRecordingObserver
        {
            internal bool Opened { get; private set; }

            internal bool Closed { get; private set; }

            internal string? RefusalCode { get; private set; }

            public void OnOpened(OperationId recording) => Opened = true;

            public void OnOpenRefused(OperationId recording, string reasonCode) =>
                RefusalCode = reasonCode;

            public void OnClosed(OperationId recording, RecordingCloseReason reason) =>
                Closed = reason.IsCompleted;

            public void OnFailed(OperationId recording, string reasonCode) =>
                RefusalCode = reasonCode;
        }

        // ── Drivers and helpers ──────────────────────────────────────────────

        private static TckCheckResult RunCheck(
            ITckHarnessFactory factory, string checkId, string obligation, Action<ITckHarness> body)
        {
            ITckHarness harness;
            try
            {
                harness = factory.Create();
            }
            catch (Exception exception)
            {
                return new TckCheckResult(
                    checkId, obligation, required: true, TckCheckStatus.Failed,
                    "harness creation: " + Describe(exception));
            }

            TckCheckStatus status;
            string? detail;
            try
            {
                body(harness);
                status = TckCheckStatus.Passed;
                detail = null;
            }
            catch (TckCheckException failure)
            {
                status = TckCheckStatus.Failed;
                detail = failure.Message;
            }
            catch (Exception exception)
            {
                // An adapter or kernel exception is itself a conformance failure of
                // the world under test — record it, never abort the suite.
                status = TckCheckStatus.Failed;
                detail = Describe(exception);
            }

            try
            {
                harness.TearDown();
            }
            catch (Exception exception)
            {
                if (status == TckCheckStatus.Passed)
                {
                    status = TckCheckStatus.Failed;
                    detail = "teardown: " + Describe(exception);
                }
            }

            return new TckCheckResult(checkId, obligation, required: true, status, detail);
        }

        private static void Require(bool condition, string detail)
        {
            if (!condition)
            {
                throw new TckCheckException(detail);
            }
        }

        private static string Describe(Exception exception) =>
            exception.GetType().Name + ": " + exception.Message;

        private static CollectingSubmissionObserver Submit(
            ITckHarness harness, CapabilityContractRef capability, string requestId)
        {
            return SubmitTo(harness.Runtime, harness, capability, requestId);
        }

        private static CollectingSubmissionObserver SubmitTo(
            KernelRuntime runtime, ITckHarness harness, CapabilityContractRef capability,
            string requestId)
        {
            var observer = new CollectingSubmissionObserver();
            runtime.Ingress.Submit(new IntentSubmission(
                new RequestId(requestId),
                capability,
                TargetReference.ForKey(harness.VisibleTargetKey),
                InvocationPayload.Empty,
                new IdentityEnvelope(
                    harness.AgentPrincipal, IngressPath.InProcessApi, Provenance.Automation, Causality.Root()),
                observer));
            return observer;
        }

        private static int DeclaredMaxFrames(ITckHarness harness, CapabilityContractRef capability)
        {
            CapabilityProfileSupport? row = null;
            foreach (var support in harness.Descriptor.Capabilities)
            {
                if (support.Capability.Equals(capability))
                {
                    row = support;
                    break;
                }
            }

            Require(row != null,
                "the descriptor must declare profile support for capability " + capability);
            var maxFrames = 0;
            foreach (var profile in row!.Profiles)
            {
                foreach (var latency in harness.Descriptor.CompletionLatencies)
                {
                    if (latency.Profile.Equals(profile) && latency.MaxFrames > maxFrames)
                    {
                        maxFrames = latency.MaxFrames;
                    }
                }
            }

            Require(maxFrames > 0,
                "every supported profile must carry a declared MaxFrames (adapter-conformance.md §4)");
            return maxFrames;
        }

        private static void RequireTerminal(ITckHarness harness, string requestId, string boundDescription)
        {
            var answer = harness.Runtime.Queries.Query(new RequestId(requestId), harness.AgentPrincipal);
            Require(answer.Equals(QueryAnswer.Terminal(InteractionOutcome.Succeeded)),
                "a Succeeded terminal with the bound profile's evidence must arrive within " +
                boundDescription + " (adapter-conformance.md §4); observed " + answer);
        }

        private static void RequireDeclaredClass(ITckHarness harness, InputClass classification)
        {
            foreach (var row in harness.Descriptor.InputClassifications)
            {
                if (row.Classification == classification)
                {
                    return;
                }
            }

            throw new TckCheckException(
                "the descriptor must declare at least one " + classification + " input class");
        }

        private static void DriveUntilIdle(ITckHarness harness, TckOptions options)
        {
            for (var i = 0; i < options.QuiescenceFrameBound; i++)
            {
                harness.DriveFrames(1);
                var report = harness.Runtime.Pump(new PumpBudget(
                    maxTurns: 1, deadline: long.MaxValue, harness.LogicalNow,
                    harness.Descriptor.FramePhases[0]));
                if (!report.WorkRemaining && !report.AwaitingAdapterCompletion)
                {
                    return;
                }
            }

            throw new TckCheckException(
                "the world did not become quiescent within " + options.QuiescenceFrameBound + " frames");
        }

        private static ValueArray<PredicateEvaluationOutcome> EvaluateBatch(ITckHarness harness)
        {
            var observer = new CollectingAssertionObserver();
            harness.Runtime.Control.EvaluateAssertions(new AssertionBatch(
                ValueArray<PredicateContractRef>.From(new[]
                {
                    harness.CountAtLeastOne,
                    harness.CountAtLeastTwo,
                }),
                harness.AgentPrincipal,
                observer));
            harness.DriveFrames(1);
            Require(observer.Results.HasValue && observer.Results.Value.Count == 2,
                "the assertion batch must answer every predicate in order");
            var outcomes = new List<PredicateEvaluationOutcome>(2);
            foreach (var result in observer.Results!.Value)
            {
                outcomes.Add(result.Outcome);
            }

            return ValueArray<PredicateEvaluationOutcome>.From(outcomes);
        }

        private static List<string> TraceKinds(ITckHarness harness)
        {
            var kinds = new List<string>();
            foreach (var semanticEvent in harness.Runtime.Trace.Snapshot())
            {
                kinds.Add(semanticEvent.Kind.Value +
                    (semanticEvent.DetailCode == null ? "" : ":" + semanticEvent.DetailCode));
            }

            return kinds;
        }

        private static bool HasTrace(ITckHarness harness, string prefixOrDetail)
        {
            foreach (var kind in TraceKinds(harness))
            {
                if (kind.StartsWith(prefixOrDetail, StringComparison.Ordinal) ||
                    kind.Contains(prefixOrDetail))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountTrace(ITckHarness harness, string prefix)
        {
            var count = 0;
            foreach (var kind in TraceKinds(harness))
            {
                if (kind.StartsWith(prefix, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        // ── Observers ────────────────────────────────────────────────────────

        private sealed class CollectingSubmissionObserver : ISubmissionObserver
        {
            internal List<RequestId> Accepted { get; } = new List<RequestId>();

            internal List<RejectionReason> Rejected { get; } = new List<RejectionReason>();

            public void OnAccepted(RequestId request) => Accepted.Add(request);

            public void OnRejected(RequestId request, RejectionReason reason) => Rejected.Add(reason);
        }

        private sealed class CollectingRegistrationObserver : IRegistrationObserver
        {
            internal RegistrationReceipt? Receipt { get; private set; }

            public void OnCompleted(RegistrationReceipt receipt) => Receipt = receipt;
        }

        private sealed class CollectingWaitObserver : IWaitObserver
        {
            internal List<(OperationId Operation, PredicateResolution Resolution)> Resolutions { get; } =
                new List<(OperationId, PredicateResolution)>();

            public void OnResolved(OperationId operation, PredicateResolution resolution) =>
                Resolutions.Add((operation, resolution));
        }

        private sealed class CollectingAssertionObserver : IAssertionObserver
        {
            internal ValueArray<PredicateEvaluationResult>? Results { get; private set; }

            public void OnEvaluated(ValueArray<PredicateEvaluationResult> results) => Results = results;
        }
    }
}
