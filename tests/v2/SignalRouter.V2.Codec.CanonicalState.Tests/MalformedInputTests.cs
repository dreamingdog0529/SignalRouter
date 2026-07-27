using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Codec.CanonicalState.Tests;

/// <summary>
/// Adversarial decode inputs: every malformed class answers a structured
/// <see cref="CanonicalStateFormatException"/> with its stable code — never a
/// partial materialization, an unbounded allocation, or a raw framework exception.
/// </summary>
public sealed class MalformedInputTests
{
    private static readonly CanonicalStateCodec Codec = new();

    private static byte[] Golden() =>
        Codec.Encode(CodecFixtures.Representative()).CopyPayload();

    private static CanonicalStateFormatException DecodeFails(byte[] payload) =>
        AssertEx.Throws<CanonicalStateFormatException>(
            () => Codec.Decode(payload, CodecFixtures.Incarnation, CodecFixtures.Revision));

    [Test]
    public void EveryStrictPrefixIsTruncated()
    {
        var payload = Golden();
        for (var length = 0; length < payload.Length; length++)
        {
            var prefix = new byte[length];
            Array.Copy(payload, prefix, length);
            DecodeFails(prefix);
        }
    }

    [Test]
    public void TrailingBytesAreRejected()
    {
        var payload = Golden();
        var extended = new byte[payload.Length + 1];
        Array.Copy(payload, extended, payload.Length);
        Assert.That(DecodeFails(extended).Code, Is.EqualTo("TrailingBytes"));
    }

    [Test]
    public void BadMagicAndUnsupportedVersionsAreRejected()
    {
        var wrongMagic = Golden();
        wrongMagic[0] = 0x54;
        Assert.That(DecodeFails(wrongMagic).Code, Is.EqualTo("BadMagic"));

        var wrongVersion = Golden();
        wrongVersion[4] = 0x02;
        Assert.That(
            DecodeFails(wrongVersion).Code, Is.EqualTo("UnsupportedVersion"),
            "readers reject an unsupported version rather than guess");
    }

    [Test]
    public void VarintsMustBeMinimalBoundedAndTerminated()
    {
        // Version varuint at offset 4 rewritten as a non-minimal 2-byte form of 1.
        var nonMinimal = Splice(Golden(), 4, 1, new byte[] { 0x81, 0x00 });
        Assert.That(DecodeFails(nonMinimal).Code, Is.EqualTo("NonMinimalVarint"));

        // 6-byte varint: five continuation groups force the overflow answer.
        var tooLong = Splice(Golden(), 4, 1, new byte[] { 0x81, 0x81, 0x81, 0x81, 0x81, 0x01 });
        Assert.That(DecodeFails(tooLong).Code, Is.EqualTo("VarintOverflow"));

        // int.MaxValue + 1 (2^31) = 0x80 0x80 0x80 0x80 0x08.
        var overflow = Splice(Golden(), 4, 1, new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 });
        Assert.That(DecodeFails(overflow).Code, Is.EqualTo("VarintOverflow"));

        // A varint whose continuation bit runs past the end of the payload.
        Assert.That(
            DecodeFails(new byte[] { 0x53, 0x52, 0x43, 0x53, 0x81 }).Code,
            Is.EqualTo("Truncated"));
    }

    [Test]
    public void VaruintBoundaryValuesRoundTripInsideStrings()
    {
        // 127/128 exercise the 1→2 byte LEB128 boundary via an identifier (bounded
        // at 1024); 16383/16384 exercise 2→3 bytes via a free-form field value.
        foreach (var length in new[] { 127, 128 })
        {
            var materialization = new ObservationMaterialization(
                CodecFixtures.Basis(scope: new string('x', length)),
                ValueArray<MaterializedNode>.Empty,
                ValueArray<MaterializedSource>.Empty,
                CompletenessMap.Complete);
            var decoded = Codec.Decode(
                Codec.Encode(materialization).CopyPayload(),
                CodecFixtures.Incarnation, CodecFixtures.Revision);
            Assert.That(decoded.Basis.Scope.Length, Is.EqualTo(length));
        }

        foreach (var length in new[] { 16383, 16384 })
        {
            var materialization = new ObservationMaterialization(
                CodecFixtures.Basis(),
                ValueArray<MaterializedNode>.Empty,
                ValueArray<MaterializedSource>.From(new[]
                {
                    new MaterializedSource(
                        new StateSourceKey("s"),
                        new StateSourceContractRef(
                            new StateSourceContractId("s"), new ContractVersion(1, 0)),
                        ValueArray<NamedField>.From(new[]
                        {
                            new NamedField("v", FieldValue.Of(new string('x', length))),
                        }),
                        ValueArray<string>.Empty,
                        omission: null),
                }),
                CompletenessMap.Complete);
            var decoded = Codec.Decode(
                Codec.Encode(materialization).CopyPayload(),
                CodecFixtures.Incarnation, CodecFixtures.Revision);
            Assert.That(decoded.Sources[0].Fields[0].Value.AsString.Length, Is.EqualTo(length));
        }
    }

    [Test]
    public void AHostileCountCannotForceAnOversizedAllocation()
    {
        // Vector-1 payload with the node count rewritten to 2^30: the pre-check
        // against the remaining bytes must refuse before any allocation.
        var minimal = Codec.Encode(CodecFixtures.Minimal()).CopyPayload();
        var nodeCountOffset = minimal.Length - 4;
        var bombed = Splice(minimal, nodeCountOffset, 1, new byte[] { 0x80, 0x80, 0x80, 0x80, 0x04 });
        Assert.That(DecodeFails(bombed).Code, Is.EqualTo("Truncated"));
    }

    [Test]
    public void StrictUtf8RejectsEveryLossyForm()
    {
        // The minimal payload's domain string "d" (len 1) sits at offset 9..10.
        byte[] WithDomainBytes(params byte[] raw)
        {
            var minimal = Codec.Encode(CodecFixtures.Minimal()).CopyPayload();
            var replacement = new byte[1 + raw.Length];
            replacement[0] = (byte)raw.Length;
            Array.Copy(raw, 0, replacement, 1, raw.Length);
            return Splice(minimal, 9, 2, replacement);
        }

        Assert.That(
            DecodeFails(WithDomainBytes(0xC1, 0x81)).Code, Is.EqualTo("InvalidUtf8"),
            "overlong encoding");
        Assert.That(
            DecodeFails(WithDomainBytes(0x80)).Code, Is.EqualTo("InvalidUtf8"),
            "stray continuation byte");
        Assert.That(
            DecodeFails(WithDomainBytes(0xED, 0xA0, 0x80)).Code, Is.EqualTo("InvalidUtf8"),
            "CESU-style encoded surrogate");
        Assert.That(
            DecodeFails(WithDomainBytes(0xF4, 0x90, 0x80, 0x80)).Code, Is.EqualTo("InvalidUtf8"),
            "scalar above U+10FFFF");
        Assert.That(
            DecodeFails(WithDomainBytes(0xE3, 0x81)).Code,
            Is.EqualTo("InvalidUtf8").Or.EqualTo("Truncated"),
            "truncated multi-byte sequence");
        Assert.That(
            DecodeFails(WithDomainBytes(0xFF)).Code, Is.EqualTo("InvalidUtf8"));
    }

    [Test]
    public void DiscriminatorBytesRejectEveryForeignValue()
    {
        var payload = Golden();
        var codes = new HashSet<string>
        {
            "InvalidBoolean", "InvalidOption", "UnknownValueTag", "BadMagic",
            "UnsupportedVersion", "NonMinimalVarint", "VarintOverflow", "Truncated",
            "InvalidUtf8", "UnknownReasonCode", "NonCanonical", "InvalidStructure",
            "NaNFloat", "TrailingBytes",
        };

        // Sweep: setting any single byte to 0xF6 either fails with a structured
        // code, or — when the byte lands inside a free 64-bit value region —
        // decodes as a different but still-canonical payload (its re-encoding is
        // byte-identical to the mutation). Never a partial answer, never a raw
        // framework exception. (0xF6 is an invalid UTF-8 lead, an invalid
        // bool/option/tag, and a varint continuation byte, so it perturbs every
        // discriminator position.)
        for (var offset = 4; offset < payload.Length; offset++)
        {
            var mutated = Golden();
            if (mutated[offset] == 0xF6)
            {
                continue;
            }

            mutated[offset] = 0xF6;
            try
            {
                var decoded = Codec.Decode(mutated, CodecFixtures.Incarnation, CodecFixtures.Revision);
                Assert.That(
                    Codec.Encode(decoded).CopyPayload(), Is.EqualTo(mutated),
                    $"offset {offset}: a successful decode must mean the mutation is itself canonical");
            }
            catch (CanonicalStateFormatException exception)
            {
                Assert.That(codes, Does.Contain(exception.Code), $"offset {offset}");
            }
        }
    }

    [Test]
    public void NaNBitPatternsAreRejected()
    {
        // The representative vector's Integer value tag (0x02) precedes 8 value
        // bytes; rewrite the tag to Float (0x04) with a NaN payload.
        var payload = Golden();
        var tagOffset = FindSequence(payload, new byte[]
        {
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05,
        });
        foreach (var nan in new[]
        {
            new byte[] { 0x7F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 },
            new byte[] { 0xFF, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
        })
        {
            var mutated = Golden();
            mutated[tagOffset] = 0x04;
            Array.Copy(nan, 0, mutated, tagOffset + 1, 8);
            Assert.That(DecodeFails(mutated).Code, Is.EqualTo("NaNFloat"));
        }
    }

    [Test]
    public void StructuralViolationsAndNonCanonicalFormsSplitByCode()
    {
        // Duplicate completeness regions: encode two entries manually by doubling
        // the single entry of the representative payload — a constructor-level
        // duplicate is InvalidStructure.
        var payload = Golden();
        var entry = new byte[]
        {
            0x09, 0x6E, 0x6F, 0x64, 0x65, 0x73, 0x2F, 0x63, 0x75, 0x74,
            0x0F, 0x42, 0x75, 0x64, 0x67, 0x65, 0x74, 0x54, 0x72, 0x75,
            0x6E, 0x63, 0x61, 0x74, 0x65, 0x64,
        };
        var entryOffset = FindSequence(payload, entry);
        var duplicated = new List<byte>(payload);
        duplicated[entryOffset - 1] = 0x02; // completeness count 1 → 2
        duplicated.InsertRange(entryOffset + entry.Length, entry);
        Assert.That(DecodeFails(duplicated.ToArray()).Code, Is.EqualTo("InvalidStructure"));

        // An out-of-order (but duplicate-free) pair is NonCanonical: the structural
        // parse succeeds, constructor sorting repairs the order, and the re-encode
        // comparison reports the payload as non-canonical.
        var second = new List<byte>
        {
            0x09, 0x6E, 0x6F, 0x64, 0x65, 0x73, 0x2F, 0x61, 0x61, 0x61, // "nodes/aaa"
            0x0F, 0x42, 0x75, 0x64, 0x67, 0x65, 0x74, 0x54, 0x72, 0x75,
            0x6E, 0x63, 0x61, 0x74, 0x65, 0x64,
        };
        var unsorted = new List<byte>(payload);
        unsorted[entryOffset - 1] = 0x02;
        unsorted.InsertRange(entryOffset + entry.Length, second); // "cut" before "aaa"
        Assert.That(DecodeFails(unsorted.ToArray()).Code, Is.EqualTo("NonCanonical"));

        // A field that is both present and redacted is a Contracts-level
        // contradiction: InvalidStructure. Rewrite the representative source's
        // redacted name "secret" to "count" (same length), colliding with the
        // present field.
        var collided = Golden();
        var redactedName = new byte[] { 0x06, 0x73, 0x65, 0x63, 0x72, 0x65, 0x74 };
        var redactedOffset = LastSequence(collided, redactedName);
        var count = new byte[] { 0x05, 0x63, 0x6F, 0x75, 0x6E, 0x74 };
        var replaced = Splice(collided, redactedOffset, redactedName.Length, count);
        Assert.That(DecodeFails(replaced).Code, Is.EqualTo("InvalidStructure"));
    }

    [Test]
    public void UnknownReasonCodesAreRejected()
    {
        var payload = Golden();
        // "BudgetTruncated" (len 0x0F) → same-length unknown code.
        var reason = new byte[]
        {
            0x0F, 0x42, 0x75, 0x64, 0x67, 0x65, 0x74, 0x54, 0x72, 0x75,
            0x6E, 0x63, 0x61, 0x74, 0x65, 0x64,
        };
        var offset = FindSequence(payload, reason);
        var mutated = Golden();
        mutated[offset + 1] = 0x58; // "XudgetTruncated"
        Assert.That(DecodeFails(mutated).Code, Is.EqualTo("UnknownReasonCode"));
    }

    private static byte[] Splice(byte[] payload, int offset, int removeLength, byte[] replacement)
    {
        var result = new List<byte>(payload);
        result.RemoveRange(offset, removeLength);
        result.InsertRange(offset, replacement);
        return result.ToArray();
    }

    private static int FindSequence(byte[] payload, byte[] sequence)
    {
        for (var i = 0; i <= payload.Length - sequence.Length; i++)
        {
            var match = true;
            for (var j = 0; j < sequence.Length && match; j++)
            {
                match = payload[i + j] == sequence[j];
            }

            if (match)
            {
                return i;
            }
        }

        throw new InvalidOperationException("sequence not found");
    }

    private static int LastSequence(byte[] payload, byte[] sequence)
    {
        var last = -1;
        for (var i = 0; i <= payload.Length - sequence.Length; i++)
        {
            var match = true;
            for (var j = 0; j < sequence.Length && match; j++)
            {
                match = payload[i + j] == sequence[j];
            }

            if (match)
            {
                last = i;
            }
        }

        if (last < 0)
        {
            throw new InvalidOperationException("sequence not found");
        }

        return last;
    }
}
