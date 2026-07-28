using System;
using System.Collections.Generic;
using SignalRouter.V2.Codec.Shared;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Codec.Recording
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
        public WriteAnswer AppendBlob(ContentId id, ReadOnlySpan<byte> canonicalPayload)
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

                var answer = AppendRecord(RecordKind.Blob, writer.WrittenSpan);
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

        public WriteAnswer AppendCut(EvidenceCut cut)
        {
            if (cut == null)
            {
                throw new ArgumentNullException(nameof(cut));
            }

            var writer = PayloadWriter.Rent();
            try
            {
                RecordingPayloadCodec.WriteCut(ref writer, cut);
                return AppendRecord(RecordKind.EvidenceCut, writer.WrittenSpan);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private WriteAnswer AppendRecord(RecordKind kind, ReadOnlySpan<byte> payload)
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
