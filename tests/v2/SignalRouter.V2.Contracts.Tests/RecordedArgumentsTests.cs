using System;
using System.Text;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// ADR 0015 — the portable replay-input contract: E2 carries the admitted
/// arguments in recorded form (typed values; sensitive values as contract-scoped
/// secret references plus keyed digests), and re-digesting that form yields the
/// invocation's redacted argument digest exactly.
/// </summary>
public sealed class RecordedArgumentsTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("incarnation-key-a");

    private static readonly ArgumentSchema Schema = new(ValueArray<ArgumentField>.From(new[]
    {
        new ArgumentField("value", FieldType.String, required: true, Sensitivity.Standard),
        new ArgumentField("token", FieldType.String, required: false, Sensitivity.Sensitive),
        new ArgumentField("times", FieldType.Integer, required: false, Sensitivity.Standard),
    }));

    private static InvocationPayload Payload(params NamedField[] fields) =>
        new(ValueArray<NamedField>.From(fields));

    private static RecordedArguments Project(InvocationPayload payload) =>
        InvocationCanonicalizer.Project(TestData.Capability, payload, Schema, Key);

    [Test]
    public void TheProjectionRedigestsToTheCanonicalArgumentDigest()
    {
        var payload = Payload(
            new NamedField("value", FieldValue.Of("x")),
            new NamedField("token", FieldValue.Of("hunter2")),
            new NamedField("times", FieldValue.Of(3L)));
        var canonical = InvocationCanonicalizer.Canonicalize(
            TestData.Capability, TestData.KeyedTarget("save"), payload, Schema, Key);

        var recorded = Project(payload);

        Assert.That(
            InvocationCanonicalizer.DigestOf(recorded), Is.EqualTo(canonical.Arguments),
            "the recorded form is identity-equivalent to the live payload");
    }

    [Test]
    public void SensitiveValuesBecomeContractScopedReferencesNeverValues()
    {
        var recorded = Project(Payload(
            new NamedField("value", FieldValue.Of("x")),
            new NamedField("token", FieldValue.Of("hunter2"))));

        var token = recorded.Fields[0];
        Assert.That(token.Name, Is.EqualTo("token"), "'token' sorts before 'value'");
        Assert.That(token.IsSecret, Is.True);
        Assert.That(
            token.Secret.Value,
            Is.EqualTo($"{TestData.Capability.Id.Value}@{TestData.Capability.Version.Major}.{TestData.Capability.Version.Minor}/token"),
            "the reference id is contract-scoped so the resolver answers stable names");
        Assert.That(token.SecretValueDigest.Value, Does.Not.Contain("hunter2"));
        Assert.That(token.ToString(), Does.Not.Contain("hunter2"));
        AssertEx.Throws<InvalidOperationException>(() => _ = token.Value);
    }

    [Test]
    public void ANullSensitiveValueStaysATypedNullNotAReference()
    {
        // The canonicalizer exempts Null from the sensitive leg; the projection
        // must mirror that exactly or the digests diverge.
        var payload = Payload(
            new NamedField("value", FieldValue.Of("x")),
            new NamedField("token", FieldValue.Null));
        var recorded = Project(payload);

        var token = recorded.Fields[0];
        Assert.That(token.Name, Is.EqualTo("token"));
        Assert.That(token.IsSecret, Is.False);
        Assert.That(token.Value.Kind, Is.EqualTo(FieldValueKind.Null));
        Assert.That(
            InvocationCanonicalizer.DigestOf(recorded),
            Is.EqualTo(InvocationCanonicalizer.Canonicalize(
                TestData.Capability, TestData.KeyedTarget("save"), payload, Schema, Key).Arguments));
    }

    [Test]
    public void TheProjectionIsPayloadOrderIndependent()
    {
        var first = Project(Payload(
            new NamedField("value", FieldValue.Of("x")),
            new NamedField("times", FieldValue.Of(2L))));
        var second = Project(Payload(
            new NamedField("times", FieldValue.Of(2L)),
            new NamedField("value", FieldValue.Of("x"))));

        Assert.That(second, Is.EqualTo(first));
        Assert.That(first.Fields[0].Name, Is.EqualTo("times"), "canonical order is ordinal");
    }

    [Test]
    public void TheProjectionValidatesLikeCanonicalization()
    {
        AssertEx.Throws<ArgumentException>(() => Project(
            Payload(new NamedField("undeclared", FieldValue.Of("x")))));
        AssertEx.Throws<ArgumentException>(() => Project(
            Payload(new NamedField("times", FieldValue.Of(1L)))));
        AssertEx.Throws<ArgumentException>(() => InvocationCanonicalizer.Project(
            TestData.Capability,
            Payload(new NamedField("value", FieldValue.Of("x"))),
            Schema,
            Array.Empty<byte>()));
    }

    [Test]
    public void RecordedArgumentsRejectDisorderDuplicatesAndDefaults()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new RecordedArguments(
            ValueArray<RecordedArgument>.From(new[]
            {
                RecordedArgument.OfValue("b", FieldValue.Of("1")),
                RecordedArgument.OfValue("a", FieldValue.Of("2")),
            })));
        AssertEx.Throws<ArgumentException>(() => _ = new RecordedArguments(
            ValueArray<RecordedArgument>.From(new[]
            {
                RecordedArgument.OfValue("a", FieldValue.Of("1")),
                RecordedArgument.OfValue("a", FieldValue.Of("2")),
            })));
        AssertEx.Throws<ArgumentException>(() => _ = new RecordedArguments(
            ValueArray<RecordedArgument>.From(new RecordedArgument[] { default })));
        Assert.That(RecordedArguments.Empty.Fields.Count, Is.Zero);
    }

    [Test]
    public void TheUnionLegsAreExclusive()
    {
        var value = RecordedArgument.OfValue("a", FieldValue.Of("1"));
        AssertEx.Throws<InvalidOperationException>(() => _ = value.Secret);
        AssertEx.Throws<InvalidOperationException>(() => _ = value.SecretValueDigest);

        var secret = RecordedArgument.OfSecret(
            "t", new SecretReference("cap@1.0/t"), new ArgumentDigest("ab12"));
        AssertEx.Throws<InvalidOperationException>(() => _ = secret.Value);
        AssertEx.Throws<ArgumentException>(() => RecordedArgument.OfSecret(
            "t", default, new ArgumentDigest("ab12")));
        AssertEx.Throws<ArgumentException>(() => RecordedArgument.OfSecret(
            "t", new SecretReference("cap@1.0/t"), default));
        AssertEx.Throws<ArgumentException>(() => RecordedArgument.OfValue("t", default));
    }

    [Test]
    public void TheAdmissionCutCarriesTheRecordedForm()
    {
        var cut = new AdmissionCut(
            new EvidenceSequence(1),
            TestData.Request("r-1"),
            new LogicalOrder(1),
            TestData.Fingerprint("r-1"),
            TestData.Invocation("r-1"),
            TestData.Recorded("r-1"),
            TestData.KeyedTarget("key-r-1"),
            TestData.Envelope(null));

        Assert.That(cut.Arguments, Is.EqualTo(TestData.Recorded("r-1")));

        Action nullArguments = () => _ = new AdmissionCut(
            new EvidenceSequence(1),
            TestData.Request("r-1"),
            new LogicalOrder(1),
            TestData.Fingerprint("r-1"),
            TestData.Invocation("r-1"),
            null!,
            TestData.KeyedTarget("key-r-1"),
            TestData.Envelope(null));
        AssertEx.Throws<ArgumentNullException>(nullArguments);
    }

    [Test]
    public void ThePredicateArmedCutCarriesTheObservationScope()
    {
        var cut = new PredicateArmed(
            new EvidenceSequence(1),
            TestData.Operation("w-1"),
            TestData.Predicate,
            TestData.Arguments("w-1"),
            TestData.Fingerprint("w-1"),
            TestData.RecordView,
            "root",
            Causality.Root(),
            new ViewSequence(1));

        Assert.That(cut.ObservationScope, Is.EqualTo("root"));

        Action emptyScope = () => _ = new PredicateArmed(
            new EvidenceSequence(1),
            TestData.Operation("w-1"),
            TestData.Predicate,
            TestData.Arguments("w-1"),
            TestData.Fingerprint("w-1"),
            TestData.RecordView,
            string.Empty,
            Causality.Root(),
            new ViewSequence(1));
        AssertEx.Throws<ArgumentException>(emptyScope);
    }
}
