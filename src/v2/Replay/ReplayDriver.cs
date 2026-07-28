using System;
using System.Collections.Generic;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Comparison;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;

namespace SignalRouter.V2.Replay
{
    /// <summary>
    /// Strict replay execution (recording-replay.md §6): entry-by-entry
    /// re-admission into the factory-built isolated twin, typed comparison at
    /// every evidence cut, stop at the first non-Equal answer. The driver is the
    /// twin's single consumer: it owns pumping, and the capture coordinator it
    /// injects observes the twin's E2/E3/E4 material at the exact bases the
    /// evidence discipline fixes. Wait resolutions re-evaluate their predicate
    /// and require it to become true — never bytewise witness equality — and
    /// assertions require the recorded outcome to recur (guarantees.md §5.6,
    /// §5.10). Single-flight: one execution per driver instance. Gating the
    /// LIVE runtime's mutation lane for the duration is the caller's
    /// orchestration around this call — the driver only ever touches the twin.
    /// </summary>
    public sealed class ReplayDriver
    {
        private readonly Codec.CanonicalState.CanonicalStateCodec codec;
        private readonly SemanticComparator comparator;
        private bool executed;

        public ReplayDriver(
            Codec.CanonicalState.CanonicalStateCodec codec, ComparisonVocabulary vocabulary)
        {
            this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
            comparator = new SemanticComparator(
                vocabulary ?? throw new ArgumentNullException(nameof(vocabulary)));
        }

        public ReplayReport Execute(
            ReplayPlan plan,
            ReplayAllowlist allowlist,
            IReplayEnvironmentFactory factory,
            ISecretReferenceResolver? secretResolver,
            byte[] redactionKey,
            ReplayMode mode)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (allowlist == null)
            {
                throw new ArgumentNullException(nameof(allowlist));
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            if (redactionKey == null)
            {
                // The shared redaction material (ADR 0015): the host supplies
                // the recording runtime's key so resolved secrets re-digest
                // against the recorded keyed digests.
                throw new ArgumentNullException(nameof(redactionKey));
            }

            if (executed)
            {
                throw new InvalidOperationException("A replay driver executes exactly once.");
            }

            executed = true;

            var capture = new ReplayCaptureCoordinator(plan.Profile.RecordView, plan.Profile.Scope);
            using var environment = factory.Create(plan.Opened, capture);
            var run = new Run(
                this, plan, allowlist, environment, capture, secretResolver, redactionKey, mode);
            return run.Execute();
        }

        /// <summary>One replay run's mutable state, kept off the reusable driver surface.</summary>
        private sealed class Run
        {
            private readonly ReplayDriver driver;
            private readonly ReplayPlan plan;
            private readonly ReplayAllowlist allowlist;
            private readonly IReplayEnvironment environment;
            private readonly KernelRuntime runtime;
            private readonly ReplayCaptureCoordinator capture;
            private readonly ISecretReferenceResolver? secrets;
            private readonly byte[] redactionKey;
            private readonly ReplayMode mode;
            private readonly Dictionary<RequestId, ReplayEntry> entriesByRequest =
                new Dictionary<RequestId, ReplayEntry>();
            private readonly Dictionary<RequestId, SubmissionProbe> probes =
                new Dictionary<RequestId, SubmissionProbe>();

            // Recorded → twin request ids: identity for re-submitted roots,
            // rebound for continuation children the twin admits itself (§5.8).
            private readonly Dictionary<RequestId, RequestId> requestMap =
                new Dictionary<RequestId, RequestId>();
            internal Run(
                ReplayDriver driver,
                ReplayPlan plan,
                ReplayAllowlist allowlist,
                IReplayEnvironment environment,
                ReplayCaptureCoordinator capture,
                ISecretReferenceResolver? secrets,
                byte[] redactionKey,
                ReplayMode mode)
            {
                this.driver = driver;
                this.plan = plan;
                this.allowlist = allowlist;
                this.environment = environment;
                runtime = environment.Runtime;
                this.capture = capture;
                this.secrets = secrets;
                this.redactionKey = redactionKey;
                this.mode = mode;
                for (var index = 0; index < plan.Entries.Count; index++)
                {
                    entriesByRequest[plan.Entries[index].Request] = plan.Entries[index];
                }
            }

            internal ReplayReport Execute()
            {
                var cuts = plan.Reading.Cuts;
                for (var index = 0; index < cuts.Count; index++)
                {
                    var cut = cuts[index];
                    if (plan.Stop != null && !(cut.Sequence < plan.Stop.Position))
                    {
                        // Everything before the planned stop compared Equal; the
                        // stop itself is the pre-scan's verdict.
                        return ReplayReport.StoppedByPlan(plan.Stop);
                    }

                    var report = cut switch
                    {
                        RecordingOpened opened => CompareBase(opened),
                        AdmissionCut admission => ReplayAdmission(admission),
                        EffectPermit permit => ComparePermit(permit),
                        TerminalCut terminal => CompareTerminal(terminal),
                        PredicateArmed _ => null,
                        PredicateResolved resolved => ReevaluateWait(resolved),
                        AssertionEvaluated assertion => ReevaluateAssertion(assertion),
                        RecordingClosed closed => CompareFinal(closed),
                        _ => null,
                    };
                    if (report != null)
                    {
                        return report;
                    }
                }

                return ReplayReport.AllEqual();
            }

            // ── E1 / E7: base and final checkpoints ──────────────────────────

            private ReplayReport? CompareBase(RecordingOpened opened)
            {
                var recorded = DecodeBlob(opened.BaseSnapshot, default);
                var twin = MaterializeTwinView();
                return CompareStates(
                    opened.Sequence, "BaseState", recorded, opened.BaseSnapshot, twin);
            }

            private ReplayReport? CompareFinal(RecordingClosed closed)
            {
                var recorded = DecodeBlob(closed.FinalCheckpoint, default);
                var twin = MaterializeTwinView();
                return CompareStates(
                    closed.Sequence, "FinalState", recorded, closed.FinalCheckpoint, twin);
            }

            // ── E2: re-admission ─────────────────────────────────────────────

            private ReplayReport? ReplayAdmission(AdmissionCut admission)
            {
                var entry = entriesByRequest[admission.RequestId];
                if (admission.Envelope.Causality.Kind == CausalityKind.Continuation)
                {
                    // A continuation child is admitted by the twin itself when
                    // its parent's completion spawns it — never re-submitted.
                    return AwaitContinuationAdmission(admission);
                }

                var payloadFailure = TryBuildPayload(admission, out var payload);
                if (payloadFailure != null)
                {
                    // Stops BEFORE the affected entry (ADR 0015): nothing was
                    // submitted, no order consumed.
                    return ReplayReport.Diverged(admission.Sequence, payloadFailure, null);
                }

                requestMap[admission.RequestId] = admission.RequestId;
                var probe = new SubmissionProbe();
                probes[admission.RequestId] = probe;
                runtime.Ingress.Submit(new IntentSubmission(
                    admission.RequestId,
                    admission.Invocation.Contract,
                    TargetReference.ForKey(admission.ResolvedTarget.AuthorKey!.Value),
                    payload,
                    admission.Envelope,
                    probe));

                if (entry.Kind == ReplayEntryKind.PreCancelled)
                {
                    // The synthetic pre-cancelled token (guarantees.md §5.7):
                    // one minimal step admits, then the cancel lands while the
                    // interaction still waits. A frame-grained environment
                    // cannot honor this timing — its whole-frame step runs the
                    // effect, and the terminal comparison reports the honest
                    // divergence.
                    environment.Advance();
                    runtime.Control.RequestCancel(admission.RequestId);
                }

                if (entry.Kind == ReplayEntryKind.Rejected)
                {
                    PumpUntil(() => probe.Answered || HasTerminal(admission.RequestId));

                    // A post-admission rejection still carries E2 identity: when
                    // the twin admitted before rejecting, the fingerprints must
                    // agree.
                    if (capture.TryGet(admission.RequestId, out var rejectedCapture) &&
                        rejectedCapture.Admission != null &&
                        !rejectedCapture.Admission.Fingerprint.Equals(admission.Fingerprint))
                    {
                        return ReplayReport.Diverged(
                            admission.Sequence, "AdmissionFingerprint", null);
                    }

                    return null;
                }

                PumpUntil(() =>
                    probe.Answered ||
                    (capture.TryGet(admission.RequestId, out var captured) &&
                        captured.Admission != null));
                if (probe.RejectionReason != null)
                {
                    return ReplayReport.Diverged(
                        admission.Sequence, "AdmissionRefused", ReasonDiff(
                            "entries/" + admission.RequestId.Value,
                            "accepted",
                            probe.RejectionReason.Value.Value));
                }

                capture.TryGet(admission.RequestId, out var entry2);
                if (!entry2.Admission!.Fingerprint.Equals(admission.Fingerprint))
                {
                    // One inequality covers the whole identity: capability,
                    // target, and the redacted argument digest — a substituted
                    // secret surfaces here through the keyed digest
                    // (guarantees.md §5.2; ADR 0015).
                    return ReplayReport.Diverged(
                        admission.Sequence, "AdmissionFingerprint", null);
                }

                return null;
            }

            private ReplayReport? AwaitContinuationAdmission(AdmissionCut admission)
            {
                var link = admission.Envelope.Causality.Continuation!.Value;
                PumpUntil(() => FindContinuation(link) != null);
                var captured = FindContinuation(link)!;
                if (!captured.Fingerprint.Equals(admission.Fingerprint))
                {
                    return ReplayReport.Diverged(
                        admission.Sequence, "ContinuationFingerprint", null);
                }

                // Bound by (parent, ordinal), never by id (guarantees.md §5.8):
                // the twin's child id is its own; later cuts resolve through
                // the map.
                requestMap[admission.RequestId] = captured.Request;
                return null;
            }

            private AdmissionEvidence? FindContinuation(ContinuationLink link)
            {
                for (var index = 0; index < capture.AdmissionOrder.Count; index++)
                {
                    if (capture.TryGet(capture.AdmissionOrder[index], out var captured) &&
                        captured.Admission != null &&
                        captured.Admission.Envelope.Causality.Kind == CausalityKind.Continuation &&
                        captured.Admission.Envelope.Causality.Continuation!.Value.Equals(link))
                    {
                        return captured.Admission;
                    }
                }

                return null;
            }

            /// <summary>Null on success; the structured stop code otherwise — nothing submitted either way.</summary>
            private string? TryBuildPayload(AdmissionCut admission, out InvocationPayload payload)
            {
                var fields = new NamedField[admission.Arguments.Fields.Count];
                for (var index = 0; index < admission.Arguments.Fields.Count; index++)
                {
                    var argument = admission.Arguments.Fields[index];
                    if (argument.IsSecret)
                    {
                        if (secrets == null ||
                            !secrets.TryResolve(
                                argument.Secret, argument.SecretValueDigest, out var resolved))
                        {
                            payload = InvocationPayload.Empty;
                            return "SecretUnresolvable";
                        }

                        // The explicit re-digest against the recorded keyed
                        // digest, BEFORE the entry executes (ADR 0015): a
                        // substituted secret stops here, never a silent
                        // substitution — and never a consumed admission.
                        if (!InvocationCanonicalizer.SensitiveValueDigest(redactionKey, resolved)
                            .Equals(argument.SecretValueDigest))
                        {
                            payload = InvocationPayload.Empty;
                            return "SecretDigestMismatch";
                        }

                        fields[index] = new NamedField(argument.Name, resolved);
                    }
                    else
                    {
                        fields[index] = new NamedField(argument.Name, argument.Value);
                    }
                }

                payload = new InvocationPayload(ValueArray<NamedField>.From(fields));
                return null;
            }

            // ── E3: the before view ──────────────────────────────────────────

            private ReplayReport? ComparePermit(EffectPermit permit)
            {
                var twinId = MapId(permit.RequestId);
                PumpUntil(() =>
                    capture.TryGet(twinId, out var captured) &&
                    (captured.Before != null || captured.MaterializationFailed));
                capture.TryGet(twinId, out var entry);
                if (entry.Before == null)
                {
                    throw new InvalidOperationException(
                        "The twin's before view did not materialize.");
                }

                var recorded = DecodeBlob(permit.BeforeView, permit.Watermark);
                return CompareStates(
                    permit.Sequence, "BeforeState", recorded, permit.BeforeView, entry.Before);
            }

            private RequestId MapId(RequestId recorded) =>
                requestMap.TryGetValue(recorded, out var twin) ? twin : recorded;

            // ── E4: the terminal ─────────────────────────────────────────────

            private ReplayReport? CompareTerminal(TerminalCut terminal)
            {
                var entry = entriesByRequest[terminal.RequestId];
                if (entry.Kind == ReplayEntryKind.Rejected)
                {
                    return CompareRejectedTerminal(terminal);
                }

                var twinId = MapId(terminal.RequestId);
                PumpUntil(() =>
                    capture.TryGet(twinId, out var captured) && captured.Terminal != null);
                capture.TryGet(twinId, out var twin);
                var observed = twin.Terminal!;

                if (observed.Outcome != terminal.Outcome)
                {
                    return ReplayReport.Diverged(
                        terminal.Sequence, "TerminalOutcome", ReasonDiff(
                            "entries/" + terminal.RequestId.Value,
                            terminal.Outcome.ToString(),
                            observed.Outcome.ToString()));
                }

                if (observed.EffectPermitted != terminal.EffectPermitted)
                {
                    return ReplayReport.Diverged(terminal.Sequence, "EffectPermitted", null);
                }

                if (!Nullable.Equals(observed.FaultCode, terminal.FaultCode))
                {
                    return ReplayReport.Diverged(terminal.Sequence, "FaultCode", null);
                }

                if (!Equals(observed.Completion, terminal.CompletionEvidence))
                {
                    return ReplayReport.Diverged(terminal.Sequence, "CompletionEvidence", null);
                }

                if (!Nullable.Equals(observed.Postcondition, terminal.Postcondition))
                {
                    return ReplayReport.Diverged(terminal.Sequence, "Postcondition", null);
                }

                var commitments = CompareCommitments(terminal, observed);
                if (commitments != null)
                {
                    return commitments;
                }

                // A cancellation on a non-Cancelled terminal is timing metadata
                // outside strict comparison (guarantees.md §5.7); a Cancelled
                // terminal here is BeforeEffect by pre-scan construction.
                if (terminal.Outcome == InteractionOutcome.Cancelled &&
                    observed.Cancellation?.Phase != terminal.Cancellation!.Phase)
                {
                    return ReplayReport.Diverged(terminal.Sequence, "CancellationPhase", null);
                }

                if (twin.MaterializationFailed && twin.After == null)
                {
                    throw new InvalidOperationException(
                        "The twin's after view did not materialize.");
                }

                var after = twin.After;
                if (after == null)
                {
                    // Rejection-like terminals leave no retained basis; the
                    // current view is the after state.
                    after = MaterializeTwinView();
                }

                var recorded = DecodeBlob(terminal.AfterView, default);
                return CompareStates(
                    terminal.Sequence, "AfterState", recorded, terminal.AfterView, after);
            }

            private ReplayReport? CompareRejectedTerminal(TerminalCut terminal)
            {
                probes.TryGetValue(terminal.RequestId, out var probe);
                capture.TryGet(MapId(terminal.RequestId), out var twin);
                var observedReason = twin?.Terminal?.RejectionReason ?? probe?.RejectionReason;
                if (observedReason == null)
                {
                    return ReplayReport.Diverged(
                        terminal.Sequence, "TerminalOutcome", ReasonDiff(
                            "entries/" + terminal.RequestId.Value,
                            "Rejected", "accepted"));
                }

                if (!observedReason.Value.Equals(terminal.RejectionReason!.Value))
                {
                    return ReplayReport.Diverged(
                        terminal.Sequence, "RejectionReason", ReasonDiff(
                            "entries/" + terminal.RequestId.Value,
                            terminal.RejectionReason.Value.Value,
                            observedReason.Value.Value));
                }

                // The zero-effect guarantee: no permit was minted twin-side.
                capture.TryGet(terminal.RequestId, out var captured);
                if (captured?.Before != null)
                {
                    return ReplayReport.Diverged(terminal.Sequence, "RejectedWithEffect", null);
                }

                var recorded = DecodeBlob(terminal.AfterView, default);
                return CompareStates(
                    terminal.Sequence, "AfterState", recorded, terminal.AfterView,
                    MaterializeTwinView());
            }

            private ReplayReport? CompareCommitments(TerminalCut terminal, TerminalEvidence observed)
            {
                if (observed.Commitments.Count != terminal.Continuations.Count)
                {
                    return ReplayReport.Diverged(terminal.Sequence, "Commitments", null);
                }

                for (var index = 0; index < terminal.Continuations.Count; index++)
                {
                    if (!observed.Commitments[index].Equals(terminal.Continuations[index]))
                    {
                        return ReplayReport.Diverged(terminal.Sequence, "Commitments", null);
                    }
                }

                return null;
            }

            // ── E6 / E8: re-evaluation in place ──────────────────────────────

            private ReplayReport? ReevaluateWait(PredicateResolved resolved)
            {
                // Only Satisfied resolutions reach the driver (the rest are
                // planned stops): re-evaluate at this position and require it
                // to become true — never bytewise witness equality (§5.6).
                var definition = DefinitionFor(resolved.OperationId);
                var result = PredicateEvaluator.Evaluate(
                    definition,
                    new MaterializationLookup(MaterializeTwinView().Materialization),
                    PredicateStructuralBounds.Default);
                if (result.Outcome.Kind == PredicateEvaluationKind.Unevaluable)
                {
                    // An evaluation that cannot be performed at replay answers
                    // Incomparable(reason), verbatim (guarantees.md §3.3).
                    return ReplayReport.Incomparable(
                        resolved.Sequence,
                        IncomparableReason.FromUnevaluable(result.Outcome.Reason));
                }

                if (result.Outcome.Kind != PredicateEvaluationKind.Satisfied)
                {
                    return ReplayReport.Diverged(resolved.Sequence, "WaitNotSatisfied", null);
                }

                return null;
            }

            private ReplayReport? ReevaluateAssertion(AssertionEvaluated assertion)
            {
                // A recorded Unevaluable is a planned stop; Satisfied and False
                // must recur (§5.10 — replay fidelity, not the case verdict).
                var definition = PredicateDefinitionFor(assertion.Predicate);
                var result = PredicateEvaluator.Evaluate(
                    definition,
                    new MaterializationLookup(MaterializeTwinView().Materialization),
                    PredicateStructuralBounds.Default);
                if (result.Outcome.Kind == PredicateEvaluationKind.Unevaluable)
                {
                    // An evaluation that cannot be performed at replay answers
                    // Incomparable(reason) (guarantees.md §5.10, §3.3).
                    return ReplayReport.Incomparable(
                        assertion.Sequence,
                        IncomparableReason.FromUnevaluable(result.Outcome.Reason));
                }

                if (result.Outcome.Kind != assertion.Outcome.Kind)
                {
                    return ReplayReport.Diverged(
                        assertion.Sequence, "AssertionOutcome", ReasonDiff(
                            "assertions/" + assertion.Predicate.Id.Value,
                            assertion.Outcome.ToString(),
                            result.Outcome.ToString()));
                }

                return null;
            }

            private PredicateDefinition DefinitionFor(OperationId operation)
            {
                // The armed cut names the contract; the allowlisted definition
                // is digest-pinned to it by the pre-scan.
                for (var index = 0; index < plan.Reading.Cuts.Count; index++)
                {
                    if (plan.Reading.Cuts[index] is PredicateArmed armed &&
                        armed.OperationId.Equals(operation))
                    {
                        return PredicateDefinitionFor(armed.Predicate);
                    }
                }

                throw new InvalidOperationException(
                    "A resolved wait without its armed cut survived the pre-scan.");
            }

            private PredicateDefinition PredicateDefinitionFor(PredicateContractRef reference)
            {
                for (var index = 0; index < allowlist.Predicates.Count; index++)
                {
                    if (allowlist.Predicates[index].Reference.Equals(reference))
                    {
                        return allowlist.Predicates[index].Definition;
                    }
                }

                throw new InvalidOperationException(
                    "A predicate outside the allowlist survived the pre-scan.");
            }

            // ── Shared machinery ─────────────────────────────────────────────

            private ReplayReport? CompareStates(
                EvidenceSequence position,
                string site,
                ObservationMaterialization recorded,
                ContentId recordedId,
                RecordMaterialization twin)
            {
                if (mode == ReplayMode.ExactArtifact)
                {
                    // This mode is defined by canonical equality alone (§5.3):
                    // unequal ids are the verdict; the typed comparator only
                    // explains the mismatch when it can.
                    if (recordedId.Equals(twin.Canonical.Id))
                    {
                        return null;
                    }

                    var explanation = driver.comparator.CompareState(
                        recorded, twin.Materialization, plan.Profile);
                    return ReplayReport.Diverged(
                        position, "CanonicalMismatch",
                        explanation.Outcome.Kind == ReplayComparisonKind.Diverged
                            ? explanation.Diff
                            : null);
                }

                // StrictSemantic: the typed comparator is the verdict, and its
                // eligibility gates (completeness, mandatory extensions) must
                // run even when the canonical ids happen to match — a hash fast
                // path here would bypass Incomparable answers (§5.1 makes the
                // fast path an optimization, never a different semantics).
                var typed = driver.comparator.CompareState(
                    recorded, twin.Materialization, plan.Profile);
                switch (typed.Outcome.Kind)
                {
                    case ReplayComparisonKind.Incomparable:
                        return ReplayReport.Incomparable(position, typed.Outcome.Reason);
                    case ReplayComparisonKind.Diverged:
                        return ReplayReport.Diverged(position, site, typed.Diff);
                    default:
                        return null;
                }
            }

            private ObservationMaterialization DecodeBlob(ContentId id, SourceRevision revision)
            {
                if (!plan.Reading.TryGetBlob(id, out var payload))
                {
                    // The reader verified every reference; a missing blob here
                    // is unreachable by construction.
                    throw new InvalidOperationException("A referenced blob survived the pre-scan unresolved.");
                }

                // The temporal legs are provenance, never comparison material
                // (ADR 0012): the recorded incarnation and the cut's watermark
                // merely satisfy the decode tuple.
                return driver.codec.Decode(payload, plan.Opened.Incarnation, revision);
            }

            private RecordMaterialization MaterializeTwinView()
            {
                // The driver is the twin's single consumer thread: reads between
                // pumps observe the same quiescent state a pump would.
                if (!runtime.RecordObservation.TryMaterializeView(
                    plan.Profile.RecordView, plan.Profile.Scope, null, out var materialization, out _))
                {
                    throw new InvalidOperationException("The twin's record view did not materialize.");
                }

                return materialization!;
            }

            private bool HasTerminal(RequestId request) =>
                capture.TryGet(MapId(request), out var captured) && captured.Terminal != null;

            private void PumpUntil(Func<bool> condition)
            {
                // One environment step at a time: the condition gates every
                // step, so the twin can never run past a boundary the driver
                // has not yet compared (an admission-only entry must not reach
                // its effect on a pump-grained world). An idle twin gets a
                // bounded grace window for an asynchronous executor to report
                // its completion before it counts as stalled.
                const int MaxStepsPerWait = 65536;
                var idleGrace = 256;
                for (var step = 0; step < MaxStepsPerWait; step++)
                {
                    if (condition())
                    {
                        return;
                    }

                    if (!environment.Advance() && !condition())
                    {
                        if (--idleGrace <= 0)
                        {
                            throw new InvalidOperationException(
                                "The twin stalled: the expected evidence never arrived.");
                        }

                        System.Threading.Thread.Sleep(1);
                    }
                }

                if (!condition())
                {
                    throw new InvalidOperationException(
                        "The twin stalled: the expected evidence never arrived.");
                }
            }

            private static SemanticDiff ReasonDiff(string path, string recorded, string actual) =>
                new SemanticDiff(ValueArray<SemanticDiffEntry>.From(new[]
                {
                    new SemanticDiffEntry(path, "ValueMismatch", recorded, actual),
                }));

            private sealed class SubmissionProbe : ISubmissionObserver
            {
                internal bool Answered { get; private set; }

                internal RejectionReason? RejectionReason { get; private set; }

                public void OnAccepted(RequestId request) => Answered = true;

                public void OnRejected(RequestId request, RejectionReason reason)
                {
                    Answered = true;
                    RejectionReason = reason;
                }
            }
        }
    }
}
