using System;
using System.Collections.Generic;
using SignalRouter.Codec.Shared;
using SignalRouter.Contracts;

namespace SignalRouter.Codec.Recording
{
    /// <summary>
    /// Writes one RecordingEventSchema@1.0 artifact (ADR 0016): the unframed
    /// header, then framed records — kind, length, payload, CRC32C over
    /// kind‖length‖payload, commit byte. Blob-before-reference is the caller's
    /// obligation (the durable coordinator appends the blob first); this writer
    /// enforces blob dedup by ContentId so reuse never writes a second copy.
    /// </summary>
    public sealed class ArtifactWriter : IDisposable
    {
        private readonly IArtifactStorage storage;
        private readonly HashSet<ContentId> writtenBlobs = new HashSet<ContentId>();
        private bool headerWritten;

        public ArtifactWriter(IArtifactStorage storage)
        {
            this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public long WrittenBytes => storage.WrittenBytes;

        public bool ContainsBlob(ContentId id) => writtenBlobs.Contains(id);

        public WriteAnswer WriteHeader(string artifactId, RuntimeIncarnationId incarnation)
        {
            if (headerWritten)
            {
                throw new InvalidOperationException("The header is written exactly once.");
            }

            ContractGrammar.ValidateIdentifier(artifactId, nameof(artifactId));
            if (incarnation.IsDefault)
            {
                throw new ArgumentException(
                    "The header requires a non-default incarnation.", nameof(incarnation));
            }

            var writer = PayloadWriter.Rent();
            try
            {
                writer.WriteMagic(0x53, 0x52, 0x52, 0x45); // "SRRE"
                writer.WriteVaruint(RecordingSchema.MajorVersion);
                writer.WriteVaruint(RecordingSchema.MinorVersion);
                writer.WriteString(artifactId);
                writer.WriteString(incarnation.Value);
                var answer = storage.Append(writer.WrittenSpan);
                headerWritten = answer == WriteAnswer.Committed;
                return answer;
            }
            finally
            {
                writer.Dispose();
            }
        }

        public WriteAnswer AppendProfile(ReplayComparisonProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            var writer = PayloadWriter.Rent();
            try
            {
                RecordingPayloadCodec.WriteProfile(ref writer, profile);
                return AppendRecord(RecordKind.ComparisonProfile, writer.WrittenSpan);
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>
        /// Appends a canonical blob. Idempotent by ContentId: a blob already in
        /// this artifact answers Committed without writing (E3/E4 reuse writes no
        /// second copy).
        /// </summary>
        public WriteAnswer AppendBlob(ContentId id, ReadOnlySpan<byte> canonicalPayload) =>
            AppendBlob(id, canonicalPayload, long.MaxValue);

        /// <summary>Budgeted variant: the complete framed blob record must fit, or OverBudget.</summary>
        public WriteAnswer AppendBlob(
            ContentId id, ReadOnlySpan<byte> canonicalPayload, long totalByteBudget)
        {
            if (id.IsDefault)
            {
                throw new ArgumentException("A blob requires a non-default ContentId.", nameof(id));
            }

            if (writtenBlobs.Contains(id))
            {
                return WriteAnswer.Committed;
            }

            var writer = PayloadWriter.Rent();
            try
            {
                RecordingPayloadCodec.WriteContentId(ref writer, id);
                writer.WriteVaruint(canonicalPayload.Length);
                foreach (var value in canonicalPayload)
                {
                    writer.WriteRaw(value);
                }

                var answer = AppendRecord(RecordKind.Blob, writer.WrittenSpan, totalByteBudget);
                if (answer == WriteAnswer.Committed)
                {
                    writtenBlobs.Add(id);
                }

                return answer;
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>
        /// Appends a blob, delta-encoded against <paramref name="baseId"/> when
        /// the delta record is strictly smaller than the full blob record —
        /// otherwise falls back to a full blob (1.1, ADR 0016). The base must
        /// already be in this artifact (blob-before-reference is byte order);
        /// chain-depth policy is the caller's.
        /// </summary>
        public WriteAnswer AppendBlobOrDelta(
            ContentId id,
            ReadOnlySpan<byte> canonicalPayload,
            ContentId baseId,
            ReadOnlySpan<byte> basePayload,
            long totalByteBudget,
            out bool wroteDelta)
        {
            wroteDelta = false;
            if (id.IsDefault)
            {
                throw new ArgumentException("A blob requires a non-default ContentId.", nameof(id));
            }

            if (!writtenBlobs.Contains(baseId))
            {
                throw new InvalidOperationException(
                    "The delta base must already be in this artifact.");
            }

            if (writtenBlobs.Contains(id))
            {
                return WriteAnswer.Committed;
            }

            var prefix = CommonPrefix(basePayload, canonicalPayload);
            var suffix = CommonSuffix(basePayload, canonicalPayload, prefix);
            var insert = canonicalPayload.Length - prefix - suffix;

            var delta = PayloadWriter.Rent();
            try
            {
                RecordingPayloadCodec.WriteContentId(ref delta, id);
                var idEncodedSize = delta.WrittenSpan.Length;
                RecordingPayloadCodec.WriteContentId(ref delta, baseId);
                delta.WriteVaruint(canonicalPayload.Length);
                delta.WriteVaruint(prefix);
                delta.WriteVaruint(suffix);
                delta.WriteVaruint(insert);
                for (var i = 0; i < insert; i++)
                {
                    delta.WriteRaw(canonicalPayload[prefix + i]);
                }

                var fullPayloadSize =
                    idEncodedSize + VaruintSize(canonicalPayload.Length) + canonicalPayload.Length;
                if (delta.WrittenSpan.Length < fullPayloadSize)
                {
                    var answer = AppendRecord(RecordKind.DeltaBlob, delta.WrittenSpan, totalByteBudget);
                    if (answer == WriteAnswer.Committed)
                    {
                        writtenBlobs.Add(id);
                        wroteDelta = true;
                    }

                    return answer;
                }
            }
            finally
            {
                delta.Dispose();
            }

            return AppendBlob(id, canonicalPayload, totalByteBudget);
        }

        /// <summary>Appends one TimelineTrack record (1.1) — droppable diagnostics, never evidence.</summary>
        public WriteAnswer AppendTimeline(TimelineRecord entry, long totalByteBudget)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var writer = PayloadWriter.Rent();
            try
            {
                RecordingPayloadCodec.WriteTimeline(ref writer, entry);
                return AppendRecord(RecordKind.Timeline, writer.WrittenSpan, totalByteBudget);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static int CommonPrefix(ReadOnlySpan<byte> baseBytes, ReadOnlySpan<byte> result)
        {
            var bound = Math.Min(baseBytes.Length, result.Length);
            var length = 0;
            while (length < bound && baseBytes[length] == result[length])
            {
                length++;
            }

            return length;
        }

        private static int CommonSuffix(
            ReadOnlySpan<byte> baseBytes, ReadOnlySpan<byte> result, int prefix)
        {
            var bound = Math.Min(baseBytes.Length, result.Length) - prefix;
            var length = 0;
            while (length < bound &&
                baseBytes[baseBytes.Length - 1 - length] == result[result.Length - 1 - length])
            {
                length++;
            }

            return length;
        }

        private static int VaruintSize(int value)
        {
            var size = 1;
            var remaining = (uint)value;
            while (remaining >= 0x80)
            {
                remaining >>= 7;
                size++;
            }

            return size;
        }

        public WriteAnswer AppendCut(EvidenceCut cut) => AppendCut(cut, long.MaxValue);

        /// <summary>
        /// Appends a cut only if the complete framed record fits inside
        /// <paramref name="totalByteBudget"/> (the preflight the declared
        /// RecordingSink byte bound needs — a partially counted bound is not a
        /// bound).
        /// </summary>
        public WriteAnswer AppendCut(EvidenceCut cut, long totalByteBudget)
        {
            if (cut == null)
            {
                throw new ArgumentNullException(nameof(cut));
            }

            var writer = PayloadWriter.Rent();
            try
            {
                RecordingPayloadCodec.WriteCut(ref writer, cut);
                return AppendRecord(RecordKind.EvidenceCut, writer.WrittenSpan, totalByteBudget);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private WriteAnswer AppendRecord(
            RecordKind kind, ReadOnlySpan<byte> payload, long totalByteBudget = long.MaxValue)
        {
            if (!headerWritten)
            {
                throw new InvalidOperationException("The header precedes every record.");
            }

            var framing = PayloadWriter.Rent();
            try
            {
                framing.WriteRaw((byte)kind);
                framing.WriteVaruint(payload.Length);
                foreach (var value in payload)
                {
                    framing.WriteRaw(value);
                }

                // The checksum covers kind ‖ length ‖ payload — everything framed
                // so far — followed by the commit byte (ADR 0016).
                var crc = Crc32C.Compute(framing.WrittenSpan);
                framing.WriteRaw((byte)(crc >> 24));
                framing.WriteRaw((byte)(crc >> 16));
                framing.WriteRaw((byte)(crc >> 8));
                framing.WriteRaw((byte)crc);
                framing.WriteRaw(RecordingSchema.CommitByte);
                if (storage.WrittenBytes + framing.WrittenSpan.Length > totalByteBudget)
                {
                    return WriteAnswer.OverBudget;
                }

                return storage.Append(framing.WrittenSpan);
            }
            finally
            {
                framing.Dispose();
            }
        }

        public void Dispose() => storage.Dispose();
    }
}
