using System;
using NUnit.Framework;
using SignalRouter.Contracts;

namespace SignalRouter.Codec.CanonicalState.Tests;

/// <summary>
/// The extended golden corpus (performance-track plan P0a): vectors 3–5 of
/// GoldenVectors.md, hand-derived from the ADR 0012 grammar exactly like
/// vectors 1–2 and pinned with externally computed SHA-256 digests. Coverage the
/// first two vectors lacked: multi-node/multi-source sorting under reversed
/// registration order, the UTF-16-ordinal versus UTF-8-byte-order divergence for
/// non-BMP identifiers, and the LEB128 length boundary at 127/128. Together with
/// the originals these freeze the representation for the P3–P5 rework: any bit
/// of drift in the canonical bytes or ContentId fails here.
/// </summary>
public sealed class GoldenCorpusTests
{
    private static readonly CanonicalStateCodec Codec = new();

    // ── Vector 3 — multi-node / multi-source, reverse registration order ──

    private const string Vector3Hex =
        "535243530101760100016404726f6f7402016101720000000001620172010161010178000301" +
        "0103436170020300010202733102733101000001016d0500027332027332010001055374616c" +
        "650001017a0002076e6f6465732f620b5669727475616c697a65640a736f75726365732f7332" +
        "055374616c65";

    private const string Vector3Sha256 =
        "c6e70db8360244147ea525b6934077b0e67a0437ba3e6a1b436a23ca19fa4216";

    /// <summary>The vector-3 world with every input list deliberately in reverse canonical order.</summary>
    private static ObservationMaterialization Vector3World()
    {
        var nodeB = new MaterializedNode(
            new AuthorKey("b"),
            new NodeRole("r"),
            new AuthorKey("a"),
            ValueArray<MaterializedAttribute>.From(new[]
            {
                new MaterializedAttribute("x", FieldValue.Of(true), redacted: false),
            }),
            ValueArray<MaterializedCapability>.From(new[]
            {
                new MaterializedCapability(
                    new CapabilityContractRef(new CapabilityContractId("Cap"), new ContractVersion(2, 3)),
                    available: false),
            }),
            visibleChildCount: 1);
        var nodeA = new MaterializedNode(
            new AuthorKey("a"), new NodeRole("r"), null,
            ValueArray<MaterializedAttribute>.Empty,
            ValueArray<MaterializedCapability>.Empty,
            visibleChildCount: 0);
        var sourceS2 = new MaterializedSource(
            new StateSourceKey("s2"),
            new StateSourceContractRef(new StateSourceContractId("s2"), new ContractVersion(1, 0)),
            ValueArray<NamedField>.Empty,
            ValueArray<string>.From(new[] { "z" }),
            omission: CompletenessReason.Stale);
        var sourceS1 = new MaterializedSource(
            new StateSourceKey("s1"),
            new StateSourceContractRef(new StateSourceContractId("s1"), new ContractVersion(1, 0)),
            ValueArray<NamedField>.From(new[] { new NamedField("m", FieldValue.Null) }),
            ValueArray<string>.Empty,
            omission: null);
        return new ObservationMaterialization(
            CodecFixtures.Basis(),
            ValueArray<MaterializedNode>.From(new[] { nodeB, nodeA }),
            ValueArray<MaterializedSource>.From(new[] { sourceS2, sourceS1 }),
            CompletenessMap.From(
                new[]
                {
                    new CompletenessEntry(new FieldPath("sources/s2"), CompletenessReason.Stale),
                    new CompletenessEntry(new FieldPath("nodes/b"), CompletenessReason.Virtualized),
                },
                maxEntries: 4));
    }

    [Test]
    public void TheMultiEntityVectorEncodesByteExactlyDespiteReversedInputOrder()
    {
        var result = Codec.Encode(Vector3World());
        Assert.That(CodecFixtures.ToHex(result.CopyPayload()), Is.EqualTo(Vector3Hex));
        Assert.That(CodecFixtures.ToHex(result.Id.Digest.ToArray()), Is.EqualTo(Vector3Sha256));
    }

    [Test]
    public void TheMultiEntityVectorDecodesAndSurvivesCanonicalReencodeEnforcement()
    {
        // Decode enforces canonical form by re-encoding internally, so a
        // successful decode already proves Encode(Decode(b)) == b.
        var decoded = Codec.Decode(
            CodecFixtures.FromHex(Vector3Hex), CodecFixtures.Incarnation, CodecFixtures.Revision);

        Assert.That(decoded.Nodes.Count, Is.EqualTo(2));
        Assert.That(decoded.Nodes[0].Key.Value, Is.EqualTo("a"));
        Assert.That(decoded.Nodes[1].Key.Value, Is.EqualTo("b"));
        Assert.That(decoded.Nodes[1].Parent!.Value.Value, Is.EqualTo("a"));
        Assert.That(decoded.Sources.Count, Is.EqualTo(2));
        Assert.That(decoded.Sources[1].Omission, Is.EqualTo(CompletenessReason.Stale));
        Assert.That(decoded.Sources[1].RedactedFieldNames[0], Is.EqualTo("z"));
        Assert.That(decoded.Completeness.Entries.Count, Is.EqualTo(2));
    }

    // ── Vector 4 — UTF-16 ordinal order vs UTF-8 byte order ──

    private const string Vector4Hex =
        "535243530101760100016404726f6f740204f090808001720000000003efbca1017200000000" +
        "000000";

    private const string Vector4Sha256 =
        "821c9b7fb64aba35f87b61e84852ea7e1ed55d4913071d1f85b54fc94031171d";

    [Test]
    public void CanonicalOrderFollowsUtf16CodeUnitsNotUtf8Bytes()
    {
        // U+10000 (UTF-16 D800 DC00, UTF-8 F0 90 80 80) sorts BEFORE U+FF21
        // (UTF-16 FF21, UTF-8 EF BC A1) under string.CompareOrdinal, while raw
        // UTF-8 byte order would reverse them (EF < F0). ADR 0012 orders by
        // CompareOrdinal — an implementation that sorts encoded bytes drifts here.
        var nodeFullwidthA = new MaterializedNode(
            new AuthorKey("Ａ"), new NodeRole("r"), null,
            ValueArray<MaterializedAttribute>.Empty, ValueArray<MaterializedCapability>.Empty, 0);
        var nodeLinearB = new MaterializedNode(
            new AuthorKey("\U00010000"), new NodeRole("r"), null,
            ValueArray<MaterializedAttribute>.Empty, ValueArray<MaterializedCapability>.Empty, 0);
        var result = Codec.Encode(new ObservationMaterialization(
            CodecFixtures.Basis(),
            ValueArray<MaterializedNode>.From(new[] { nodeFullwidthA, nodeLinearB }),
            ValueArray<MaterializedSource>.Empty,
            CompletenessMap.Complete));

        var hex = CodecFixtures.ToHex(result.CopyPayload());
        Assert.That(hex, Is.EqualTo(Vector4Hex));
        Assert.That(CodecFixtures.ToHex(result.Id.Digest.ToArray()), Is.EqualTo(Vector4Sha256));
        Assert.That(
            hex.IndexOf("f0908080", StringComparison.Ordinal),
            Is.LessThan(hex.IndexOf("efbca1", StringComparison.Ordinal)),
            "the non-BMP key must precede the BMP key: UTF-16 ordinal order, not UTF-8 byte order");
    }

    [Test]
    public void TheNonBmpVectorDecodes()
    {
        var decoded = Codec.Decode(
            CodecFixtures.FromHex(Vector4Hex), CodecFixtures.Incarnation, CodecFixtures.Revision);

        Assert.That(decoded.Nodes[0].Key.Value, Is.EqualTo("\U00010000"));
        Assert.That(decoded.Nodes[1].Key.Value, Is.EqualTo("Ａ"));
    }

    // ── Vector 5 — LEB128 length boundary at 127 / 128 ──

    private static string Vector5Hex()
    {
        var a127 = string.Concat(System.Linq.Enumerable.Repeat("61", 127));
        var a128 = string.Concat(System.Linq.Enumerable.Repeat("61", 128));
        return "535243530101760100016404726f6f74" + "00" + "01" +
            "0173" + "0173" + "01" + "00" + "00" +
            "02" +
            "0170" + "01" + "7f" + a127 +
            "0171" + "01" + "8001" + a128 +
            "00" + "00" + "00";
    }

    private const string Vector5Sha256 =
        "0a9ab9d1d1efedcf44c38dd332fede13e1d3c45a426c11fa0bb6aa070ad25069";

    [Test]
    public void VarintLengthsCrossTheOneByteBoundaryInMinimalForm()
    {
        // 127 encodes as 0x7F (one byte); 128 as 0x80 0x01 (two bytes, minimal
        // form). A writer that emits a non-minimal 127 or splits 128 wrongly drifts.
        var result = Codec.Encode(new ObservationMaterialization(
            CodecFixtures.Basis(),
            ValueArray<MaterializedNode>.Empty,
            ValueArray<MaterializedSource>.From(new[]
            {
                new MaterializedSource(
                    new StateSourceKey("s"),
                    new StateSourceContractRef(new StateSourceContractId("s"), new ContractVersion(1, 0)),
                    ValueArray<NamedField>.From(new[]
                    {
                        new NamedField("q", FieldValue.Of(new string('a', 128))),
                        new NamedField("p", FieldValue.Of(new string('a', 127))),
                    }),
                    ValueArray<string>.Empty,
                    omission: null),
            }),
            CompletenessMap.Complete));

        Assert.That(CodecFixtures.ToHex(result.CopyPayload()), Is.EqualTo(Vector5Hex()));
        Assert.That(CodecFixtures.ToHex(result.Id.Digest.ToArray()), Is.EqualTo(Vector5Sha256));
    }

    [Test]
    public void TheBoundaryVectorDecodes()
    {
        var decoded = Codec.Decode(
            CodecFixtures.FromHex(Vector5Hex()), CodecFixtures.Incarnation, CodecFixtures.Revision);

        Assert.That(decoded.Sources[0].Fields[0].Value.AsString.Length, Is.EqualTo(127));
        Assert.That(decoded.Sources[0].Fields[1].Value.AsString.Length, Is.EqualTo(128));
    }
}
