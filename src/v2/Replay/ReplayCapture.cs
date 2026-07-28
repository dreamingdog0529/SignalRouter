using System;
using System.Collections.Generic;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;

namespace SignalRouter.V2.Replay
{
    /// <summary>
    /// The driver's observation seam inside the twin: a vacuous recording
    /// coordinator that persists nothing and captures the E2/E3/E4 material —
    /// including the exact before/after record-view materializations, taken
    /// inside the evidence callbacks where their basis is authoritative — for
    /// the driver to compare against the recorded cuts. Every readiness answer
    /// is Ready: replay evidence gating belongs to the recording, not the twin.
    /// </summary>
    internal sealed class ReplayCaptureCoordinator : IRecordingCoordinator
    {
        internal sealed class CapturedEntry
        {
            internal AdmissionEvidence? Admission { get; set; }

            internal RecordMaterialization? Before { get; set; }

            internal TerminalEvidence? Terminal { get; set; }

            internal RecordMaterialization? After { get; set; }

            /// <summary>A record-view materialization failed inside a callback — never silently absent.</summary>
            internal bool MaterializationFailed { get; set; }
        }

        private readonly ViewContractRef recordView;
        private readonly string scope;
        private readonly Dictionary<RequestId, CapturedEntry> entries =
            new Dictionary<RequestId, CapturedEntry>();
        private readonly List<RequestId> admissionOrder = new List<RequestId>();
        private IRecordObservationServices? services;

        internal ReplayCaptureCoordinator(ViewContractRef recordView, string scope)
        {
            this.recordView = recordView;
            this.scope = scope;
        }

        internal IReadOnlyList<RequestId> AdmissionOrder => admissionOrder;

        internal bool TryGet(RequestId request, out CapturedEntry entry) =>
            entries.TryGetValue(request, out entry!);

        public void Bind(IRecordObservationServices boundServices)
        {
            if (services != null)
            {
                throw new KernelFaultException("Bind is valid exactly once.");
            }

            services = boundServices ?? throw new ArgumentNullException(nameof(boundServices));
        }

        public EvidenceReadiness PrepareAdmissionEvidence(AdmissionEvidence evidence)
        {
            Entry(evidence.Request).Admission = evidence;
            admissionOrder.Add(evidence.Request);
            return EvidenceReadiness.Ready;
        }

        public EvidenceReadiness PrepareEffectPermit(PermitEvidence evidence)
        {
            // The exact before-basis: materialized inside the callback, at the
            // watermark the permit fixes (kernel-execution.md §5).
            if (services!.TryMaterializeView(
                recordView, scope, evidence.Watermark, out var before, out _))
            {
                Entry(evidence.Request).Before = before;
            }
            else
            {
                Entry(evidence.Request).MaterializationFailed = true;
            }

            return EvidenceReadiness.Ready;
        }

        public EvidenceReadiness CommitTerminalEvidence(TerminalEvidence evidence)
        {
            var entry = Entry(evidence.Request);
            entry.Terminal = evidence;

            // The exact after-basis under the PROFILE's record view: the twin
            // is not recording, so the kernel's retained after-basis uses its
            // raw view — the comparison surface must be the pinned one, at the
            // terminal's own watermark (kernel-execution.md §5).
            if (services!.TryMaterializeView(
                recordView, scope, evidence.AfterWatermark, out var after, out _))
            {
                entry.After = after;
            }
            else
            {
                entry.MaterializationFailed = true;
            }

            return EvidenceReadiness.Ready;
        }

        // ── Vacuous lifecycle: the twin never records ────────────────────────

        public EvidenceReadiness PrepareOpenEvidence(OpenEvidence evidence) => EvidenceReadiness.Ready;

        public EvidenceReadiness CommitCloseEvidence(CloseEvidence evidence) => EvidenceReadiness.Ready;

        public BarrierAnswer CommitExternalMutation(BarrierEvidence evidence) =>
            BarrierAnswer.Continue(EvidenceReadiness.Ready);

        public EvidenceReadiness CommitWaitArmed(WaitArmedEvidence evidence) => EvidenceReadiness.Ready;

        public EvidenceReadiness CommitWaitResolved(WaitResolvedEvidence evidence) => EvidenceReadiness.Ready;

        public EvidenceReadiness CommitAssertionEvidence(AssertionEvidence evidence) => EvidenceReadiness.Ready;

        public void NotifyTeardown()
        {
        }

        public IncompleteReason? CloseRequested => null;

        public RecordingAdmissionPolicy AdmissionPolicy => RecordingAdmissionPolicy.RefuseUnkeyedTargets;

        private CapturedEntry Entry(RequestId request)
        {
            if (!entries.TryGetValue(request, out var entry))
            {
                entry = new CapturedEntry();
                entries.Add(request, entry);
            }

            return entry;
        }
    }
}
