using System;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>
    /// One record-view materialization with its canonical encoding: what the
    /// durable evidence coordinator retains before it appends a cut
    /// (observation-state.md §5.1, ADR 0011).
    /// </summary>
    public sealed class RecordMaterialization
    {
        internal RecordMaterialization(
            ObservationSnapshot snapshot,
            ObservationMaterialization materialization,
            CanonicalStateResult canonical)
        {
            Snapshot = snapshot;
            Materialization = materialization;
            Canonical = canonical;
        }

        /// <summary>The addressed snapshot tuple (basis, ContentId, completeness).</summary>
        public ObservationSnapshot Snapshot { get; }

        public ObservationMaterialization Materialization { get; }

        public CanonicalStateResult Canonical { get; }
    }

    /// <summary>The structured cache-lease answer; the caller maps it onto its lane's failure matrix (guarantees.md §7).</summary>
    public enum LeaseAnswer
    {
        Retained,
        OverBudget,
        OverBlobBound,
        Unaddressable,
    }

    /// <summary>
    /// The StateStore-first capability the recording module (item 5) consumes
    /// (ADR 0011). Pump-thread only — valid during evidence-coordinator callbacks.
    /// The end-to-end order is: materialize → encode (inside
    /// <see cref="TryMaterializeView"/>) → <see cref="TryLease"/> (cache retain +
    /// pin) → the coordinator's own durable blob write → durable evidence append →
    /// `Ready`. The cache lease is not the durable commit and `Ready` never
    /// precedes durability.
    /// </summary>
    public interface IRecordObservationServices
    {
        /// <summary>False without a configured canonical-state codec — recording is unavailable.</summary>
        bool CanAddress { get; }

        /// <summary>
        /// Materializes a registered Record-family view at the current revision.
        /// When <paramref name="expectedBasis"/> is present and the revision has
        /// moved, answers false with <paramref name="basisMismatch"/> set — the E3
        /// response is re-materialization at the new revision, never a silent
        /// different-revision materialization (kernel-execution.md §5). Throws
        /// <see cref="KernelFaultException"/> for an unregistered or non-Record
        /// view, an invalid scope, or a codec-less runtime.
        /// </summary>
        bool TryMaterializeView(
            ViewContractRef view,
            string scope,
            SourceRevision? expectedBasis,
            out RecordMaterialization? materialization,
            out bool basisMismatch);

        /// <summary>
        /// Retains the blob in the cache and pins it for the recording — MUST
        /// precede the coordinator's durable append. Diagnostic (timeline) pins are
        /// released before this answers <see cref="LeaseAnswer.OverBudget"/>.
        /// </summary>
        LeaseAnswer TryLease(RecordMaterialization materialization, OperationId recording);

        /// <summary>
        /// The exact after-basis of an interaction's terminal (kernel-execution.md
        /// §5): the record-view materialization captured at the terminal decision,
        /// retained until the terminal evidence commits. E4 uses this, never a
        /// fresh materialization.
        /// </summary>
        bool TryGetAfterMaterialization(RequestId request, out RecordMaterialization? materialization);

        /// <summary>Releases every pin one recording holds.</summary>
        void ReleaseRecording(OperationId recording);
    }
}
