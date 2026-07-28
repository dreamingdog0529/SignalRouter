using System;
using System.Linq;
using NUnit.Framework;
using SignalRouter.Codec.Recording;
using SignalRouter.Contracts;

namespace SignalRouter.Codec.Recording.Tests;

/// <summary>
/// The reader semantics ADR 0016 fixes beyond framing: blob-before-reference
/// existence, the single pre-E1 profile matching E1's pinned reference,
/// stop-at-first-invalid, budget violations distinguishable from crash
/// truncation, and the shared-source public-type guard.
/// </summary>
public sealed class ReaderSemanticsTests
{
    private static ArtifactWriter Open(MemoryArtifactStore store, string id)
    {
        var writer = new ArtifactWriter(store.Create(id));
        writer.WriteHeader(id, ArtifactRoundTripTests.Incarnation);
        return writer;
    }

    private static ExternalMutationBarrier Barrier(ulong sequence) => new(
        new EvidenceSequence(sequence),
        new EvidenceSequence(0),
        new EvidenceSequence(0),
        new SourceRevision(1),
        "external-source",
        ValueArray<RequestId>.Empty);

    private static EffectPermit PermitReferencing(ulong sequence, ContentId blob) => new(
        new EvidenceSequence(sequence),
        new RequestId("r-1"),
        new LogicalOrder(1),
        new SourceRevision(1),
        blob,
        reusedCheckpointBlob: false);

    [Test]
    public void ACutReferencingAnAbsentBlobDegrades()
    {
        var store = new MemoryArtifactStore();
        using (var writer = Open(store, "no-blob"))
        {
            writer.AppendCut(PermitReferencing(
                0, new ContentId("sha256", 1, DigestValue.From(new byte[32]))));
        }

        var result = ArtifactReader.Read(
            store.ReadAll("no-blob", ArtifactRoundTripTests.Limits.MaxArtifactBytes),
            ArtifactRoundTripTests.Limits);
        Assert.That(result.IntegrityFailure, Is.True);
        Assert.That(result.IntegrityDetail, Does.Contain("does not carry"));
    }

    [Test]
    public void AnOpenedArtifactWithoutAProfileDegrades()
    {
        var store = new MemoryArtifactStore();
        var (bytes, _, _) = ArtifactRoundTripTests.BuildCompleteArtifact(store, "with-profile");
        var intact = ArtifactReader.Read(bytes, ArtifactRoundTripTests.Limits);
        Assert.That(intact.IntegrityFailure, Is.False);

        // Rebuild the same cuts and blobs without the profile record.
        var bare = new MemoryArtifactStore();
        using (var writer = Open(bare, "no-profile"))
        {
            foreach (var cut in intact.Cuts)
            {
                foreach (var id in ReferencedIds(cut))
                {
                    if (intact.TryGetBlob(id, out var blob))
                    {
                        writer.AppendBlob(id, blob);
                    }
                }

                writer.AppendCut(cut);
            }
        }

        var result = ArtifactReader.Read(
            bare.ReadAll("no-profile", ArtifactRoundTripTests.Limits.MaxArtifactBytes),
            ArtifactRoundTripTests.Limits);
        Assert.That(result.IntegrityFailure, Is.True);
    }

    [Test]
    public void AMismatchedOrDuplicatedOrLateProfileDegrades()
    {
        // Late: E1 before the profile record.
        var store = new MemoryArtifactStore();
        var (bytes, _, _) = ArtifactRoundTripTests.BuildCompleteArtifact(store, "src");
        var intact = ArtifactReader.Read(bytes, ArtifactRoundTripTests.Limits);
        var opened = (RecordingOpened)intact.Cuts[0];

        var late = new MemoryArtifactStore();
        using (var writer = Open(late, "late"))
        {
            intact.TryGetBlob(opened.BaseSnapshot, out var baseBlob);
            writer.AppendBlob(opened.BaseSnapshot, baseBlob);
            writer.AppendCut(opened);
            writer.AppendProfile(intact.Profile!);
        }

        Assert.That(
            ArtifactReader.Read(
                late.ReadAll("late", ArtifactRoundTripTests.Limits.MaxArtifactBytes),
                ArtifactRoundTripTests.Limits).IntegrityFailure,
            Is.True,
            "a profile after E1 violates the pre-E1 rule");

        // Duplicate profile records.
        var duplicated = new MemoryArtifactStore();
        using (var writer = Open(duplicated, "dup"))
        {
            writer.AppendProfile(intact.Profile!);
            writer.AppendProfile(intact.Profile!);
        }

        Assert.That(
            ArtifactReader.Read(
                duplicated.ReadAll("dup", ArtifactRoundTripTests.Limits.MaxArtifactBytes),
                ArtifactRoundTripTests.Limits).IntegrityFailure,
            Is.True);
    }

    [Test]
    public void AnOverBudgetCommittedRecordThrowsNotTruncates()
    {
        var store = new MemoryArtifactStore();
        var (bytes, _, _) = ArtifactRoundTripTests.BuildCompleteArtifact(store, "big");
        var tightRecords = new ArtifactReadLimits(
            maxArtifactBytes: 1024 * 1024,
            maxRecordCount: 1024,
            maxRecordBytes: 8,
            maxBlobBytes: 1024,
            maxStringLength: 4096);

        Assert.That(
            CodeOfThrow(() => ArtifactReader.Read(bytes, tightRecords)),
            Is.EqualTo("OverBudget"));
    }

    [Test]
    public void AnOversizedStringIsABudgetViolation()
    {
        var store = new MemoryArtifactStore();
        var (bytes, _, _) = ArtifactRoundTripTests.BuildCompleteArtifact(store, "strings");
        var tightStrings = new ArtifactReadLimits(
            maxArtifactBytes: 1024 * 1024,
            maxRecordCount: 1024,
            maxRecordBytes: 256 * 1024,
            maxBlobBytes: 128 * 1024,
            maxStringLength: 3);

        // The header's artifact id already exceeds three characters.
        Assert.That(
            CodeOfThrow(() => ArtifactReader.Read(bytes, tightStrings)),
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
    public void AMalformedCommittedRecordTruncatesAndIsExcluded()
    {
        // Hand-frame a record whose payload is a valid cut plus one trailing
        // garbage byte — committed (CRC valid) but invalid. The reader must
        // truncate there, exclude the cut, and degrade.
        var store = new MemoryArtifactStore();
        using (var writer = Open(store, "clean"))
        {
            writer.AppendCut(Barrier(0));
        }

        var clean = store.ReadAll("clean", ArtifactRoundTripTests.Limits.MaxArtifactBytes);
        var headerLength = HeaderLength(clean);
        var record = clean.Skip(headerLength).ToArray();
        var kind = record[0];
        var payloadLength = record[1]; // single-byte varuint in this artifact
        var payload = record.Skip(2).Take(payloadLength).ToArray();

        var extended = payload.Concat(new byte[] { 0x00 }).ToArray();
        var framed = new byte[1 + 1 + extended.Length + 5];
        framed[0] = kind;
        framed[1] = (byte)extended.Length;
        extended.CopyTo(framed, 2);
        var crc = TestCrc(framed.Take(2 + extended.Length).ToArray());
        framed[2 + extended.Length] = (byte)(crc >> 24);
        framed[3 + extended.Length] = (byte)(crc >> 16);
        framed[4 + extended.Length] = (byte)(crc >> 8);
        framed[5 + extended.Length] = (byte)crc;
        framed[6 + extended.Length] = RecordingSchema.CommitByte;

        var hostile = clean.Take(headerLength).Concat(framed).ToArray();
        var result = ArtifactReader.Read(hostile, ArtifactRoundTripTests.Limits);

        Assert.That(result.Cuts.Count, Is.Zero, "an invalid record is never evidence");
        Assert.That(result.TruncatedTail, Is.True, "the first invalid record truncates");
        Assert.That(result.IntegrityFailure, Is.True);
    }

    [Test]
    public void TheSharedSourceNamespaceExposesNoPublicTypes()
    {
        // A public type compiled from shared source would exist twice for a
        // consumer referencing both codec leaves (ADR 0016).
        foreach (var assembly in new[]
        {
            typeof(ArtifactReader).Assembly,
            typeof(SignalRouter.Codec.CanonicalState.CanonicalStateCodec).Assembly,
        })
        {
            var leaked = assembly.GetExportedTypes()
                .Where(type => type.Namespace == "SignalRouter.Codec.Shared")
                .Select(type => type.FullName)
                .ToArray();
            Assert.That(leaked, Is.Empty, assembly.GetName().Name);
        }
    }

    private static System.Collections.Generic.IEnumerable<ContentId> ReferencedIds(EvidenceCut cut)
    {
        switch (cut)
        {
            case RecordingOpened opened:
                yield return opened.BaseSnapshot;
                break;
            case EffectPermit permit:
                yield return permit.BeforeView;
                break;
            case TerminalCut terminal:
                yield return terminal.AfterView;
                break;
            case RecordingClosed closed:
                yield return closed.FinalCheckpoint;
                break;
        }
    }

    private static int HeaderLength(byte[] artifact)
    {
        var position = 4 + 1 + 1; // magic + major + minor (single-byte varuints)
        for (var i = 0; i < 2; i++)
        {
            var length = artifact[position];
            position += 1 + length;
        }

        return position;
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
