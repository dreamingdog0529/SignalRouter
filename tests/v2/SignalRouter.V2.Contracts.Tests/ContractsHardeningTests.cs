using System;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// ADR 0012 hardening — every constructor-valid materialization must be
/// canonically encodable: unpaired surrogates are rejected at construction (strict
/// UTF-8 cannot represent them), the four comparator states stay disjoint, and
/// undefined reason values never enter a completeness entry.
/// </summary>
public sealed class ContractsHardeningTests
{
    private const string LoneHighSurrogate = "\uD800";
    private const string LoneLowSurrogate = "\uDC00";

    [Test]
    public void IdentifiersRejectUnpairedSurrogates()
    {
        AssertEx.Throws<ArgumentException>(
            () => ContractGrammar.ValidateIdentifier("x" + LoneHighSurrogate, "value"));
        AssertEx.Throws<ArgumentException>(
            () => ContractGrammar.ValidateIdentifier(LoneLowSurrogate + "x", "value"));
        Assert.That(
            ContractGrammar.ValidateIdentifier("emoji-\U0001F600-ok", "value"),
            Is.EqualTo("emoji-\U0001F600-ok"),
            "a well-formed surrogate pair stays legal");
    }

    [Test]
    public void StringFieldValuesRejectUnpairedSurrogates()
    {
        AssertEx.Throws<ArgumentException>(() => FieldValue.Of("x" + LoneHighSurrogate));
        AssertEx.Throws<ArgumentException>(() => FieldValue.Of(LoneLowSurrogate));
        Assert.That(
            FieldValue.Of("pair-\U0001F600").AsString, Is.EqualTo("pair-\U0001F600"));
        Assert.That(
            FieldValue.Of("control--ok").AsString, Is.EqualTo("control--ok"),
            "free-form values keep permitting control characters — only scalar validity is enforced");
    }

    [Test]
    public void ADefaultNamedFieldIsDetectableAndRejectedBySources()
    {
        Assert.That(default(NamedField).IsDefault, Is.True);
        var contract = new StateSourceContractRef(
            new StateSourceContractId("inventory"), new ContractVersion(1, 0));
        AssertEx.Throws<ArgumentException>(() => new MaterializedSource(
            new StateSourceKey("inventory"), contract,
            ValueList<NamedField>.From(new[] { default(NamedField) }),
            ValueList<string>.Empty,
            omission: null));
    }

    [Test]
    public void APresentFieldCanNeverAlsoBeRedacted()
    {
        var contract = new StateSourceContractRef(
            new StateSourceContractId("inventory"), new ContractVersion(1, 0));
        AssertEx.Throws<ArgumentException>(() => new MaterializedSource(
            new StateSourceKey("inventory"), contract,
            ValueList<NamedField>.From(new[] { new NamedField("count", FieldValue.Of(1L)) }),
            ValueList<string>.From(new[] { "count" }),
            omission: null));

        // An omitted source may still declare redacted names — the intended
        // projector state for an unavailable source with a sensitive schema.
        var omitted = new MaterializedSource(
            new StateSourceKey("inventory"), contract,
            ValueList<NamedField>.Empty,
            ValueList<string>.From(new[] { "secret" }),
            CompletenessReason.SourceUnavailable);
        Assert.That(omitted.RedactedFieldNames.Count, Is.EqualTo(1));
    }

    [Test]
    public void RedactedFieldNamesFollowTheIdentifierGrammar()
    {
        var contract = new StateSourceContractRef(
            new StateSourceContractId("inventory"), new ContractVersion(1, 0));
        AssertEx.Throws<ArgumentException>(() => new MaterializedSource(
            new StateSourceKey("inventory"), contract,
            ValueList<NamedField>.Empty,
            ValueList<string>.From(new[] { "" }),
            omission: null));
        AssertEx.Throws<ArgumentException>(() => new MaterializedSource(
            new StateSourceKey("inventory"), contract,
            ValueList<NamedField>.Empty,
            ValueList<string>.From(new[] { "bad" + LoneHighSurrogate }),
            omission: null));
    }

    [Test]
    public void CompletenessEntriesRejectUndefinedReasons()
    {
        AssertEx.Throws<ArgumentException>(
            () => new CompletenessEntry(new FieldPath("nodes/save"), (CompletenessReason)999));
        Assert.That(
            new CompletenessEntry(new FieldPath("nodes/save"), CompletenessReason.UnsupportedContract)
                .Reason,
            Is.EqualTo(CompletenessReason.UnsupportedContract),
            "every defined reason stays constructible");
    }
}
