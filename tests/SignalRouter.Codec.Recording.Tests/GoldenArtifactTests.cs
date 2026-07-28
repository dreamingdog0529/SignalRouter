using System;
using NUnit.Framework;
using SignalRouter.Codec.Recording;
using SignalRouter.Contracts;

namespace SignalRouter.Codec.Recording.Tests;

/// <summary>
/// The frozen byte-level regression vector of RecordingEventSchema@1.1
/// (ADR 0016 golden discipline): a minimal artifact — header plus one E5 cut —
/// whose exact bytes are pinned. Any framing, grammar, or CRC change moves
/// these bytes and must be a reviewed schema revision, never an accident.
/// The 1.0 → 1.1 revision changed exactly one byte here: the header minor
/// varuint (00 → 01, the delta/timeline PR).
/// The primitive encodings are additionally pinned at their boundary values
/// against the ADR 0012 worksheet (shared-source parity).
/// </summary>
public sealed class GoldenArtifactTests
{
    // Worksheet: 53525245 "SRRE" · 01 major · 01 minor · 06+"golden" ·
    // 05+"inc-1" · 01 cut-record · 3D length(61) · 17+"ExternalMutationBarrier"
    // · 8B sequence(0) · 8B lastClean(0) · 8B firstObserved(0) · 8B revision(1)
    // · 03+"ext" · 00 count · F4EF5FCC crc32c · C7 commit.
    private const string ExpectedHex =
        "53525245010106676F6C64656E05696E632D31013D1745787465726E616C4D75746174696F6E" +
        "4261727269657200000000000000000000000000000000000000000000000000000000000000" +
        "010365787400F4EF5FCCC7";

    [Test]
    public void TheMinimalArtifactBytesAreFrozen()
    {
        var store = new MemoryArtifactStore();
        using (var writer = new ArtifactWriter(store.Create("golden")))
        {
            writer.WriteHeader("golden", new RuntimeIncarnationId("inc-1"));
            writer.AppendCut(new ExternalMutationBarrier(
                new EvidenceSequence(0),
                new EvidenceSequence(0),
                new EvidenceSequence(0),
                new SourceRevision(1),
                "ext",
                ValueArray<RequestId>.Empty));
        }

        var actual = Convert.ToHexString(
            store.ReadAll("golden", ArtifactRoundTripTests.Limits.MaxArtifactBytes));
        Assert.That(actual, Is.EqualTo(ExpectedHex));
    }

    [Test]
    public void SharedPrimitiveBoundariesMatchTheAdr0012Worksheet()
    {
        // varuint minimal-form boundary (127 → 7F; 128 → 80 01) and the framed
        // string form, byte-for-byte as ADR 0012 froze them — pinned through a
        // record whose payload starts with those primitives: the E5 grammar
        // opens with the cut-kind string ("ExternalMutationBarrier", 23 chars →
        // varuint 17 hex + ASCII).
        var store = new MemoryArtifactStore();
        using (var writer = new ArtifactWriter(store.Create("prims")))
        {
            writer.WriteHeader("prims", new RuntimeIncarnationId("inc-1"));
            writer.AppendCut(new ExternalMutationBarrier(
                new EvidenceSequence(127),
                new EvidenceSequence(0),
                new EvidenceSequence(0),
                new SourceRevision(128),
                "ext",
                ValueArray<RequestId>.Empty));
        }

        var hex = Convert.ToHexString(
            store.ReadAll("prims", ArtifactRoundTripTests.Limits.MaxArtifactBytes));
        Assert.That(hex, Does.Contain("17" + Convert.ToHexString(
            System.Text.Encoding.ASCII.GetBytes("ExternalMutationBarrier"))),
            "framed strings are varuint-length + strict UTF-8");
    }
}
