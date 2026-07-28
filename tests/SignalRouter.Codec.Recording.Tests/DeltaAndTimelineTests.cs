using System;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using SignalRouter.Codec.Recording;
using SignalRouter.Contracts;

namespace SignalRouter.Codec.Recording.Tests;

/// <summary>
/// The RecordingEventSchema 1.1 additions at the codec boundary: delta blobs
/// (kind 0x05) reconstruct eagerly, verify like any blob, and refuse a lying
/// writer; the timeline lane (kind 0x03) round-trips its known kinds, skips
/// unknown ones, and never bears on cuts or closure.
/// </summary>
public sealed class DeltaAndTimelineTests
{
    private static readonly PredicateContractRef Probe =
        new(new PredicateContractId("probe"), new ContractVersion(1, 0));

    private static ContentId IdOf(byte[] payload)
    {
        using var sha = SHA256.Create();
        return new ContentId("sha256", 1, DigestValue.From(sha.ComputeHash(payload)));
    }

    private static byte[] Payload(int length, byte fill)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = fill;
        }

        return bytes;
    }

    private static ArtifactWriter Open(MemoryArtifactStore store, string id)
    {
        var writer = new ArtifactWriter(store.Create(id));
        writer.WriteHeader(id, ArtifactRoundTripTests.Incarnation);
        return writer;
    }

    private static ArtifactReadResult Read(MemoryArtifactStore store, string id) =>
        ArtifactReader.Read(
            store.ReadAll(id, ArtifactRoundTripTests.Limits.MaxArtifactBytes),
            ArtifactRoundTripTests.Limits);

    [Test]
    public void ASimilarBlobDeltaEncodesAndReconstructsExactly()
    {
        var baseBytes = Payload(300, 0x41);
        var result = Payload(300, 0x41);
        result[150] = 0x42;
        result[151] = 0x43;

        var store = new MemoryArtifactStore();
        using (var writer = Open(store, "delta"))
        {
            Assert.That(writer.AppendBlob(IdOf(baseBytes), baseBytes),
                Is.EqualTo(WriteAnswer.Committed));
            Assert.That(
                writer.AppendBlobOrDelta(
                    IdOf(result), result, IdOf(baseBytes), baseBytes,
                    long.MaxValue, out var wroteDelta),
                Is.EqualTo(WriteAnswer.Committed));
            Assert.That(wroteDelta, Is.True, "a two-byte change must delta-encode");
        }

        var read = Read(store, "delta");
        Assert.That(read.IntegrityFailure, Is.False, read.IntegrityDetail);
        Assert.That(read.DeltaBlobCount, Is.EqualTo(1));
        Assert.That(read.TryGetBlob(IdOf(result), out var reconstructed), Is.True);
        Assert.That(reconstructed, Is.EqualTo(result));
    }

    [Test]
    public void ADissimilarBlobFallsBackToAFullRecord()
    {
        var baseBytes = Payload(300, 0x41);
        var result = Payload(300, 0x42);

        var store = new MemoryArtifactStore();
        using (var writer = Open(store, "full"))
        {
            writer.AppendBlob(IdOf(baseBytes), baseBytes);
            Assert.That(
                writer.AppendBlobOrDelta(
                    IdOf(result), result, IdOf(baseBytes), baseBytes,
                    long.MaxValue, out var wroteDelta),
                Is.EqualTo(WriteAnswer.Committed));
            Assert.That(wroteDelta, Is.False, "a delta larger than the full record is never written");
        }

        var read = Read(store, "full");
        Assert.That(read.DeltaBlobCount, Is.Zero);
        Assert.That(read.TryGetBlob(IdOf(result), out var reconstructed), Is.True);
        Assert.That(reconstructed, Is.EqualTo(result));
    }

    [Test]
    public void ALyingDeltaResultIdIsRefusedNotTrusted()
    {
        var baseBytes = Payload(300, 0x41);
        var result = Payload(300, 0x41);
        result[10] = 0x42;
        var lyingId = IdOf(Payload(300, 0x77));

        var store = new MemoryArtifactStore();
        using (var writer = Open(store, "lying"))
        {
            writer.AppendBlob(IdOf(baseBytes), baseBytes);
            writer.AppendBlobOrDelta(
                lyingId, result, IdOf(baseBytes), baseBytes, long.MaxValue, out _);
        }

        var read = Read(store, "lying");
        Assert.That(read.IntegrityFailure, Is.True, "verify-before-use, never writer trust");
        Assert.That(read.TryGetBlob(lyingId, out _), Is.False);
    }

    [Test]
    public void AChainBeyondTheDepthBoundIsStructural()
    {
        var store = new MemoryArtifactStore();
        var previous = Payload(200, 0x41);
        using (var writer = Open(store, "chain"))
        {
            writer.AppendBlob(IdOf(previous), previous);
            for (var i = 1; i <= RecordingSchema.MaxDeltaChainDepth + 1; i++)
            {
                var next = (byte[])previous.Clone();
                next[i % next.Length] ^= 0xFF;
                Assert.That(
                    writer.AppendBlobOrDelta(
                        IdOf(next), next, IdOf(previous), previous,
                        long.MaxValue, out var wroteDelta),
                    Is.EqualTo(WriteAnswer.Committed));
                Assert.That(wroteDelta, Is.True);
                previous = next;
            }
        }

        var read = Read(store, "chain");
        Assert.That(read.IntegrityFailure, Is.True, "the reader enforces the chain-depth bound");
        Assert.That(read.DeltaBlobCount, Is.EqualTo(RecordingSchema.MaxDeltaChainDepth));
        Assert.That(read.TryGetBlob(IdOf(previous), out _), Is.False,
            "the record beyond the bound never becomes a blob");
    }

    [Test]
    public void TheDeltaBaseMustAlreadyBeInTheArtifact()
    {
        var store = new MemoryArtifactStore();
        using var writer = Open(store, "nobase");
        var baseBytes = Payload(100, 0x41);
        try
        {
            writer.AppendBlobOrDelta(
                IdOf(Payload(100, 0x42)), Payload(100, 0x42),
                IdOf(baseBytes), baseBytes, long.MaxValue, out _);
            Assert.Fail("Expected an InvalidOperationException.");
        }
        catch (InvalidOperationException)
        {
            // Blob-before-reference is byte order (ADR 0016).
        }
    }

    [Test]
    public void TimelineRecordsRoundTripAndNeverBecomeCuts()
    {
        var store = new MemoryArtifactStore();
        using (var writer = Open(store, "timeline"))
        {
            Assert.That(
                writer.AppendTimeline(
                    TimelineRecord.WaitPoll(
                        new OperationId("wait-1"), Probe, new SourceRevision(7)),
                    long.MaxValue),
                Is.EqualTo(WriteAnswer.Committed));
            Assert.That(
                writer.AppendTimeline(TimelineRecord.Gap(3), long.MaxValue),
                Is.EqualTo(WriteAnswer.Committed));
        }

        var read = Read(store, "timeline");
        Assert.That(read.IntegrityFailure, Is.False, read.IntegrityDetail);
        Assert.That(read.Cuts.Count, Is.Zero, "timeline records are never evidence");
        Assert.That(read.Timeline.Count, Is.EqualTo(2));
        Assert.That(read.Timeline[0].Kind, Is.EqualTo(TimelineRecordKinds.WaitPoll));
        Assert.That(read.Timeline[0].Operation, Is.EqualTo(new OperationId("wait-1")));
        Assert.That(read.Timeline[0].Predicate, Is.EqualTo(Probe));
        Assert.That(read.Timeline[0].Revision, Is.EqualTo(new SourceRevision(7)));
        Assert.That(read.Timeline[1].Kind, Is.EqualTo(TimelineRecordKinds.Gap));
        Assert.That(read.Timeline[1].DroppedCount, Is.EqualTo(3));
    }

    [Test]
    public void AnUnknownTimelineKindIsSkippedNotRefused()
    {
        // Hand-frame a timeline record whose kind string this reader does not
        // know, with an arbitrary payload tail: the lane is droppable
        // diagnostics — skipped whole, never a degradation, and records after
        // it still read.
        var store = new MemoryArtifactStore();
        using (var writer = Open(store, "unknown"))
        {
            writer.AppendTimeline(TimelineRecord.Gap(1), long.MaxValue);
        }

        var clean = store.ReadAll(
            "unknown", ArtifactRoundTripTests.Limits.MaxArtifactBytes);
        var kindString = System.Text.Encoding.ASCII.GetBytes("MysteryKind");
        var payload = new byte[1 + kindString.Length + 3];
        payload[0] = (byte)kindString.Length;
        kindString.CopyTo(payload, 1);
        payload[^3] = 0xDE;
        payload[^2] = 0xAD;
        payload[^1] = 0x01;
        var framed = new byte[1 + 1 + payload.Length + 5];
        framed[0] = (byte)RecordKind.Timeline;
        framed[1] = (byte)payload.Length;
        payload.CopyTo(framed, 2);
        var crc = TestCrc(framed.Take(2 + payload.Length).ToArray());
        framed[2 + payload.Length] = (byte)(crc >> 24);
        framed[3 + payload.Length] = (byte)(crc >> 16);
        framed[4 + payload.Length] = (byte)(crc >> 8);
        framed[5 + payload.Length] = (byte)crc;
        framed[6 + payload.Length] = RecordingSchema.CommitByte;

        var extended = clean.Concat(framed).ToArray();
        var read = ArtifactReader.Read(extended, ArtifactRoundTripTests.Limits);
        Assert.That(read.IntegrityFailure, Is.False, read.IntegrityDetail);
        Assert.That(read.TruncatedTail, Is.False);
        Assert.That(read.Timeline.Count, Is.EqualTo(1), "only the known kind decodes");
        Assert.That(read.Timeline[0].Kind, Is.EqualTo(TimelineRecordKinds.Gap));
    }

    [Test]
    public void TheAggregateDecodedBudgetStopsDeltaAmplification()
    {
        // Small delta records each declare a full-size result: without an
        // aggregate decoded budget a bounded file amplifies without bound
        // (codex review). Base 64 + one delta of 64 sit at the 128 budget;
        // the second delta must refuse OverBudget before allocating.
        var limits = new ArtifactReadLimits(
            maxArtifactBytes: 1024 * 1024, maxRecordCount: 128,
            maxRecordBytes: 64 * 1024, maxBlobBytes: 1024, maxStringLength: 1024,
            maxTotalBlobBytes: 128);
        var store = new MemoryArtifactStore();
        var previous = Payload(64, 0x41);
        using (var writer = Open(store, "amplify"))
        {
            writer.AppendBlob(IdOf(previous), previous);
            for (var i = 1; i <= 2; i++)
            {
                var next = (byte[])previous.Clone();
                next[8 * i] ^= 0xFF;
                writer.AppendBlobOrDelta(
                    IdOf(next), next, IdOf(previous), previous, long.MaxValue, out var wroteDelta);
                Assert.That(wroteDelta, Is.True);
                previous = next;
            }
        }

        try
        {
            ArtifactReader.Read(
                store.ReadAll("amplify", limits.MaxArtifactBytes), limits);
            Assert.Fail("Expected a RecordingFormatException.");
        }
        catch (RecordingFormatException exception)
        {
            Assert.That(exception.Code, Is.EqualTo("OverBudget"));
        }
    }

    [Test]
    public void TheDeltaRecordLayoutMatchesTheIndependentDerivation()
    {
        // A hundred 0x41 bytes with byte 50 changed to 0xEE: prefix 50,
        // suffix 49, insert EE. Payload := contentId(result) ‖ contentId(base)
        //   ‖ varuint(100) ‖ varuint(50) ‖ varuint(49) ‖ varuint(1) ‖ EE,
        // framed like every record — derived here independently of the writer
        // (ADR 0016 golden discipline for the 1.1 grammar).
        var baseBytes = Payload(100, 0x41);
        var result = Payload(100, 0x41);
        result[50] = 0xEE;

        var store = new MemoryArtifactStore();
        long headerAndBase;
        using (var writer = Open(store, "layout"))
        {
            writer.AppendBlob(IdOf(baseBytes), baseBytes);
            headerAndBase = writer.WrittenBytes;
            writer.AppendBlobOrDelta(
                IdOf(result), result, IdOf(baseBytes), baseBytes, long.MaxValue, out var wroteDelta);
            Assert.That(wroteDelta, Is.True);
        }

        var bytes = store.ReadAll("layout", ArtifactRoundTripTests.Limits.MaxArtifactBytes);
        var actual = bytes.Skip((int)headerAndBase).ToArray();

        var payload = EncodedContentId(result)
            .Concat(EncodedContentId(baseBytes))
            .Concat(new byte[] { 0x64, 0x32, 0x31, 0x01, 0xEE })
            .ToArray();
        var expected = Frame((byte)RecordKind.DeltaBlob, payload);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void TheGapRecordLayoutMatchesTheIndependentDerivation()
    {
        // Payload := str("TimelineGap") ‖ int64(3) as eight big-endian bytes.
        var store = new MemoryArtifactStore();
        long headerLength;
        using (var writer = Open(store, "gaplayout"))
        {
            headerLength = writer.WrittenBytes;
            writer.AppendTimeline(TimelineRecord.Gap(3), long.MaxValue);
        }

        var bytes = store.ReadAll("gaplayout", ArtifactRoundTripTests.Limits.MaxArtifactBytes);
        var actual = bytes.Skip((int)headerLength).ToArray();

        var kind = System.Text.Encoding.ASCII.GetBytes("TimelineGap");
        var payload = new byte[] { (byte)kind.Length }
            .Concat(kind)
            .Concat(new byte[] { 0, 0, 0, 0, 0, 0, 0, 3 })
            .ToArray();
        Assert.That(actual, Is.EqualTo(Frame((byte)RecordKind.Timeline, payload)));
    }

    private static byte[] EncodedContentId(byte[] payload)
    {
        var digest = SHA256.HashData(payload);
        return new byte[] { 0x06 }
            .Concat(System.Text.Encoding.ASCII.GetBytes("sha256"))
            .Concat(new byte[] { 0x01, 0x20 })
            .Concat(digest)
            .ToArray();
    }

    private static byte[] Frame(byte kind, byte[] payload)
    {
        Assert.That(payload.Length, Is.LessThan(128), "single-byte varuint framing only");
        var framed = new byte[1 + 1 + payload.Length + 5];
        framed[0] = kind;
        framed[1] = (byte)payload.Length;
        payload.CopyTo(framed, 2);
        var crc = TestCrc(framed.Take(2 + payload.Length).ToArray());
        framed[2 + payload.Length] = (byte)(crc >> 24);
        framed[3 + payload.Length] = (byte)(crc >> 16);
        framed[4 + payload.Length] = (byte)(crc >> 8);
        framed[5 + payload.Length] = (byte)crc;
        framed[6 + payload.Length] = RecordingSchema.CommitByte;
        return framed;
    }

    private static uint TestCrc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
