using System;
using NUnit.Framework;
using SignalRouter.Codec.Recording;
using SignalRouter.Contracts;

namespace SignalRouter.Codec.Recording.Tests;

/// <summary>
/// The crash and hostile-input matrix (ADR 0016): a torn tail truncates —
/// detectably, never as an exception, at every byte boundary of the final
/// record; corruption is caught by the CRC; digest and contiguity failures
/// degrade to integrity failure; non-artifacts and over-budget inputs throw.
/// </summary>
public sealed class ArtifactCrashAndIntegrityTests
{
    [Test]
    public void TheWriterEmitsStandardCrc32C()
    {
        // The independent bitwise implementation is itself pinned to the
        // standard check vector ("123456789" → 0xE3069283)...
        Assert.That(
            IndependentCrc32C(System.Text.Encoding.ASCII.GetBytes("123456789")),
            Is.EqualTo(0xE3069283u));

        // ...and the writer's emitted CRC field must match it over
        // kind ‖ length ‖ payload of a real record.
        var store = new MemoryArtifactStore();
        using (var writer = new ArtifactWriter(store.Create("crc")))
        {
            writer.WriteHeader("crc", ArtifactRoundTripTests.Incarnation);
            writer.AppendCut(new ExternalMutationBarrier(
                new EvidenceSequence(0),
                new EvidenceSequence(0),
                new EvidenceSequence(0),
                new SourceRevision(1),
                "external-source",
                ValueArray<RequestId>.Empty));
        }

        var bytes = store.ReadAll("crc", ArtifactRoundTripTests.Limits.MaxArtifactBytes);
        // The record region: everything after the header; the record ends with
        // 4 CRC bytes + the commit byte.
        var recordStart = FindRecordStart(bytes);
        var crcOffset = bytes.Length - 5;
        var emitted =
            ((uint)bytes[crcOffset] << 24) |
            ((uint)bytes[crcOffset + 1] << 16) |
            ((uint)bytes[crcOffset + 2] << 8) |
            bytes[crcOffset + 3];
        var covered = new byte[crcOffset - recordStart];
        Array.Copy(bytes, recordStart, covered, 0, covered.Length);
        Assert.That(emitted, Is.EqualTo(IndependentCrc32C(covered)));
        Assert.That(bytes[^1], Is.EqualTo(RecordingSchema.CommitByte));
    }

    private static int FindRecordStart(byte[] artifact)
    {
        // Header: magic(4) + varuint major + varuint minor + framed strings; the
        // first record begins with the EvidenceCut kind byte (0x01) — locate it
        // by parsing the two framed strings after the versions.
        var position = 4;
        position++; // major (single-byte varuint in these tests)
        position++; // minor
        for (var i = 0; i < 2; i++)
        {
            var length = artifact[position]; // ids under 128 in these tests
            position += 1 + length;
        }

        return position;
    }

    private static uint IndependentCrc32C(byte[] data)
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

    [Test]
    public void ATornTailTruncatesAtEveryByteBoundary()
    {
        var store = new MemoryArtifactStore();
        var (bytes, _, _) = ArtifactRoundTripTests.BuildCompleteArtifact(store);
        var intact = ArtifactReader.Read(bytes, ArtifactRoundTripTests.Limits);
        Assert.That(intact.Cuts.Count, Is.EqualTo(5));

        // Find where the final record begins: reading progressively longer
        // prefixes, the cut count reaches 5 only at full length.
        for (var length = bytes.Length - 1; length > 0; length--)
        {
            var truncatedBytes = new byte[length];
            Array.Copy(bytes, truncatedBytes, length);
            ArtifactReadResult result;
            try
            {
                result = ArtifactReader.Read(truncatedBytes, ArtifactRoundTripTests.Limits);
            }
            catch (RecordingFormatException)
            {
                // Only a truncation inside the header itself may throw — such an
                // input is not an artifact at all.
                Assert.That(length, Is.LessThan(32), "record-region truncation must not throw");
                continue;
            }

            // A truncation landing exactly on a record boundary is invisible to
            // the framing by design — the classification catches it instead:
            // the artifact is either detectably torn or detectably not Completed.
            Assert.That(result.Cuts.Count, Is.LessThanOrEqualTo(5));
            if (!result.TruncatedTail)
            {
                Assert.That(
                    EvidenceSemantics.ClassifyArtifact(result.Facts).Outcome.Kind,
                    Is.Not.EqualTo(RecordingOutcomeKind.Completed),
                    $"length {length} lost bytes silently");
            }
        }
    }

    [Test]
    public void ACorruptedByteReadsAsTornNeverAsEvidence()
    {
        var store = new MemoryArtifactStore();
        var (bytes, _, _) = ArtifactRoundTripTests.BuildCompleteArtifact(store);

        // Flip one byte inside the final record's payload region.
        var corrupted = new byte[bytes.Length];
        Array.Copy(bytes, corrupted, bytes.Length);
        corrupted[bytes.Length - 10] ^= 0xFF;

        var result = ArtifactReader.Read(corrupted, ArtifactRoundTripTests.Limits);
        Assert.That(result.TruncatedTail, Is.True, "a CRC mismatch reads as a torn record");
        Assert.That(result.Cuts.Count, Is.LessThan(5));
    }

    [Test]
    public void ABlobDigestMismatchDegradesToIntegrityFailure()
    {
        var store = new MemoryArtifactStore();
        using (var writer = new ArtifactWriter(store.Create("bad-blob")))
        {
            writer.WriteHeader("bad-blob", ArtifactRoundTripTests.Incarnation);
            // A ContentId whose digest does not match the payload.
            var lyingId = new ContentId(
                "sha256", 1, DigestValue.From(new byte[32]));
            writer.AppendBlob(lyingId, new byte[] { 1, 2, 3 });
        }

        var result = ArtifactReader.Read(
            store.ReadAll("bad-blob", ArtifactRoundTripTests.Limits.MaxArtifactBytes),
            ArtifactRoundTripTests.Limits);
        Assert.That(result.IntegrityFailure, Is.True);
        Assert.That(
            EvidenceSemantics.ClassifyArtifact(result.Facts).Outcome.Kind, Is.Not.EqualTo(RecordingOutcomeKind.Completed));
    }

    [Test]
    public void AnEvidenceSequenceGapDegradesToIntegrityFailure()
    {
        var store = new MemoryArtifactStore();
        using (var writer = new ArtifactWriter(store.Create("gap")))
        {
            writer.WriteHeader("gap", ArtifactRoundTripTests.Incarnation);
            writer.AppendCut(new ExternalMutationBarrier(
                new EvidenceSequence(0),
                new EvidenceSequence(0),
                new EvidenceSequence(0),
                new SourceRevision(1),
                "external-source",
                ValueArray<RequestId>.Empty));
            writer.AppendCut(new ExternalMutationBarrier(
                new EvidenceSequence(2), // gap: 1 is missing
                new EvidenceSequence(0),
                new EvidenceSequence(0),
                new SourceRevision(2),
                "external-source",
                ValueArray<RequestId>.Empty));
        }

        var result = ArtifactReader.Read(
            store.ReadAll("gap", ArtifactRoundTripTests.Limits.MaxArtifactBytes),
            ArtifactRoundTripTests.Limits);
        Assert.That(result.IntegrityFailure, Is.True);
    }

    [Test]
    public void NonArtifactsAndBudgetViolationsThrow()
    {
        var store = new MemoryArtifactStore();
        var (bytes, _, _) = ArtifactRoundTripTests.BuildCompleteArtifact(store);

        var badMagic = new byte[bytes.Length];
        Array.Copy(bytes, badMagic, bytes.Length);
        badMagic[0] = 0x00;
        Assert.That(
            CodeOfThrow(() => ArtifactReader.Read(badMagic, ArtifactRoundTripTests.Limits)),
            Is.EqualTo("BadMagic"));

        var badMajor = new byte[bytes.Length];
        Array.Copy(bytes, badMajor, bytes.Length);
        badMajor[4] = 0x63; // varuint major = 99
        Assert.That(
            CodeOfThrow(() => ArtifactReader.Read(badMajor, ArtifactRoundTripTests.Limits)),
            Is.EqualTo("UnsupportedVersion"));

        var tinyBudget = new ArtifactReadLimits(
            maxArtifactBytes: 8, maxRecordCount: 1024, maxRecordBytes: 1024,
            maxBlobBytes: 1024, maxStringLength: 1024);
        Assert.That(
            CodeOfThrow(() => ArtifactReader.Read(bytes, tinyBudget)),
            Is.EqualTo("OverBudget"));
    }

    private static string CodeOfThrow(Action action)
    {
        try
        {
            action();
        }
        catch (RecordingFormatException exception)
        {
            return exception.Code;
        }

        Assert.Fail("Expected a RecordingFormatException.");
        return string.Empty;
    }

    [Test]
    public void ScriptedFaultsSurfaceAsWriteAnswers()
    {
        var store = new MemoryArtifactStore();
        var storage = store.Create("faulty");
        using var writer = new ArtifactWriter(storage);
        writer.WriteHeader("faulty", ArtifactRoundTripTests.Incarnation);
        store.ScriptedAnswers.Enqueue(WriteAnswer.Fault);

        var answer = writer.AppendCut(new ExternalMutationBarrier(
            new EvidenceSequence(0),
            new EvidenceSequence(0),
            new EvidenceSequence(0),
            new SourceRevision(1),
            "external-source",
            ValueArray<RequestId>.Empty));
        Assert.That(answer, Is.EqualTo(WriteAnswer.Fault));
        Assert.That(storage.IsDurable, Is.False, "the memory store is test-only by contract");
    }
}
