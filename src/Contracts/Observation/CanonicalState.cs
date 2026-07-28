using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// One canonical encoding answer (ADR 0011, observation-state.md §5.1): the
    /// `ContentId`, the canonical payload bytes, and the exact encoded length in one
    /// answer — so downstream consumers (the recording module's durable writes, the
    /// StateStore's byte accounting) never re-encode. The payload is defensively
    /// copied; a result is immutable.
    /// </summary>
    public sealed class CanonicalStateResult
    {
        private readonly byte[] payload;

        public CanonicalStateResult(ContentId id, byte[] canonicalPayload)
        {
            if (id.IsDefault)
            {
                throw new ArgumentException(
                    "A canonical-state result requires a non-default ContentId.", nameof(id));
            }

            if (canonicalPayload == null)
            {
                throw new ArgumentNullException(nameof(canonicalPayload));
            }

            if (canonicalPayload.Length == 0)
            {
                throw new ArgumentException(
                    "A canonical payload is never empty.", nameof(canonicalPayload));
            }

            Id = id;
            payload = new byte[canonicalPayload.Length];
            Array.Copy(canonicalPayload, payload, canonicalPayload.Length);
        }

        public ContentId Id { get; }

        /// <summary>The exact encoded length in bytes.</summary>
        public int Length => payload.Length;

        /// <summary>A defensive copy of the canonical payload.</summary>
        public byte[] CopyPayload()
        {
            var copy = new byte[payload.Length];
            Array.Copy(payload, copy, payload.Length);
            return copy;
        }
    }

    /// <summary>
    /// The injected canonical-state codec seam (ADR 0011/0012): implemented by the
    /// `Codec.CanonicalState` leaf, never by the BCL-only kernel. Encoding MUST be
    /// deterministic — same materialization, same bytes, same `ContentId` — and MUST
    /// derive the `ContentId` from the canonical bytes, which embed the projection
    /// identity (view contract, security domain, scope) and neither temporal leg:
    /// `RuntimeIncarnationId` and `SourceRevision` stay authoritative in the
    /// surrounding snapshot/cut tuple, so an unchanged state re-addresses to the
    /// same `ContentId` (guarantees.md §5.3 blob reuse). For a Contracts-valid
    /// materialization within the kernel's materialization bounds, `Encode` throws
    /// no input-derived exception (environmental failures excluded). A runtime
    /// configured without a codec degrades honestly: unaddressed snapshots, no
    /// StateStore retention, no timeline, no recording support
    /// (observation-state.md §2, §5.1).
    /// </summary>
    public interface ICanonicalStateCodec
    {
        CanonicalStateResult Encode(ObservationMaterialization materialization);
    }
}
