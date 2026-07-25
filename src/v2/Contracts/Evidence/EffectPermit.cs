using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// E3 ("BeforeCut") — the durable permit that gates the adapter invocation, taken
    /// immediately before invoking, not at admission (guarantees.md §5.3). Blob reuse
    /// is permitted; cut reuse is not — every permitted interaction gets a fresh E3.
    /// </summary>
    public sealed class EffectPermit : EvidenceCut
    {
        public EffectPermit(
            EvidenceSequence sequence,
            RequestId requestId,
            LogicalOrder logicalOrder,
            SourceRevision watermark,
            ContentId beforeView,
            bool reusedCheckpointBlob)
            : base(sequence)
        {
            if (requestId.IsDefault)
            {
                throw new ArgumentException("E3 requires a non-default RequestId.", nameof(requestId));
            }

            if (beforeView.IsDefault)
            {
                throw new ArgumentException("E3 requires a non-default before-view ContentId.", nameof(beforeView));
            }

            RequestId = requestId;
            LogicalOrder = logicalOrder;
            Watermark = watermark;
            BeforeView = beforeView;
            ReusedCheckpointBlob = reusedCheckpointBlob;
        }

        public override EvidenceCutKind Kind => EvidenceCutKind.EffectPermit;

        public RequestId RequestId { get; }

        public LogicalOrder LogicalOrder { get; }

        /// <summary>
        /// The observation revision fixed into the cut — the SourceRevision playing
        /// its ViewWatermark role (observation-state.md §4).
        /// </summary>
        public SourceRevision Watermark { get; }

        public ContentId BeforeView { get; }

        /// <summary>True when the referenced blob is a reused checkpoint under the §5.3 conditions.</summary>
        public bool ReusedCheckpointBlob { get; }
    }
}
