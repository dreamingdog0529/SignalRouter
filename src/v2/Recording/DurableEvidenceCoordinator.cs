using System;
using System.Collections.Generic;
using SignalRouter.V2.Codec.Recording;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;

namespace SignalRouter.V2.Recording
{
    /// <summary>
    /// The durable evidence coordinator (ADR 0015): owns the E1–E4/E7
    /// durability obligations over one artifact at a time, writing through the
    /// RecordingEventSchema leaf in the StateStore-first order — lease, blob,
    /// cut, cut-level lease release, then Ready. Pump-thread only; no threads,
    /// no timers. Outside an open recording every evidence answer is vacuously
    /// Ready and nothing persists (the kernel's E2/E3/E4 sites fire in every
    /// phase — the vacuous discipline is this class's contract, ADR 0015).
    /// Degradation flows only through <see cref="CloseRequested"/>; the kernel
    /// owns the fences.
    /// </summary>
    public sealed class DurableEvidenceCoordinator : IRecordingCoordinator
    {
        private readonly IArtifactStore store;
        private readonly RecordingCoordinatorOptions options;
        private IRecordObservationServices? services;

        private ArtifactWriter? writer;
        private bool recordingActive;
        private OperationId recording;
        private ViewContractRef recordView;
        private RuntimeIncarnationId incarnation;
        private string scope = string.Empty;
        private ulong nextSequence;
        private int cutCount;
        private readonly HashSet<ContentId> reachableSet = new HashSet<ContentId>();
        private readonly List<ContentId> reachableOrder = new List<ContentId>();
        private readonly Dictionary<(RequestId Parent, int Ordinal), SemanticFingerprint>
            unresolvedCommitments = new Dictionary<(RequestId, int), SemanticFingerprint>();
        private int openStep;
        private RequestId? pendingPermitRequest;
        private RecordMaterialization? pendingPermitBefore;
        private ContentId pendingPermitContentId;
        private bool pendingPermitReused;
        private bool pendingPermitLeased;

        // Delta chain state (1.1): the last appended blob is the next delta
        // base; depths are tracked per ContentId so a dedup hit can never
        // launder a deep delta into a fresh checkpoint.
        private byte[]? deltaBaseBytes;
        private ContentId deltaBaseId;
        private readonly Dictionary<ContentId, int> blobDepths = new Dictionary<ContentId, int>();

        // Invalidation-token E3 reuse (guarantees.md §5.3): the record-view
        // blob most recently made durable, with the watermark it materialized
        // at. Cleared by an E5 barrier — no checkpoint reuse across one.
        private ContentId lastPermitBlobId;
        private SourceRevision? lastPermitBlobBasis;

        // TimelineTrack accounting (recording-replay.md §3).
        private long timelineWrittenBytes;
        private long droppedTimelineEvents;
        private bool closeGapWritten;
        private OperationId lastPollOperation;
        private SourceRevision lastPollRevision;

        public DurableEvidenceCoordinator(IArtifactStore store, RecordingCoordinatorOptions options)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public IncompleteReason? CloseRequested { get; private set; }

        public RecordingAdmissionPolicy AdmissionPolicy =>
            RecordingAdmissionPolicy.RefuseUnkeyedTargets;

        public void Bind(IRecordObservationServices boundServices)
        {
            if (services != null)
            {
                throw new KernelFaultException("Bind is valid exactly once.");
            }

            services = boundServices ?? throw new ArgumentNullException(nameof(boundServices));
        }

        // ── E1 ───────────────────────────────────────────────────────────────

        public EvidenceReadiness PrepareOpenEvidence(OpenEvidence evidence)
        {
            if (recordingActive)
            {
                return EvidenceReadiness.Fault;
            }

            if (!evidence.Profile.Equals(options.Profile.Reference) ||
                !evidence.RecordView.Equals(options.Profile.RecordView) ||
                !string.Equals(evidence.Scope, options.Profile.Scope, StringComparison.Ordinal) ||
                !evidence.RedactionPolicy.Equals(options.Profile.RedactionPolicy))
            {
                // Every profile-defining open field must agree with the embedded
                // document: E1 pins only the reference, so a divergent view,
                // scope, or redaction policy would record under rules replay
                // does not apply (ADR 0016).
                return EvidenceReadiness.Fault;
            }

            // The open is a resumable step sequence so a Pending storage answer
            // retries exactly the missing step, never duplicating a record.
            if (openStep == 0)
            {
                var created = store.Create(evidence.Recording.Value);
                if (!created.IsDurable && !options.AllowNonDurableStore)
                {
                    created.Dispose();
                    return EvidenceReadiness.Fault;
                }

                writer = new ArtifactWriter(created);
                openStep = 1;
            }

            if (openStep == 1)
            {
                switch (writer!.WriteHeader(evidence.Recording.Value, evidence.Incarnation))
                {
                    case WriteAnswer.Fault:
                        return FailOpen();
                    case WriteAnswer.InFlight:
                        return EvidenceReadiness.Pending;
                }

                openStep = 2;
            }

            if (openStep == 2)
            {
                switch (writer!.AppendProfile(options.Profile))
                {
                    case WriteAnswer.Fault:
                        return FailOpen();
                    case WriteAnswer.InFlight:
                        return EvidenceReadiness.Pending;
                }

                openStep = 3;
            }

            if (openStep == 3)
            {
                var answer = AppendBlobBounded(
                    evidence.BaseSnapshot.Snapshot.ContentId, evidence.BaseSnapshot);
                if (answer != EvidenceReadiness.Ready)
                {
                    return answer == EvidenceReadiness.Fault ? FailOpen() : answer;
                }

                lastPermitBlobId = evidence.BaseSnapshot.Snapshot.ContentId;
                lastPermitBlobBasis = evidence.BaseSnapshot.Snapshot.Basis.Revision;
                openStep = 4;
            }

            var opened = new RecordingOpened(
                new EvidenceSequence(nextSequence),
                evidence.Profile,
                evidence.RecordView,
                evidence.RedactionPolicy,
                evidence.Catalog.CompletionBindings,
                evidence.Catalog.StateSourceContracts,
                evidence.Catalog.PredicateContracts,
                evidence.Incarnation,
                evidence.BaseSnapshot.Snapshot.ContentId);
            switch (AppendCut(opened))
            {
                case EvidenceReadiness.Fault:
                    return FailOpen();
                case EvidenceReadiness.Pending:
                    return EvidenceReadiness.Pending;
            }

            services!.ReleaseLease(evidence.BaseSnapshot.Snapshot.ContentId, evidence.Recording);
            recording = evidence.Recording;
            recordView = evidence.RecordView;
            scope = evidence.Scope;
            incarnation = evidence.Incarnation;
            recordingActive = true;
            openStep = 0;
            return EvidenceReadiness.Ready;
        }

        private EvidenceReadiness FailOpen()
        {
            writer?.Dispose();
            writer = null;
            openStep = 0;
            return EvidenceReadiness.Fault;
        }

        // ── E2/E3/E4 (vacuous outside an open recording) ─────────────────────

        public EvidenceReadiness PrepareAdmissionEvidence(AdmissionEvidence evidence)
        {
            if (!recordingActive)
            {
                return EvidenceReadiness.Ready;
            }

            var cut = new AdmissionCut(
                new EvidenceSequence(nextSequence),
                evidence.Request,
                evidence.Order,
                evidence.Fingerprint,
                evidence.Invocation,
                evidence.Arguments,
                evidence.ResolvedTarget,
                evidence.Envelope);
            var answer = AppendCut(cut);
            if (answer == EvidenceReadiness.Ready &&
                evidence.Envelope.Causality.Kind == CausalityKind.Continuation)
            {
                var link = evidence.Envelope.Causality.Continuation!.Value;
                unresolvedCommitments.Remove((link.ParentRequestId, link.ContinuationOrdinal));
            }

            // An E2 fault refuses only this admission; the artifact stays
            // writable (ADR 0015 failure routing).
            return answer;
        }

        public EvidenceReadiness PrepareEffectPermit(PermitEvidence evidence)
        {
            if (!recordingActive)
            {
                return EvidenceReadiness.Ready;
            }

            // The permit is a resumable step: the reuse decision and the lease
            // are cached across Pending retries, so a retry can neither flip
            // reusedCheckpointBlob (the blob it wrote itself would read as a
            // checkpoint) nor leak or duplicate the lease.
            if (!pendingPermitRequest.HasValue || !pendingPermitRequest.Value.Equals(evidence.Request))
            {
                if (lastPermitBlobBasis.HasValue &&
                    lastPermitBlobBasis.Value.Equals(evidence.Watermark) &&
                    services!.RecordViewIsRevisionPure &&
                    writer!.ContainsBlob(lastPermitBlobId))
                {
                    // The invalidation token (guarantees.md §5.3): the watermark
                    // has not moved since the checkpoint materialized and the
                    // record view is a pure function of the revision, so
                    // NoRelevantMutation is proven — the prior checkpoint is
                    // this permit's before-state without a re-materialization.
                    // The barrier rule holds because an E5 clears the token.
                    pendingPermitRequest = evidence.Request;
                    pendingPermitBefore = null;
                    pendingPermitContentId = lastPermitBlobId;
                    pendingPermitReused = true;
                    pendingPermitLeased = false;
                }
                else if (services!.TryMaterializeView(
                    recordView, scope, evidence.Watermark, out var before, out _))
                {
                    pendingPermitRequest = evidence.Request;
                    pendingPermitBefore = before!;
                    pendingPermitContentId = before!.Snapshot.ContentId;
                    pendingPermitReused = writer!.ContainsBlob(pendingPermitContentId);
                    pendingPermitLeased = false;
                }
                else
                {
                    CloseRequested ??= new IncompleteReason("SinkFault");
                    return EvidenceReadiness.Fault;
                }
            }

            var beforeId = pendingPermitContentId;
            if (!pendingPermitReused)
            {
                if (!pendingPermitLeased)
                {
                    if (services!.TryLease(pendingPermitBefore!, recording) != LeaseAnswer.Retained)
                    {
                        CloseRequested ??= new IncompleteReason("SizeLimit");
                        ClearPendingPermit();
                        return EvidenceReadiness.Fault;
                    }

                    pendingPermitLeased = true;
                }

                var blobAnswer = AppendBlobBounded(beforeId, pendingPermitBefore!);
                if (blobAnswer == EvidenceReadiness.Pending)
                {
                    return EvidenceReadiness.Pending;
                }

                if (blobAnswer == EvidenceReadiness.Fault)
                {
                    ReleasePendingPermitLease(beforeId);
                    ClearPendingPermit();
                    return EvidenceReadiness.Fault;
                }
            }

            var cut = new EffectPermit(
                new EvidenceSequence(nextSequence),
                evidence.Request,
                evidence.Order,
                evidence.Watermark,
                beforeId,
                pendingPermitReused);
            var answer = AppendCut(cut);
            if (answer == EvidenceReadiness.Pending)
            {
                return EvidenceReadiness.Pending;
            }

            if (answer == EvidenceReadiness.Ready)
            {
                lastPermitBlobId = beforeId;
                lastPermitBlobBasis = evidence.Watermark;
            }

            ReleasePendingPermitLease(beforeId);
            ClearPendingPermit();
            return answer;
        }

        private void ReleasePendingPermitLease(ContentId beforeId)
        {
            if (pendingPermitLeased)
            {
                services!.ReleaseLease(beforeId, recording);
                pendingPermitLeased = false;
            }
        }

        private void ClearPendingPermit()
        {
            pendingPermitRequest = null;
            pendingPermitBefore = null;
            pendingPermitContentId = default;
            pendingPermitReused = false;
            pendingPermitLeased = false;
        }

        public EvidenceReadiness CommitTerminalEvidence(TerminalEvidence evidence)
        {
            if (!recordingActive)
            {
                return EvidenceReadiness.Ready;
            }

            if (!services!.TryGetAfterMaterialization(evidence.Request, out var after))
            {
                CloseRequested ??= new IncompleteReason("SinkFault");
                return EvidenceReadiness.Fault;
            }

            var afterId = after!.Snapshot.ContentId;
            if (!writer!.ContainsBlob(afterId))
            {
                var blobAnswer = AppendBlobBounded(afterId, after);
                if (blobAnswer != EvidenceReadiness.Ready)
                {
                    return blobAnswer;
                }
            }

            // The after-state is the freshest record-view checkpoint: the next
            // E3 at an unmoved watermark reuses it under the token condition.
            lastPermitBlobId = afterId;
            lastPermitBlobBasis = after.Snapshot.Basis.Revision;

            var cut = new TerminalCut(
                new EvidenceSequence(nextSequence),
                evidence.Request,
                evidence.Order,
                evidence.Outcome,
                evidence.EffectPermitted,
                afterId,
                evidence.RejectionReason,
                evidence.FaultCode,
                evidence.Completion,
                evidence.Postcondition,
                evidence.Cancellation,
                evidence.Commitments);
            var answer = AppendCut(cut);
            if (answer == EvidenceReadiness.Ready)
            {
                for (var i = 0; i < evidence.Commitments.Count; i++)
                {
                    unresolvedCommitments[(evidence.Request, evidence.Commitments[i].Ordinal)] =
                        evidence.Commitments[i].Fingerprint;
                }
            }

            return answer;
        }

        // ── E5/E6/E8 (vacuous outside an open recording) ─────────────────────

        public BarrierAnswer CommitExternalMutation(BarrierEvidence evidence)
        {
            if (!recordingActive)
            {
                return BarrierAnswer.Continue(EvidenceReadiness.Ready);
            }

            // No checkpoint reuse across a barrier (guarantees.md §5.3): the
            // token is cleared before anything else, even on Pending.
            lastPermitBlobBasis = null;
            lastPermitBlobId = default;

            // The interval endpoints are append positions (guarantees.md §5.5):
            // the last already-durable cut is the clean bound, and the barrier
            // itself is the first cut at-or-after the detection. recordingActive
            // implies E1 committed, so nextSequence is at least one.
            var readiness = AppendCut(new ExternalMutationBarrier(
                new EvidenceSequence(nextSequence),
                lastKnownCleanCut: new EvidenceSequence(nextSequence - 1),
                firstObservedCut: new EvidenceSequence(nextSequence),
                evidence.RevisionAtDetection,
                evidence.SourceHint,
                evidence.ContaminatedRequests));
            return options.ExternalMutation == ExternalMutationPolicy.Terminate
                ? BarrierAnswer.RequestClose(readiness, IncompleteReason.ExternalMutation)
                : BarrierAnswer.Continue(readiness);
        }

        public EvidenceReadiness CommitWaitArmed(WaitArmedEvidence evidence)
        {
            if (!recordingActive)
            {
                return EvidenceReadiness.Ready;
            }

            // E6a carries no snapshot: the witness is E6b's (guarantees.md §5.6).
            // The view contract and observation scope are the recording's own —
            // replay re-evaluates against the record-view materialization.
            return AppendCut(new PredicateArmed(
                new EvidenceSequence(nextSequence),
                evidence.Operation,
                evidence.Predicate,
                evidence.Operands,
                evidence.Fingerprint,
                recordView,
                scope,
                evidence.Causality,
                evidence.ArmedSequence));
        }

        public EvidenceReadiness CommitWaitResolved(WaitResolvedEvidence evidence)
        {
            if (!recordingActive)
            {
                return EvidenceReadiness.Ready;
            }

            var witnessId = evidence.Observation.Snapshot.ContentId;
            if (!writer!.ContainsBlob(witnessId))
            {
                var blobAnswer = AppendBlobBounded(witnessId, evidence.Observation);
                if (blobAnswer == EvidenceReadiness.Pending)
                {
                    return EvidenceReadiness.Pending;
                }

                if (blobAnswer == EvidenceReadiness.Fault)
                {
                    services!.ReleaseLease(witnessId, recording);
                    return EvidenceReadiness.Fault;
                }
            }

            var answer = AppendCut(new PredicateResolved(
                new EvidenceSequence(nextSequence),
                evidence.Operation,
                evidence.Resolution,
                witnessId,
                evidence.ResolvedSequence));
            if (answer != EvidenceReadiness.Pending)
            {
                // Cut-level release (ADR 0015): the kernel leased the witness at
                // the site; the pin ends with the final answer either way.
                services!.ReleaseLease(witnessId, recording);
            }

            return answer;
        }

        public EvidenceReadiness CommitAssertionEvidence(AssertionEvidence evidence)
        {
            if (!recordingActive)
            {
                return EvidenceReadiness.Ready;
            }

            var snapshot = evidence.Snapshot.Snapshot;
            var snapshotId = snapshot.ContentId;
            if (!writer!.ContainsBlob(snapshotId))
            {
                var blobAnswer = AppendBlobBounded(snapshotId, evidence.Snapshot);
                if (blobAnswer == EvidenceReadiness.Pending)
                {
                    return EvidenceReadiness.Pending;
                }

                if (blobAnswer == EvidenceReadiness.Fault)
                {
                    services!.ReleaseLease(snapshotId, recording);
                    return EvidenceReadiness.Fault;
                }
            }

            var answer = AppendCut(new AssertionEvaluated(
                new EvidenceSequence(nextSequence),
                incarnation,
                snapshot.Basis.Revision,
                recordView,
                evidence.StateSourceTableVersion,
                scope,
                evidence.Domain,
                snapshotId,
                completeForScope: snapshot.Completeness.Equals(CompletenessMap.Complete),
                evidence.Predicate,
                evidence.Operands,
                evidence.Clauses,
                evidence.Outcome,
                // Witness-path extraction is diagnostic material the evaluator
                // does not produce in v2.0: bounded to the empty set, never
                // fabricated (guarantees.md §5.10).
                ValueArray<string>.Empty));
            if (answer != EvidenceReadiness.Pending)
            {
                services!.ReleaseLease(snapshotId, recording);
            }

            return answer;
        }

        // ── E7 ───────────────────────────────────────────────────────────────

        public EvidenceReadiness CommitCloseEvidence(CloseEvidence evidence)
        {
            if (!recordingActive)
            {
                return EvidenceReadiness.Fault;
            }

            // Writer-side Completed pre-validation (ADR 0015): a Completed close
            // over unresolved continuation commitments would be overturned by
            // every reader (R3) — fail fast instead of writing a lie.
            if (evidence.Reason.IsCompleted && unresolvedCommitments.Count > 0)
            {
                EndRecording();
                return EvidenceReadiness.Fault;
            }

            MarkTimelineLoss();
            var finalId = evidence.FinalSnapshot.Snapshot.ContentId;
            if (!writer!.ContainsBlob(finalId))
            {
                var blobAnswer = AppendBlobBounded(finalId, evidence.FinalSnapshot, enforceBounds: false);
                if (blobAnswer == EvidenceReadiness.Fault)
                {
                    // The close cannot commit: the kernel resets to NotRecording
                    // on this answer, and the coordinator must end with it — a
                    // surviving writer would swallow the next recording.
                    EndRecording();
                    return EvidenceReadiness.Fault;
                }

                if (blobAnswer == EvidenceReadiness.Pending)
                {
                    return EvidenceReadiness.Pending;
                }
            }

            AddReachable(finalId);
            var declared = reachableOrder.ToArray();
            var closed = new RecordingClosed(
                new EvidenceSequence(nextSequence),
                evidence.Reason,
                declaredEventCount: cutCount + 1,
                finalId,
                ValueArray<ContentId>.From(declared));
            switch (AppendCut(closed, enforceBounds: false))
            {
                case EvidenceReadiness.Fault:
                    EndRecording();
                    return EvidenceReadiness.Fault;
                case EvidenceReadiness.Pending:
                    return EvidenceReadiness.Pending;
            }

            services!.ReleaseLease(finalId, evidence.Recording);
            EndRecording();
            return EvidenceReadiness.Ready;
        }

        public void NotifyTeardown()
        {
            if (!recordingActive)
            {
                writer?.Dispose();
                writer = null;
                return;
            }

            // Best-effort durable Incomplete(IncarnationChanged): observation
            // services are still addressable here (ADR 0015 teardown order). A
            // failure leaves the artifact for the reader to classify Interrupted.
            MarkTimelineLoss();
            if (services!.TryMaterializeView(recordView, scope, null, out var final, out _) &&
                final != null)
            {
                var finalId = final.Snapshot.ContentId;
                if (writer!.ContainsBlob(finalId) ||
                    AppendBlobBounded(finalId, final, enforceBounds: false) == EvidenceReadiness.Ready)
                {
                    AddReachable(finalId);
                    var declared = reachableOrder.ToArray();
                    _ = AppendCut(enforceBounds: false, cut: new RecordingClosed(
                        new EvidenceSequence(nextSequence),
                        RecordingCloseReason.Incomplete(new IncompleteReason("IncarnationChanged")),
                        declaredEventCount: cutCount + 1,
                        finalId,
                        ValueArray<ContentId>.From(declared)));
                }
            }

            EndRecording();
        }

        // ── Shared append discipline ─────────────────────────────────────────

        private EvidenceReadiness AppendCut(EvidenceCut cut, bool enforceBounds = true)
        {
            // One cut slot is reserved for E7 so the declared MaxEventCount is
            // never exceeded on file: non-close cuts stop one early, and the
            // close appends into the reserved slot. Bytes: non-close records
            // preflight their full framed size against MaxArtifactBytes; the
            // final blob and E7 are byte-exempt — an artifact must always be
            // able to end honestly (guarantees.md §8; RecordingCoordinatorOptions).
            if (enforceBounds && cutCount + 1 > options.MaxEventCount - 1)
            {
                CloseRequested ??= new IncompleteReason("SizeLimit");
                return EvidenceReadiness.Fault;
            }

            switch (writer!.AppendCut(
                cut, enforceBounds ? options.MaxArtifactBytes : long.MaxValue))
            {
                case WriteAnswer.OverBudget:
                    CloseRequested ??= new IncompleteReason("SizeLimit");
                    return EvidenceReadiness.Fault;
                case WriteAnswer.Committed:
                    nextSequence++;
                    cutCount++;
                    TrackReachable(cut);
                    return EvidenceReadiness.Ready;
                case WriteAnswer.InFlight:
                    return EvidenceReadiness.Pending;
                default:
                    CloseRequested ??= new IncompleteReason("SinkFault");
                    return EvidenceReadiness.Fault;
            }
        }

        private EvidenceReadiness AppendBlobBounded(
            ContentId id, RecordMaterialization materialization, bool enforceBounds = true)
        {
            var payload = materialization.Canonical.CopyPayload();
            if (enforceBounds && payload.Length > options.MaxBlobBytes)
            {
                CloseRequested ??= new IncompleteReason("SizeLimit");
                return EvidenceReadiness.Fault;
            }

            // Delta-encode against the previous blob while the chain bound
            // allows (recording-replay.md §4): every delta records base →
            // result; the bound forces a periodic full checkpoint. The close
            // path (enforceBounds: false) always writes full — an artifact's
            // final checkpoint never depends on a chain.
            var budget = enforceBounds ? options.MaxArtifactBytes : long.MaxValue;
            var effectiveChain = Math.Min(
                options.MaxDeltaChainLength, RecordingCoordinatorOptions.StoreMaxChainLength);
            WriteAnswer written;
            var wroteDelta = false;
            if (enforceBounds && effectiveChain > 0 && deltaBaseBytes != null &&
                blobDepths[deltaBaseId] < effectiveChain && !writer!.ContainsBlob(id))
            {
                written = writer.AppendBlobOrDelta(
                    id, payload, deltaBaseId, deltaBaseBytes, budget, out wroteDelta);
            }
            else
            {
                written = writer!.AppendBlob(id, payload, budget);
            }

            switch (written)
            {
                case WriteAnswer.Committed:
                    int depth;
                    if (wroteDelta)
                    {
                        depth = blobDepths[deltaBaseId] + 1;
                    }
                    else if (!blobDepths.TryGetValue(id, out depth))
                    {
                        depth = 0;
                    }

                    blobDepths[id] = depth;
                    deltaBaseBytes = payload;
                    deltaBaseId = id;
                    return EvidenceReadiness.Ready;
                case WriteAnswer.InFlight:
                    return EvidenceReadiness.Pending;
                case WriteAnswer.OverBudget:
                    return OverBudgetAnswer();
                default:
                    return FaultWithSink();
            }
        }

        // ── TimelineTrack (droppable diagnostics) ────────────────────────────

        public void OfferWaitPoll(WaitPollEvidence evidence)
        {
            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }

            if (!recordingActive || options.TimelineByteBudget < 1)
            {
                return;
            }

            // Coalesce: one poll per (wait, revision) — a repeat carries no
            // information (recording-replay.md §3 per-profile coalescing).
            if (evidence.Operation.Equals(lastPollOperation) &&
                evidence.Revision.Equals(lastPollRevision))
            {
                return;
            }

            lastPollOperation = evidence.Operation;
            lastPollRevision = evidence.Revision;
            AppendTimelineRecord(TimelineRecord.WaitPoll(
                evidence.Operation, evidence.Predicate, evidence.Revision));
        }

        private void AppendTimelineRecord(TimelineRecord entry)
        {
            var before = writer!.WrittenBytes;
            var budget = Math.Min(
                options.MaxArtifactBytes,
                before + (options.TimelineByteBudget - timelineWrittenBytes));
            switch (writer.AppendTimeline(entry, budget))
            {
                case WriteAnswer.Committed:
                    timelineWrittenBytes += writer.WrittenBytes - before;
                    break;
                case WriteAnswer.OverBudget:
                    droppedTimelineEvents++;
                    break;
                case WriteAnswer.InFlight:
                    // Nothing produces InFlight in v2.0 (ADR 0015); a droppable
                    // lane has no retry obligation — the event counts as lost.
                    droppedTimelineEvents++;
                    break;
                default:
                    // A physical write fault poisons the stream for every later
                    // record — the sink discipline is the evidence lane's.
                    droppedTimelineEvents++;
                    CloseRequested ??= new IncompleteReason("SinkFault");
                    break;
            }
        }

        private void MarkTimelineLoss()
        {
            // Loss is marked once, best-effort and byte-exempt like the close
            // records themselves — a failure here must never block the close.
            if (!closeGapWritten && droppedTimelineEvents > 0 &&
                options.TimelineByteBudget > 0)
            {
                closeGapWritten = true;
                _ = writer!.AppendTimeline(
                    TimelineRecord.Gap(droppedTimelineEvents), long.MaxValue);
            }
        }

        private EvidenceReadiness OverBudgetAnswer()
        {
            CloseRequested ??= new IncompleteReason("SizeLimit");
            return EvidenceReadiness.Fault;
        }

        private EvidenceReadiness FaultWithSink()
        {
            CloseRequested ??= new IncompleteReason("SinkFault");
            return EvidenceReadiness.Fault;
        }

        private void TrackReachable(EvidenceCut cut)
        {
            // The single shared definition (EvidenceSemantics) — a new cut kind
            // extends reachability once, for the writer, the reader, and closure
            // verification together.
            foreach (var contentId in EvidenceSemantics.ReferencedContentIds(cut))
            {
                AddReachable(contentId);
            }
        }

        // First-reference order: deterministic without depending on any digest rendering.
        private void AddReachable(ContentId id)
        {
            if (reachableSet.Add(id))
            {
                reachableOrder.Add(id);
            }
        }

        private void EndRecording()
        {
            writer?.Dispose();
            writer = null;
            recordingActive = false;
            recording = default;
            scope = string.Empty;
            recordView = default;
            incarnation = default;
            nextSequence = 0;
            cutCount = 0;
            reachableSet.Clear();
            reachableOrder.Clear();
            ClearPendingPermit();
            unresolvedCommitments.Clear();
            CloseRequested = null;
            openStep = 0;
            deltaBaseBytes = null;
            deltaBaseId = default;
            blobDepths.Clear();
            lastPermitBlobId = default;
            lastPermitBlobBasis = null;
            timelineWrittenBytes = 0;
            droppedTimelineEvents = 0;
            closeGapWritten = false;
            lastPollOperation = default;
            lastPollRevision = default;
        }
    }
}
