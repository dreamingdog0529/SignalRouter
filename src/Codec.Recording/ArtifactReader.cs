using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using SignalRouter.Codec.Shared;
using SignalRouter.Contracts;

namespace SignalRouter.Codec.Recording
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
            string? integrityDetail,
            ValueArray<TimelineRecord> timeline,
            int deltaBlobCount)
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
            Timeline = timeline;
            DeltaBlobCount = deltaBlobCount;

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

        /// <summary>
        /// The decoded TimelineTrack lane (1.1): droppable diagnostics, excluded
        /// from closure and classification — strict replay never depends on it.
        /// </summary>
        public ValueArray<TimelineRecord> Timeline { get; }

        /// <summary>How many blobs arrived delta-encoded (diagnostics; content is uniform).</summary>
        public int DeltaBlobCount { get; }

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
                var header = new PayloadReader(data, limits.MaxStringLength);
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
            catch (ArgumentException exception)
            {
                // A framed header whose identifiers violate the contract grammar
                // is equally not an artifact (the reader contract: unreadable
                // headers throw the structured exception).
                throw new RecordingFormatException(
                    "InvalidStructure", 0, "The header is invalid: " + exception.Message);
            }

            var cuts = new List<EvidenceCut>();
            var blobs = new Dictionary<ContentId, byte[]>();
            var chainDepths = new Dictionary<ContentId, int>();
            var timeline = new List<TimelineRecord>();
            var deltaBlobCount = 0;
            var totalBlobBytes = 0L;
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
            var sawOpened = false;
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
                            var reader = new PayloadReader(payload, limits.MaxStringLength);
                            var cut = RecordingPayloadCodec.ReadCut(reader);
                            reader.ExpectEnd();

                            // Blob-before-reference is byte order (ADR 0016): a
                            // cut referencing a blob the artifact has not yet
                            // carried — or never carries — is an integrity
                            // failure, not evidence.
                            foreach (var reference in EvidenceSemantics.ReferencedContentIds(cut))
                            {
                                if (!blobs.ContainsKey(reference))
                                {
                                    Degrade("Record " + recordCount
                                        + " references a blob the artifact does not carry before it.");
                                }
                            }

                            if (cut is RecordingOpened openedCut)
                            {
                                sawOpened = true;
                                if (profile == null)
                                {
                                    Degrade("E1 appears before the comparison-profile record.");
                                }
                                else if (!profile.Reference.Equals(openedCut.Profile))
                                {
                                    Degrade("The embedded profile does not match E1's pinned reference.");
                                }
                            }

                            cuts.Add(cut);
                            break;
                        }

                        case (byte)RecordKind.Blob:
                        {
                            var reader = new PayloadReader(payload, limits.MaxStringLength);
                            var id = RecordingPayloadCodec.ReadContentId(reader);
                            var length = reader.ReadCount(1);
                            if (length > limits.MaxBlobBytes)
                            {
                                throw new RecordingFormatException(
                                    "OverBudget", payloadStart, "A blob exceeds the blob budget.");
                            }

                            totalBlobBytes += length;
                            if (totalBlobBytes > limits.MaxTotalBlobBytes)
                            {
                                throw new RecordingFormatException(
                                    "OverBudget", payloadStart,
                                    "The artifact exceeds the aggregate decoded-blob budget.");
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
                                chainDepths[id] = 0;
                            }

                            break;
                        }

                        case (byte)RecordKind.DeltaBlob:
                        {
                            // A delta blob reconstructs eagerly against a base the
                            // artifact already carries (blob-before-reference is
                            // byte order), then verifies exactly like a full blob
                            // — verify-before-use, never writer trust (1.1).
                            var reader = new PayloadReader(payload, limits.MaxStringLength);
                            var id = RecordingPayloadCodec.ReadContentId(reader);
                            var baseId = RecordingPayloadCodec.ReadContentId(reader);
                            // Result/prefix/suffix are reconstruction lengths, not
                            // stream-resident bytes — only the insert run is read
                            // from the payload and count-checked against it.
                            var resultLength = reader.ReadVaruint();
                            if (resultLength > limits.MaxBlobBytes)
                            {
                                throw new RecordingFormatException(
                                    "OverBudget", payloadStart, "A delta blob exceeds the blob budget.");
                            }

                            // Amplification guard: a small delta record may
                            // declare a large result — the aggregate decoded
                            // budget is enforced before the allocation, so a
                            // bounded file cannot decompress without bound.
                            totalBlobBytes += resultLength;
                            if (totalBlobBytes > limits.MaxTotalBlobBytes)
                            {
                                throw new RecordingFormatException(
                                    "OverBudget", payloadStart,
                                    "The artifact exceeds the aggregate decoded-blob budget.");
                            }

                            var prefix = reader.ReadVaruint();
                            var suffix = reader.ReadVaruint();
                            var insert = reader.ReadCount(1);
                            var insertBytes = new byte[insert];
                            for (var i = 0; i < insert; i++)
                            {
                                insertBytes[i] = reader.ReadByte();
                            }

                            reader.ExpectEnd();
                            if (!blobs.TryGetValue(baseId, out var baseBytes))
                            {
                                Degrade("Record " + recordCount
                                    + " delta-references a blob the artifact does not carry before it.");
                                break;
                            }

                            if ((long)prefix + insert + suffix != resultLength ||
                                (long)prefix + suffix > baseBytes.Length)
                            {
                                Degrade("Record " + recordCount + " carries an inconsistent delta.");
                                break;
                            }

                            var depth = chainDepths[baseId] + 1;
                            if (depth > RecordingSchema.MaxDeltaChainDepth)
                            {
                                Degrade("Record " + recordCount
                                    + " exceeds the delta chain depth bound.");
                                break;
                            }

                            var bytes = new byte[resultLength];
                            Array.Copy(baseBytes, 0, bytes, 0, prefix);
                            Array.Copy(insertBytes, 0, bytes, prefix, insert);
                            Array.Copy(
                                baseBytes, baseBytes.Length - suffix,
                                bytes, prefix + insert, suffix);
                            if (!VerifyBlob(id, bytes))
                            {
                                Degrade("A delta blob does not verify against its ContentId.");
                            }
                            else
                            {
                                blobs[id] = bytes;
                                chainDepths[id] = depth;
                                deltaBlobCount++;
                            }

                            break;
                        }

                        case (byte)RecordKind.ComparisonProfile:
                        {
                            if (profile != null)
                            {
                                Degrade("The artifact carries more than one comparison-profile record.");
                                break;
                            }

                            var reader = new PayloadReader(payload, limits.MaxStringLength);
                            profile = RecordingPayloadCodec.ReadProfile(reader);
                            reader.ExpectEnd();
                            break;
                        }

                        case (byte)RecordKind.Timeline:
                        {
                            // Droppable diagnostics: decoded when the kind is
                            // known, skipped when not, excluded from closure and
                            // classification either way (ADR 0016).
                            var reader = new PayloadReader(payload, limits.MaxStringLength);
                            var entry = RecordingPayloadCodec.ReadTimeline(reader);
                            if (entry != null)
                            {
                                reader.ExpectEnd();
                                timeline.Add(entry);
                            }

                            break;
                        }

                        default:
                            // A record kind from a newer minor: additive, skipped.
                            break;
                    }
                }
                catch (CodecFormatException exception)
                {
                    // A committed-but-malformed record truncates the artifact
                    // exactly like a torn one, and additionally degrades it —
                    // "the first torn or invalid record truncates" (ADR 0016).
                    Degrade("Record " + recordCount + " is malformed: "
                        + exception.Code + " — " + exception.Message);
                    truncated = true;
                    break;
                }
                catch (ArgumentException exception)
                {
                    Degrade("Record " + recordCount + " violates a cut invariant: " + exception.Message);
                    truncated = true;
                    break;
                }
            }

            if (sawOpened && profile == null)
            {
                Degrade("An opened artifact carries no comparison-profile record.");
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
                truncated, integrityFailure, integrityDetail,
                ValueArray<TimelineRecord>.From(timeline.ToArray()), deltaBlobCount);
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

            if (bytesRead > 1 && (group & 0x7F) == 0)
            {
                // Non-minimal framing in the tail reads as torn.
                position = start;
                return false;
            }

            if (value > limits.MaxRecordBytes)
            {
                // A fully committed over-budget record is a budget violation the
                // caller must see as such — a resource refusal must never be
                // indistinguishable from crash truncation. Anything less than a
                // committed record is torn.
                var overEnd = (long)cursor + value + 5;
                if (overEnd <= data.LongLength &&
                    IsCommitted(data, start, cursor, (long)value))
                {
                    throw new RecordingFormatException(
                        "OverBudget", start, "A committed record exceeds the record budget.");
                }

                position = start;
                return false;
            }

            payloadLength = (int)value;
            payloadStart = cursor;
            var afterPayload = (long)cursor + payloadLength;
            if (afterPayload + 5 > data.LongLength)
            {
                position = start;
                return false;
            }

            var crcOffset = (int)afterPayload;
            var expectedCrc =
                ((uint)data[crcOffset] << 24) |
                ((uint)data[crcOffset + 1] << 16) |
                ((uint)data[crcOffset + 2] << 8) |
                data[crcOffset + 3];
            var actualCrc = Crc32C.Compute(new ReadOnlySpan<byte>(data, start, crcOffset - start));
            if (expectedCrc != actualCrc || data[crcOffset + 4] != RecordingSchema.CommitByte)
            {
                position = start;
                return false;
            }

            position = crcOffset + 5;
            return true;
        }

        private static bool IsCommitted(byte[] data, int recordStart, int payloadStart, long payloadLength)
        {
            var crcOffset = payloadStart + payloadLength;
            var expectedCrc =
                ((uint)data[crcOffset] << 24) |
                ((uint)data[crcOffset + 1] << 16) |
                ((uint)data[crcOffset + 2] << 8) |
                data[crcOffset + 3];
            var actualCrc = Crc32C.Compute(
                new ReadOnlySpan<byte>(data, recordStart, (int)(crcOffset - recordStart)));
            return expectedCrc == actualCrc && data[crcOffset + 4] == RecordingSchema.CommitByte;
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
