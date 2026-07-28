using System.Linq;
using NUnit.Framework;
using SignalRouter.Contracts;

namespace SignalRouter.Codec.CanonicalState.Tests;

/// <summary>
/// Byte-exact golden vectors transcribed from GoldenVectors.md (hand-derived from
/// the ADR 0012 grammar; digests computed externally from the hex literals) plus
/// the determinism obligations. Any change to these bytes is a representation
/// break, which ADR 0012 defines as a new version, never an edit.
/// </summary>
public sealed class GoldenVectorAndDeterminismTests
{
    private const string Vector1Hex =
        "535243530101760100016404726f6f7400000000";

    private const string Vector1Sha256 =
        "a11521e8718da761ff84ca6ef5b9e8877e74f754733373cc8648a83aa878306b";

    private const string Vector2Hex =
        "53524353010e6167656e742d7374616e6461726401000c6167656e742d646f6d61696e04726f6f74" +
        "01047361766506627574746f6e010570616e656c02056c6162656c000104536176650673656372" +
        "6574010106496e766f6b65010001020109696e76656e746f727909696e76656e746f7279010000" +
        "0105636f756e7402000000000000000501067365637265740101096e6f6465732f6375740f4275" +
        "646765745472756e6361746564";

    private const string Vector2Sha256 =
        "77ca21927ef0f1ddf361a9ceddc08e6aadf2a65ea82cd964a7d032280488455a";

    private static readonly CanonicalStateCodec Codec = new();

    [Test]
    public void TheMinimalVectorEncodesByteExactly()
    {
        var result = Codec.Encode(CodecFixtures.Minimal());
        Assert.That(CodecFixtures.ToHex(result.CopyPayload()), Is.EqualTo(Vector1Hex));
        Assert.That(CodecFixtures.ToHex(result.Id.Digest.ToArray()), Is.EqualTo(Vector1Sha256));
        Assert.That(result.Id.DigestAlgorithmId, Is.EqualTo("sha256"));
        Assert.That(result.Id.CanonicalRepresentationVersion, Is.EqualTo(1));
        Assert.That(result.Length, Is.EqualTo(result.CopyPayload().Length));
    }

    [Test]
    public void TheRepresentativeVectorEncodesByteExactly()
    {
        var result = Codec.Encode(CodecFixtures.Representative());
        Assert.That(CodecFixtures.ToHex(result.CopyPayload()), Is.EqualTo(Vector2Hex));
        Assert.That(CodecFixtures.ToHex(result.Id.Digest.ToArray()), Is.EqualTo(Vector2Sha256));
    }

    [Test]
    public void EncodingIsDeterministicAcrossInstancesAndInputOrder()
    {
        var first = Codec.Encode(CodecFixtures.Representative());
        var second = Codec.Encode(CodecFixtures.Representative());
        var permuted = new CanonicalStateCodec().Encode(
            CodecFixtures.Representative(permuteInputOrder: true));

        Assert.That(second.CopyPayload(), Is.EqualTo(first.CopyPayload()));
        Assert.That(
            permuted.CopyPayload(), Is.EqualTo(first.CopyPayload()),
            "constructor normalization makes input order irrelevant");
        Assert.That(permuted.Id, Is.EqualTo(first.Id));
    }

    [Test]
    public void TemporalLegsNeverInfluenceTheContentId()
    {
        // ADR 0012: the payload embeds the projection identity only, so an
        // unchanged state at a different incarnation/revision re-addresses to the
        // same blob (the guarantees.md §5.3 reuse premise).
        var baseline = Codec.Encode(CodecFixtures.Minimal());
        var laterRevision = Codec.Encode(CodecFixtures.Minimal(revision: new SourceRevision(999)));
        var otherIncarnation = Codec.Encode(CodecFixtures.Minimal(
            incarnation: new RuntimeIncarnationId("incarnation-2")));

        Assert.That(laterRevision.Id, Is.EqualTo(baseline.Id));
        Assert.That(laterRevision.CopyPayload(), Is.EqualTo(baseline.CopyPayload()));
        Assert.That(otherIncarnation.Id, Is.EqualTo(baseline.Id));
    }

    [Test]
    public void ProjectionIdentityLegsEachInfluenceTheContentId()
    {
        var baseline = Codec.Encode(CodecFixtures.Minimal()).Id;
        Assert.That(Codec.Encode(CodecFixtures.Minimal(view: "w")).Id, Is.Not.EqualTo(baseline));
        Assert.That(Codec.Encode(CodecFixtures.Minimal(domain: "e")).Id, Is.Not.EqualTo(baseline));
        Assert.That(Codec.Encode(CodecFixtures.Minimal(scope: "sub")).Id, Is.Not.EqualTo(baseline));
    }

    [Test]
    public void DistinctValueStatesProduceDistinctPayloads()
    {
        ObservationMaterialization WithAttribute(MaterializedAttribute[] attributes) => new(
            CodecFixtures.Basis(),
            ValueArray<MaterializedNode>.From(new[]
            {
                new MaterializedNode(
                    new AuthorKey("n"), NodeRole.Button, null,
                    ValueArray<MaterializedAttribute>.From(attributes),
                    ValueArray<MaterializedCapability>.Empty, 0),
            }),
            ValueArray<MaterializedSource>.Empty,
            CompletenessMap.Complete);

        var absent = Codec.Encode(WithAttribute(System.Array.Empty<MaterializedAttribute>()));
        var nullValue = Codec.Encode(WithAttribute(new[]
        {
            new MaterializedAttribute("a", FieldValue.Null, redacted: false),
        }));
        var redacted = Codec.Encode(WithAttribute(new[]
        {
            new MaterializedAttribute("a", default, redacted: true),
        }));
        var emptyString = Codec.Encode(WithAttribute(new[]
        {
            new MaterializedAttribute("a", FieldValue.Of(""), redacted: false),
        }));

        var payloads = new[] { absent, nullValue, redacted, emptyString }
            .Select(result => CodecFixtures.ToHex(result.CopyPayload()))
            .ToArray();
        Assert.That(payloads.Distinct().Count(), Is.EqualTo(4),
            "absent, null, redacted, and empty-string are four distinct comparator inputs");
    }

    [Test]
    public void LengthFramingSeparatesAdjacentText()
    {
        ObservationMaterialization WithSourceField(string name, string value) => new(
            CodecFixtures.Basis(),
            ValueArray<MaterializedNode>.Empty,
            ValueArray<MaterializedSource>.From(new[]
            {
                new MaterializedSource(
                    new StateSourceKey("s"),
                    new StateSourceContractRef(new StateSourceContractId("s"), new ContractVersion(1, 0)),
                    ValueArray<NamedField>.From(new[] { new NamedField(name, FieldValue.Of(value)) }),
                    ValueArray<string>.Empty,
                    omission: null),
            }),
            CompletenessMap.Complete);

        Assert.That(
            CodecFixtures.ToHex(Codec.Encode(WithSourceField("ab", "c")).CopyPayload()),
            Is.Not.EqualTo(CodecFixtures.ToHex(Codec.Encode(WithSourceField("a", "bc")).CopyPayload())),
            "framing keeps ('ab','c') and ('a','bc') apart by construction");
    }

    [Test]
    public void FloatBitPatternsAreFaithful()
    {
        ObservationMaterialization WithFloat(double value) => new(
            CodecFixtures.Basis(),
            ValueArray<MaterializedNode>.Empty,
            ValueArray<MaterializedSource>.From(new[]
            {
                new MaterializedSource(
                    new StateSourceKey("s"),
                    new StateSourceContractRef(new StateSourceContractId("s"), new ContractVersion(1, 0)),
                    ValueArray<NamedField>.From(new[] { new NamedField("f", FieldValue.Of(value)) }),
                    ValueArray<string>.Empty,
                    omission: null),
            }),
            CompletenessMap.Complete);

        // -0.0 and 0.0 are distinct payloads — permitted: ContentId inequality
        // implies nothing (semantic-model.md §5, ADR 0012).
        Assert.That(
            Codec.Encode(WithFloat(-0.0)).Id, Is.Not.EqualTo(Codec.Encode(WithFloat(0.0)).Id));
        Assert.That(
            Codec.Encode(WithFloat(double.PositiveInfinity)).Id,
            Is.Not.EqualTo(Codec.Encode(WithFloat(double.NegativeInfinity)).Id));
    }

    [Test]
    public void RootTruncationAndAvailabilityAreDistinct()
    {
        var complete = Codec.Encode(CodecFixtures.Minimal());
        var truncated = Codec.Encode(new ObservationMaterialization(
            CodecFixtures.Basis(),
            ValueArray<MaterializedNode>.Empty,
            ValueArray<MaterializedSource>.Empty,
            CompletenessMap.From(
                System.Array.Empty<CompletenessEntry>(), maxEntries: 1, rootTruncated: true)));
        Assert.That(truncated.Id, Is.Not.EqualTo(complete.Id));

        ObservationMaterialization WithAvailability(bool available) => new(
            CodecFixtures.Basis(),
            ValueArray<MaterializedNode>.From(new[]
            {
                new MaterializedNode(
                    new AuthorKey("n"), NodeRole.Button, null,
                    ValueArray<MaterializedAttribute>.Empty,
                    ValueArray<MaterializedCapability>.From(new[]
                    {
                        new MaterializedCapability(
                            new CapabilityContractRef(
                                new CapabilityContractId("Invoke"), new ContractVersion(1, 0)),
                            available),
                    }),
                    0),
            }),
            ValueArray<MaterializedSource>.Empty,
            CompletenessMap.Complete);
        Assert.That(
            Codec.Encode(WithAvailability(true)).Id,
            Is.Not.EqualTo(Codec.Encode(WithAvailability(false)).Id));
    }
}
