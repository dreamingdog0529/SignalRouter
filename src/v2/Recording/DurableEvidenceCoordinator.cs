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
        private string scope = string.Empty;
        private ulong nextSequence;
        private int cutCount;
        private readonly HashSet<ContentId> reachableSet = new HashSet<ContentId>();
        private readonly List<ContentId> reachableOrder = new List<ContentId>();
        private readonly Dictionary<(RequestId Parent, int Ordinal), SemanticFingerprint>
            unresolvedCommitments = new Dictionary<(RequestId, int), SemanticFingerprint>();
        private int openStep;

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

            if (!evidence.Profile.Equals(options.Profile.Reference))
            {
                // The open pins a profile this coordinator does not carry; an
                // artifact embedding a mismatched document would be degraded by
                // every reader (ADR 0016).
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

            if (!services!.TryMaterializeView(
                recordView, scope, evidence.Watermark, out var before, out _))
            {
                CloseRequested ??= new IncompleteReason("SinkFault");
                return EvidenceReadiness.Fault;
            }

            var beforeId = before!.Snapshot.ContentId;
            var reused = writer!.ContainsBlob(beforeId);
            if (!reused)
            {
                if (services.TryLease(before, recording) != LeaseAnswer.Retained)
                {
                    CloseRequested ??= new IncompleteReason("SizeLimit");
                    return EvidenceReadiness.Fault;
                }

                var blobAnswer = AppendBlobBounded(beforeId, before);
                if (blobAnswer != EvidenceReadiness.Ready)
                {
                    return blobAnswer;
                }
            }

            var cut = new EffectPermit(
                new EvidenceSequence(nextSequence),
                evidence.Request,
                evidence.Order,
                evidence.Watermark,
                beforeId,
                reused);
            var answer = AppendCut(cut);
            if (answer == EvidenceReadiness.Ready && !reused)
            {
                services.ReleaseLease(beforeId, recording);
            }

            return answer;
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
                return EvidenceReadiness.Fault;
            }

            var finalId = evidence.FinalSnapshot.Snapshot.ContentId;
            if (!writer!.ContainsBlob(finalId))
            {
                var blobAnswer = AppendBlobBounded(finalId, evidence.FinalSnapshot, enforceBounds: false);
                if (blobAnswer != EvidenceReadiness.Ready)
                {
                    return blobAnswer;
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
            // The close path is bound-exempt: an artifact must always be able to
            // end honestly — E7 (and its final blob) are the SizeLimit close,
            // not a violation of it (guarantees.md §8).
            if (enforceBounds &&
                (cutCount + 1 > options.MaxEventCount ||
                 writer!.WrittenBytes >= options.MaxArtifactBytes))
            {
                CloseRequested ??= new IncompleteReason("SizeLimit");
                return EvidenceReadiness.Fault;
            }

            switch (writer!.AppendCut(cut))
            {
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
            if (enforceBounds &&
                (payload.Length > options.MaxBlobBytes ||
                 writer!.WrittenBytes + payload.Length > options.MaxArtifactBytes))
            {
                CloseRequested ??= new IncompleteReason("SizeLimit");
                return EvidenceReadiness.Fault;
            }

            return writer!.AppendBlob(id, payload) switch
            {
                WriteAnswer.Committed => EvidenceReadiness.Ready,
                WriteAnswer.InFlight => EvidenceReadiness.Pending,
                _ => FaultWithSink(),
            };
        }

        private EvidenceReadiness FaultWithSink()
        {
            CloseRequested ??= new IncompleteReason("SinkFault");
            return EvidenceReadiness.Fault;
        }

        private void TrackReachable(EvidenceCut cut)
        {
            switch (cut)
            {
                case RecordingOpened opened:
                    AddReachable(opened.BaseSnapshot);
                    break;
                case EffectPermit permit:
                    AddReachable(permit.BeforeView);
                    break;
                case TerminalCut terminal:
                    AddReachable(terminal.AfterView);
                    break;
                case RecordingClosed closed:
                    AddReachable(closed.FinalCheckpoint);
                    break;
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
            nextSequence = 0;
            cutCount = 0;
            reachableSet.Clear();
            reachableOrder.Clear();
            unresolvedCommitments.Clear();
            CloseRequested = null;
            openStep = 0;
        }
    }
}
