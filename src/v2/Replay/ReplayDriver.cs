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
        private const int MaxPumpsPerStep = 64;

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

            if (executed)
            {
                throw new InvalidOperationException("A replay driver executes exactly once.");
            }

            executed = true;

            var capture = new ReplayCaptureCoordinator(plan.Profile.RecordView, plan.Profile.Scope);
            using var environment = factory.Create(plan.Opened, capture);
            var run = new Run(
                this, plan, allowlist, environment.Runtime, capture, secretResolver, mode);
            return run.Execute();
        }

        /// <summary>One replay run's mutable state, kept off the reusable driver surface.</summary>
        private sealed class Run
        {
            private readonly ReplayDriver driver;
            private readonly ReplayPlan plan;
            private readonly ReplayAllowlist allowlist;
            private readonly KernelRuntime runtime;
            private readonly ReplayCaptureCoordinator capture;
            private readonly ISecretReferenceResolver? secrets;
            private readonly ReplayMode mode;
            private readonly Dictionary<RequestId, ReplayEntry> entriesByRequest =
                new Dictionary<RequestId, ReplayEntry>();
            private readonly Dictionary<RequestId, SubmissionProbe> probes =
                new Dictionary<RequestId, SubmissionProbe>();
            private long logicalNow = 1;

            internal Run(
                ReplayDriver driver,
                ReplayPlan plan,
                ReplayAllowlist allowlist,
                KernelRuntime runtime,
                ReplayCaptureCoordinator capture,
                ISecretReferenceResolver? secrets,
                ReplayMode mode)
            {
                this.driver = driver;
                this.plan = plan;
                this.allowlist = allowlist;
                this.runtime = runtime;
                this.capture = capture;
                this.secrets = secrets;
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

                if (!TryBuildPayload(admission, out var payload))
                {
                    // The pre-scan plans a stop for unresolvable references; a
                    // resolver that answered CanResolve but fails TryResolve
                    // stops at the same boundary.
                    return ReplayReport.Diverged(
                        admission.Sequence, "SecretUnresolvable", null);
                }

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
                    // admit in one turn, cancel while still queued.
                    PumpTurns(1);
                    runtime.Control.RequestCancel(admission.RequestId);
                }

                if (entry.Kind == ReplayEntryKind.Rejected)
                {
                    PumpUntil(() => probe.Answered || HasTerminal(admission.RequestId));
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

            private bool TryBuildPayload(AdmissionCut admission, out InvocationPayload payload)
            {
                var fields = new NamedField[admission.Arguments.Fields.Count];
                for (var index = 0; index < admission.Arguments.Fields.Count; index++)
                {
                    var argument = admission.Arguments.Fields[index];
                    if (argument.IsSecret)
                    {
                        // In-memory resolution; the twin's own canonicalization
                        // re-digests the value with the shared redaction
                        // material, so a substituted secret diverges at the
                        // admission fingerprint (ADR 0015).
                        if (secrets == null || !secrets.TryResolve(argument.Secret, out var resolved))
                        {
                            payload = InvocationPayload.Empty;
                            return false;
                        }

                        fields[index] = new NamedField(argument.Name, resolved);
                    }
                    else
                    {
                        fields[index] = new NamedField(argument.Name, argument.Value);
                    }
                }

                payload = new InvocationPayload(ValueArray<NamedField>.From(fields));
                return true;
            }

            // ── E3: the before view ──────────────────────────────────────────

            private ReplayReport? ComparePermit(EffectPermit permit)
            {
                PumpUntil(() =>
                    capture.TryGet(permit.RequestId, out var captured) && captured.Before != null);
                capture.TryGet(permit.RequestId, out var entry);
                var recorded = DecodeBlob(permit.BeforeView, permit.Watermark);
                return CompareStates(
                    permit.Sequence, "BeforeState", recorded, permit.BeforeView, entry.Before!);
            }

            // ── E4: the terminal ─────────────────────────────────────────────

            private ReplayReport? CompareTerminal(TerminalCut terminal)
            {
                var entry = entriesByRequest[terminal.RequestId];
                if (entry.Kind == ReplayEntryKind.Rejected)
                {
                    return CompareRejectedTerminal(terminal);
                }

                PumpUntil(() =>
                    capture.TryGet(terminal.RequestId, out var captured) && captured.Terminal != null);
                capture.TryGet(terminal.RequestId, out var twin);
                var observed = twin.Terminal!;

                if (observed.Outcome != terminal.Outcome)
                {
                    return ReplayReport.Diverged(
                        terminal.Sequence, "TerminalOutcome", ReasonDiff(
                            "entries/" + terminal.RequestId.Value,
                            terminal.Outcome.ToString(),
                            observed.Outcome.ToString()));
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
                capture.TryGet(terminal.RequestId, out var twin);
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
                // ContentId equality is only a fast path; inequality routes to
                // the typed comparator (recording-replay.md §5.1).
                if (recordedId.Equals(twin.Canonical.Id))
                {
                    return null;
                }

                var typed = driver.comparator.CompareState(
                    recorded, twin.Materialization, plan.Profile);
                switch (typed.Outcome.Kind)
                {
                    case ReplayComparisonKind.Incomparable:
                        return ReplayReport.Incomparable(position, typed.Outcome.Reason);
                    case ReplayComparisonKind.Diverged:
                        return ReplayReport.Diverged(position, site, typed.Diff);
                    default:
                        // Semantically equal under the profile. ExactArtifact
                        // demands canonical equality regardless (§5.3).
                        return mode == ReplayMode.ExactArtifact
                            ? ReplayReport.Diverged(position, "CanonicalMismatch", null)
                            : null;
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
                capture.TryGet(request, out var captured) && captured.Terminal != null;

            private void PumpTurns(int maxTurns)
            {
                runtime.Pump(new PumpBudget(
                    maxTurns, long.MaxValue, new LogicalTime(logicalNow++), FramePhase.Update));
            }

            private void PumpUntil(Func<bool> condition)
            {
                for (var pump = 0; pump < MaxPumpsPerStep; pump++)
                {
                    if (condition())
                    {
                        return;
                    }

                    PumpTurns(64);
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
