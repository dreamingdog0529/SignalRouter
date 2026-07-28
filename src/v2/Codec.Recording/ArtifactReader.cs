using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using SignalRouter.V2.Codec.Shared;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Codec.Recording
{
    /// <summary>The reader's verified view of one artifact (ADR 0016).</summary>
    public sealed class ArtifactReadResult
    {
        private readonly Dictionary<ContentId, byte[]> blobs;

        internal ArtifactReadResult(
            int majorVersion,
            int minorVersion,
            string artifactId,
            RuntimeIncarnationId incarnation,
            ReplayComparisonProfile? profile,
            ValueArray<EvidenceCut> cuts,
            Dictionary<ContentId, byte[]> blobs,
            bool truncatedTail,
            bool integrityFailure,
            string? integrityDetail)
        {
            MajorVersion = majorVersion;
            MinorVersion = minorVersion;
            ArtifactId = artifactId;
            Incarnation = incarnation;
            Profile = profile;
            Cuts = cuts;
            this.blobs = blobs;
            TruncatedTail = truncatedTail;
            IntegrityFailure = integrityFailure;
            IntegrityDetail = integrityDetail;

            var baseSnapshotDurable = false;
            for (var i = 0; i < cuts.Count; i++)
            {
                if (cuts[i] is RecordingOpened opened)
                {
                    baseSnapshotDurable = blobs.ContainsKey(opened.BaseSnapshot);
                    break;
                }
            }

            Facts = new ArtifactFacts(baseSnapshotDurable, cuts, integrityFailure);
        }

        public int MajorVersion { get; }

        public int MinorVersion { get; }

        public string ArtifactId { get; }

        public RuntimeIncarnationId Incarnation { get; }

        /// <summary>The embedded comparison-profile document, when present and digest-valid.</summary>
        public ReplayComparisonProfile? Profile { get; }

        public ValueArray<EvidenceCut> Cuts { get; }

        /// <summary>True when the file ended in a torn (uncommitted) record — everything before it is the artifact.</summary>
        public bool TruncatedTail { get; }

        /// <summary>True on digest or structural verification failure — the classification input, never a guess.</summary>
        public bool IntegrityFailure { get; }

        public string? IntegrityDetail { get; }

        /// <summary>The reader-authoritative facts for <c>EvidenceSemantics</c>.</summary>
        public ArtifactFacts Facts { get; }

        public bool TryGetBlob(ContentId id, out byte[] canonicalPayload)
        {
            if (blobs.TryGetValue(id, out var stored))
            {
                canonicalPayload = new byte[stored.Length];
                Array.Copy(stored, canonicalPayload, stored.Length);
                return true;
            }

            canonicalPayload = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>
    /// The bounded RecordingEventSchema reader (ADR 0016): enforces the caller's
    /// read limits before allocating from any untrusted length, truncates at the
    /// first torn record, verifies every blob digest and the evidence-sequence
    /// contiguity itself, and hands classification to <c>EvidenceSemantics</c>
    /// through <see cref="ArtifactReadResult.Facts"/>. An unsupported major
    /// version or unreadable header throws <see cref="RecordingFormatException"/>
    /// — such an input is not an artifact.
    /// </summary>
    public static class ArtifactReader
    {
        public static ArtifactReadResult Read(byte[] data, ArtifactReadLimits limits)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            if (data.LongLength > limits.MaxArtifactBytes)
            {
                throw new RecordingFormatException(
                    "OverBudget", -1, "The artifact exceeds the caller's read budget.");
            }

            var position = 0;
            string artifactId;
            RuntimeIncarnationId incarnation;
            int major;
            int minor;
            try
            {
                var header = new PayloadReader(data);
                if (header.ReadByte() != 0x53 || header.ReadByte() != 0x52 ||
                    header.ReadByte() != 0x52 || header.ReadByte() != 0x45)
                {
                    throw new RecordingFormatException(
                        "BadMagic", 0, "The file does not start with the SRRE magic.");
                }

                major = header.ReadVaruint();
                minor = header.ReadVaruint();
                if (major != RecordingSchema.MajorVersion)
                {
                    throw new RecordingFormatException(
                        "UnsupportedVersion", 4,
                        "Unsupported RecordingEventSchema major version " + major + ".");
                }

                artifactId = header.ReadString();
                incarnation = new RuntimeIncarnationId(header.ReadString());
                position = header.Position;
            }
            catch (CodecFormatException exception)
            {
                throw new RecordingFormatException(
                    exception.Code, exception.Position, exception.Message);
            }

            var cuts = new List<EvidenceCut>();
            var blobs = new Dictionary<ContentId, byte[]>();
            ReplayComparisonProfile? profile = null;
            var truncated = false;
            var integrityFailure = false;
            string? integrityDetail = null;

            void Degrade(string detail)
            {
                if (!integrityFailure)
                {
                    integrityFailure = true;
                    integrityDetail = detail;
                }
            }

            var recordCount = 0;
            while (position < data.Length)
            {
                if (++recordCount > limits.MaxRecordCount)
                {
                    throw new RecordingFormatException(
                        "OverBudget", position, "The artifact exceeds the record-count budget.");
                }

                if (!TryReadRecord(
                    data, ref position, limits, out var kind, out var payloadStart, out var payloadLength))
                {
                    truncated = true;
                    break;
                }

                var payload = new byte[payloadLength];
                Array.Copy(data, payloadStart, payload, 0, payloadLength);
                try
                {
                    switch (kind)
                    {
                        case (byte)RecordKind.EvidenceCut:
                        {
                            var reader = new PayloadReader(payload);
                            cuts.Add(RecordingPayloadCodec.ReadCut(reader));
                            reader.ExpectEnd();
                            break;
                        }

                        case (byte)RecordKind.Blob:
                        {
                            var reader = new PayloadReader(payload);
                            var id = RecordingPayloadCodec.ReadContentId(reader);
                            var length = reader.ReadCount(1);
                            if (length > limits.MaxBlobBytes)
                            {
                                throw new RecordingFormatException(
                                    "OverBudget", payloadStart, "A blob exceeds the blob budget.");
                            }

                            var bytes = new byte[length];
                            for (var i = 0; i < length; i++)
                            {
                                bytes[i] = reader.ReadByte();
                            }

                            reader.ExpectEnd();
                            if (!VerifyBlob(id, bytes))
                            {
                                Degrade("A blob does not verify against its ContentId.");
                            }
                            else
                            {
                                blobs[id] = bytes;
                            }

                            break;
                        }

                        case (byte)RecordKind.ComparisonProfile:
                        {
                            var reader = new PayloadReader(payload);
                            profile = RecordingPayloadCodec.ReadProfile(reader);
                            reader.ExpectEnd();
                            break;
                        }

                        case (byte)RecordKind.Timeline:
                            // Reserved lane: accepted, excluded from closure (ADR 0016).
                            break;

                        default:
                            // A record kind from a newer minor: additive, skipped.
                            break;
                    }
                }
                catch (CodecFormatException exception)
                {
                    Degrade("Record " + recordCount + " is malformed: "
                        + exception.Code + " — " + exception.Message);
                }
                catch (ArgumentException exception)
                {
                    // A cut that parses but violates a Contracts invariant is not
                    // evidence; the artifact degrades, never the reader.
                    Degrade("Record " + recordCount + " violates a cut invariant: " + exception.Message);
                }
            }

            // Evidence sequences are the append positions: strictly monotonic,
            // contiguous from zero (ADR 0015/0016). A gap is a structural failure.
            for (var i = 0; i < cuts.Count; i++)
            {
                if (cuts[i].Sequence.Value != (ulong)i)
                {
                    Degrade("Evidence sequences are not contiguous at position " + i + ".");
                    break;
                }
            }

            return new ArtifactReadResult(
                major, minor, artifactId, incarnation, profile,
                ValueArray<EvidenceCut>.From(cuts.ToArray()), blobs,
                truncated, integrityFailure, integrityDetail);
        }

        private static bool TryReadRecord(
            byte[] data,
            ref int position,
            ArtifactReadLimits limits,
            out byte kind,
            out int payloadStart,
            out int payloadLength)
        {
            kind = 0;
            payloadStart = 0;
            payloadLength = 0;
            var start = position;
            if (data.Length - position < 1)
            {
                return false;
            }

            kind = data[position];
            var cursor = position + 1;

            // Bounded varint read of the payload length.
            var value = 0L;
            var shift = 0;
            byte group;
            var bytesRead = 0;
            do
            {
                if (cursor >= data.Length || bytesRead == 5)
                {
                    position = start;
                    return false;
                }

                group = data[cursor++];
                value |= (long)(group & 0x7F) << shift;
                shift += 7;
                bytesRead++;
            }
            while ((group & 0x80) != 0);

            if ((bytesRead > 1 && (group & 0x7F) == 0) || value > limits.MaxRecordBytes)
            {
                // A non-minimal or over-budget length in the tail reads as torn;
                // over-budget in a committed record throws below once verified.
                position = start;
                return false;
            }

            payloadLength = (int)value;
            payloadStart = cursor;
            var afterPayload = cursor + payloadLength;
            if (afterPayload + 5 > data.Length)
            {
                position = start;
                return false;
            }

            var expectedCrc =
                ((uint)data[afterPayload] << 24) |
                ((uint)data[afterPayload + 1] << 16) |
                ((uint)data[afterPayload + 2] << 8) |
                data[afterPayload + 3];
            var actualCrc = Crc32C.Compute(new ReadOnlySpan<byte>(data, start, afterPayload - start));
            if (expectedCrc != actualCrc || data[afterPayload + 4] != RecordingSchema.CommitByte)
            {
                position = start;
                return false;
            }

            position = afterPayload + 5;
            return true;
        }

        private static bool VerifyBlob(ContentId id, byte[] payload)
        {
            if (!string.Equals(id.DigestAlgorithmId, "sha256", StringComparison.Ordinal))
            {
                // An algorithm this reader cannot verify is an unverifiable blob,
                // never a trusted one.
                return false;
            }

            using (var sha = SHA256.Create())
            {
                return DigestValue.From(sha.ComputeHash(payload)).Equals(id.Digest);
            }
        }
    }
}
