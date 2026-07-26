using System;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Codec.CanonicalState.Tests;

/// <summary>
/// Encode→Decode round trips (with the temporal legs supplied from the tuple, per
/// ADR 0012), the `Encode(Decode(b)) == b` canonical guarantee, and
/// verify-before-use semantics (semantic-model.md §5).
/// </summary>
public sealed class RoundTripAndVerifyTests
{
    private static readonly CanonicalStateCodec Codec = new();

    private static ObservationMaterialization RoundTrip(ObservationMaterialization original)
    {
        var encoded = Codec.Encode(original);
        return Codec.Decode(
            encoded.CopyPayload(), original.Basis.Incarnation, original.Basis.Revision);
    }

    [Test]
    public void TheRepresentativeWorldRoundTripsStructurally()
    {
        var original = CodecFixtures.Representative();
        var decoded = RoundTrip(original);

        Assert.That(decoded.Basis, Is.EqualTo(original.Basis));
        Assert.That(decoded.Nodes, Is.EqualTo(original.Nodes));
        Assert.That(decoded.Sources, Is.EqualTo(original.Sources));
        Assert.That(decoded.Completeness, Is.EqualTo(original.Completeness));
    }

    [Test]
    public void EveryValueKindRoundTrips()
    {
        var source = new MaterializedSource(
            new StateSourceKey("s"),
            new StateSourceContractRef(new StateSourceContractId("s"), new ContractVersion(1, 0)),
            ValueList<NamedField>.From(new[]
            {
                new NamedField("b", FieldValue.Of(true)),
                new NamedField("f", FieldValue.Of(-123.456)),
                new NamedField("i", FieldValue.Of(-9007199254740993L)),
                new NamedField("n", FieldValue.Null),
                new NamedField("s", FieldValue.Of("text-\U0001F600")),
            }),
            ValueList<string>.Empty,
            omission: null);
        var original = new ObservationMaterialization(
            CodecFixtures.Basis(),
            ValueList<MaterializedNode>.Empty,
            ValueList<MaterializedSource>.From(new[] { source }),
            CompletenessMap.Complete);

        Assert.That(RoundTrip(original).Sources, Is.EqualTo(original.Sources));
    }

    [Test]
    public void OmittedSourcesAndRootTruncationRoundTrip()
    {
        var original = new ObservationMaterialization(
            CodecFixtures.Basis(),
            ValueList<MaterializedNode>.Empty,
            ValueList<MaterializedSource>.From(new[]
            {
                new MaterializedSource(
                    new StateSourceKey("s"),
                    new StateSourceContractRef(new StateSourceContractId("s"), new ContractVersion(1, 0)),
                    ValueList<NamedField>.Empty,
                    ValueList<string>.From(new[] { "secret" }),
                    CompletenessReason.Stale),
            }),
            CompletenessMap.From(
                new[]
                {
                    new CompletenessEntry(new FieldPath("sources/s"), CompletenessReason.Stale),
                },
                maxEntries: 2,
                rootTruncated: true));
        var decoded = RoundTrip(original);

        Assert.That(decoded.Sources, Is.EqualTo(original.Sources));
        Assert.That(decoded.Completeness, Is.EqualTo(original.Completeness));
        Assert.That(decoded.Completeness.RootTruncated, Is.True);
    }

    [Test]
    public void DecodeReencodesToTheIdenticalBytes()
    {
        var encoded = Codec.Encode(CodecFixtures.Representative());
        var decoded = Codec.Decode(
            encoded.CopyPayload(), CodecFixtures.Incarnation, CodecFixtures.Revision);
        Assert.That(
            Codec.Encode(decoded).CopyPayload(), Is.EqualTo(encoded.CopyPayload()),
            "Encode(Decode(b)) == b holds by construction");
    }

    [Test]
    public void TheDecodedBasisCarriesTheSuppliedTemporalLegs()
    {
        // The tuple is the temporal authority (ADR 0012): the same payload decodes
        // under whichever incarnation/revision the referencing cut supplies.
        var encoded = Codec.Encode(CodecFixtures.Minimal());
        var later = Codec.Decode(
            encoded.CopyPayload(), new RuntimeIncarnationId("incarnation-9"), new SourceRevision(42));

        Assert.That(later.Basis.Incarnation, Is.EqualTo(new RuntimeIncarnationId("incarnation-9")));
        Assert.That(later.Basis.Revision, Is.EqualTo(new SourceRevision(42)));
        Assert.That(later.Basis.Domain, Is.EqualTo(new SecurityDomainId("d")));
    }

    [Test]
    public void VerifyAcceptsTheGoodPairAndRejectsEveryFlippedByte()
    {
        var encoded = Codec.Encode(CodecFixtures.Minimal());
        var payload = encoded.CopyPayload();
        Assert.That(CanonicalStateCodec.Verify(encoded.Id, payload), Is.True);

        for (var offset = 0; offset < payload.Length; offset++)
        {
            var tampered = encoded.CopyPayload();
            tampered[offset] ^= 0x01;
            Assert.That(
                CanonicalStateCodec.Verify(encoded.Id, tampered), Is.False,
                $"a flip at offset {offset} must fail verification");
        }
    }

    [Test]
    public void VerifyRejectsForeignAlgorithmsVersionsAndDefaults()
    {
        var encoded = Codec.Encode(CodecFixtures.Minimal());
        var payload = encoded.CopyPayload();

        Assert.That(
            CanonicalStateCodec.Verify(
                new ContentId("sha512", 1, encoded.Id.Digest), payload),
            Is.False, "a foreign algorithm never verifies, it answers false");
        Assert.That(
            CanonicalStateCodec.Verify(
                new ContentId("sha256", 2, encoded.Id.Digest), payload),
            Is.False, "a foreign representation version answers false");
        Assert.That(CanonicalStateCodec.Verify(default, payload), Is.False);
        AssertEx.Throws<ArgumentNullException>(() => CanonicalStateCodec.Verify(encoded.Id, null!));
    }

    [Test]
    public void TheCodecSurfaceIsStableAndReusable()
    {
        Assert.That(CanonicalStateCodec.AlgorithmId, Is.EqualTo("sha256"));
        Assert.That(CanonicalStateCodec.RepresentationVersion, Is.EqualTo(1));
        var codec = new CanonicalStateCodec();
        var first = codec.Encode(CodecFixtures.Minimal());
        var second = codec.Encode(CodecFixtures.Representative());
        var third = codec.Encode(CodecFixtures.Minimal());
        Assert.That(third.Id, Is.EqualTo(first.Id));
        Assert.That(second.Id, Is.Not.EqualTo(first.Id));
        AssertEx.Throws<ArgumentNullException>(() => codec.Encode(null!));
        AssertEx.Throws<ArgumentNullException>(
            () => codec.Decode(null!, CodecFixtures.Incarnation, CodecFixtures.Revision));
        AssertEx.Throws<ArgumentException>(
            () => codec.Decode(first.CopyPayload(), default, CodecFixtures.Revision));
    }
}
