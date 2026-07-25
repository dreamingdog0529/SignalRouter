using System;
using System.Text;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// kernel-execution.md §3 / ADR 0010 — the kernel-owned fingerprint derivation:
/// deterministic, payload-order independent, payload-difference detecting, and
/// secret-safe (sensitive values contribute a keyed HMAC, never plaintext or a
/// bare guessable hash).
/// </summary>
public sealed class InvocationCanonicalizerTests
{
    private static readonly byte[] KeyA = Encoding.UTF8.GetBytes("incarnation-key-a");
    private static readonly byte[] KeyB = Encoding.UTF8.GetBytes("incarnation-key-b");

    private static readonly ArgumentSchema Schema = new(ValueList<ArgumentField>.From(new[]
    {
        new ArgumentField("value", FieldType.String, required: true, Sensitivity.Standard),
        new ArgumentField("token", FieldType.String, required: false, Sensitivity.Sensitive),
        new ArgumentField("times", FieldType.Integer, required: false, Sensitivity.Standard),
    }));

    private static InvocationPayload Payload(params NamedField[] fields) =>
        new(ValueList<NamedField>.From(fields));

    private static CanonicalInvocation Canonicalize(InvocationPayload payload, byte[]? key = null) =>
        InvocationCanonicalizer.Canonicalize(
            TestData.Capability, TestData.KeyedTarget("save"), payload, Schema, key ?? KeyA);

    [Test]
    public void DerivationIsDeterministicAndOrderIndependent()
    {
        var first = Canonicalize(Payload(
            new NamedField("value", FieldValue.Of("x")),
            new NamedField("times", FieldValue.Of(2L))));
        var second = Canonicalize(Payload(
            new NamedField("times", FieldValue.Of(2L)),
            new NamedField("value", FieldValue.Of("x"))));

        Assert.That(second.Fingerprint, Is.EqualTo(first.Fingerprint));
        Assert.That(second.Arguments, Is.EqualTo(first.Arguments));
    }

    [Test]
    public void PayloadDifferencesChangeTheFingerprint()
    {
        var baseline = Canonicalize(Payload(new NamedField("value", FieldValue.Of("x"))));
        var differentValue = Canonicalize(Payload(new NamedField("value", FieldValue.Of("y"))));
        var extraField = Canonicalize(Payload(
            new NamedField("value", FieldValue.Of("x")),
            new NamedField("times", FieldValue.Of(1L))));

        Assert.That(differentValue.Fingerprint, Is.Not.EqualTo(baseline.Fingerprint));
        Assert.That(extraField.Fingerprint, Is.Not.EqualTo(baseline.Fingerprint));
    }

    [Test]
    public void TargetAndContractParticipateInTheFingerprint()
    {
        var payload = Payload(new NamedField("value", FieldValue.Of("x")));
        var baseline = Canonicalize(payload);
        var otherTarget = InvocationCanonicalizer.Canonicalize(
            TestData.Capability, TestData.KeyedTarget("other"), payload, Schema, KeyA);

        Assert.That(otherTarget.Fingerprint, Is.Not.EqualTo(baseline.Fingerprint));
        Assert.That(
            otherTarget.Arguments, Is.EqualTo(baseline.Arguments),
            "the argument digest covers arguments only");
    }

    [Test]
    public void SensitiveValuesRequireTheKeyAndNeverAppearBare()
    {
        // security-resources.md §4: a low-entropy secret must not be confirmable by
        // hashing a guess — the sensitive contribution is keyed, so without the
        // runtime key equal payloads produce different digests under different keys.
        var payload = Payload(
            new NamedField("value", FieldValue.Of("x")),
            new NamedField("token", FieldValue.Of("hunter2")));

        var underKeyA = Canonicalize(payload, KeyA);
        var underKeyB = Canonicalize(payload, KeyB);
        Assert.That(underKeyB.Arguments, Is.Not.EqualTo(underKeyA.Arguments));
        Assert.That(Canonicalize(payload, KeyA).Arguments, Is.EqualTo(underKeyA.Arguments));

        AssertEx.Throws<ArgumentException>(() => InvocationCanonicalizer.Canonicalize(
            TestData.Capability, TestData.KeyedTarget("save"), payload, Schema,
            Array.Empty<byte>()));
    }

    [Test]
    public void SchemaViolationsAreRejected()
    {
        AssertEx.Throws<ArgumentException>(() => Canonicalize(Payload(
            new NamedField("unknown", FieldValue.Of("x")))));
        AssertEx.Throws<ArgumentException>(() => Canonicalize(Payload(
            new NamedField("value", FieldValue.Of(1L)))));
        AssertEx.Throws<ArgumentException>(() => Canonicalize(InvocationPayload.Empty));
    }

    [Test]
    public void SeparatorCharactersInValuesCannotForgeCollisions()
    {
        // Length framing makes the canonical form injective even when a value
        // embeds the record/field separators a naive encoding would rely on.
        var forged = Canonicalize(Payload(
            new NamedField("value", FieldValue.Of("x\u001etimes\u001fi\u001f2"))));
        var genuine = Canonicalize(Payload(
            new NamedField("value", FieldValue.Of("x")),
            new NamedField("times", FieldValue.Of(2L))));

        Assert.That(forged.Fingerprint, Is.Not.EqualTo(genuine.Fingerprint));
        Assert.That(forged.Arguments, Is.Not.EqualTo(genuine.Arguments));
    }

    [Test]
    public void PayloadFieldNamesAreUnique()
    {
        AssertEx.Throws<ArgumentException>(() => _ = Payload(
            new NamedField("value", FieldValue.Of("x")),
            new NamedField("value", FieldValue.Of("y"))));
    }
}
