using System;
using System.Collections.Generic;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;

namespace SignalRouter.V2.Tck
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
    /// TCK 0.x Core Profile (adapter-conformance.md §7.2): black-box checks driven
    /// through the SDK and runtime surfaces. Staged obligations — replay-environment
    /// isolation and the fixture/reset contract — are required skips until the
    /// recording and replay module lands, so the best possible aggregate today is
    /// <see cref="TckAggregate.Incomplete"/>: this profile never claims SDK
    /// conformance or tier-2 completion.
    /// </summary>
    public static class TckSuite
    {
        public const string Version = "tck-core-0.x";

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
                new TckCheckResult(
                    "replay-environment-isolation", "replay-isolation", required: true,
                    TckCheckStatus.Skipped,
                    "Staged: IReplayEnvironmentFactory is declared with the recording and replay module (docs/v2 item 5)."),
                new TckCheckResult(
                    "fixture-reset-contract", "fixture-reset", required: true,
                    TckCheckStatus.Skipped,
                    "Staged: the fixture and reset contract executes under the replay harness (docs/v2 item 5)."),
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
            Submit(harness, harness.SlowCapability, "tck-window");
            harness.DriveFrames(1);
            harness.SimulateExternalMutation();
            harness.DriveFrames(1);

            // Trace-level only in this profile: the E5 evidence cut is staged to the
            // recording and replay module.
            Require(HasTrace(harness, "ContaminationObserved"),
                "an Observed mutation landing inside the effect window must contaminate " +
                "(observation-state.md §7.2)");
            DriveUntilIdle(harness, options);
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
            var observer = new CollectingSubmissionObserver();
            harness.Runtime.Ingress.Submit(new IntentSubmission(
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
