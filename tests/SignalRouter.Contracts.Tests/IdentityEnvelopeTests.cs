using System;
using NUnit.Framework;

namespace SignalRouter.Contracts.Tests;

/// <summary>
/// semantic-model.md §6 and guarantees.md §5.2/§5.8: the envelope carries four
/// orthogonal fields, and continuation causality requires
/// ParentRequestId + ContinuationOrdinal + fingerprint.
/// </summary>
public sealed class IdentityEnvelopeTests
{
    [Test]
    public void ContinuationCausalityCarriesAllThreeBindingComponents()
    {
        var link = new ContinuationLink(TestData.Request("parent"), 0, TestData.Fingerprint("child"));
        var causality = Causality.OfContinuation(link);

        Assert.That(causality.Kind, Is.EqualTo(CausalityKind.Continuation));
        Assert.That(causality.Continuation, Is.EqualTo(link));
        Assert.That(causality.Continuation!.Value.ParentRequestId, Is.EqualTo(TestData.Request("parent")));
        Assert.That(causality.Continuation!.Value.ContinuationOrdinal, Is.EqualTo(0));
        Assert.That(causality.Continuation!.Value.Fingerprint, Is.EqualTo(TestData.Fingerprint("child")));
    }

    [Test]
    public void ContinuationLinkRejectsMissingComponents()
    {
        AssertEx.Throws<ArgumentException>(
            () => _ = new ContinuationLink(default, 0, TestData.Fingerprint("child")));
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => _ = new ContinuationLink(TestData.Request("parent"), -1, TestData.Fingerprint("child")));
        AssertEx.Throws<ArgumentException>(
            () => _ = new ContinuationLink(TestData.Request("parent"), 0, default));
        AssertEx.Throws<ArgumentException>(() => Causality.OfContinuation(default));
    }

    [Test]
    public void NonContinuationCausalityCarriesNoLink()
    {
        Assert.That(Causality.Root().Continuation, Is.Null);
        Assert.That(Causality.OfExternalTrigger("scene-load").Continuation, Is.Null);
        Assert.That(Causality.OfExternalTrigger(null).ExternalTriggerHint, Is.Null);
    }

    [Test]
    public void EnvelopeCarriesFourOrthogonalFields()
    {
        var envelope = new IdentityEnvelope(
            new Principal(Principal.WellKnownKinds.LocalUser, "user-1"),
            IngressPath.PhysicalInput,
            Provenance.HumanDirected,
            Causality.Root());

        Assert.That(envelope.Principal.Kind, Is.EqualTo("LocalUser"));
        Assert.That(envelope.Ingress, Is.EqualTo(IngressPath.PhysicalInput));
        Assert.That(envelope.Provenance, Is.EqualTo(Provenance.HumanDirected));
        Assert.That(envelope.Causality.Kind, Is.EqualTo(CausalityKind.Root));
    }

    [Test]
    public void EnvelopeRejectsMissingFields()
    {
        AssertEx.Throws<ArgumentNullException>(() => _ = new IdentityEnvelope(
            null!, IngressPath.Mcp, Provenance.Automation, Causality.Root()));
        AssertEx.Throws<ArgumentException>(() => _ = new IdentityEnvelope(
            new Principal("AgentSession", "a"), default, Provenance.Automation, Causality.Root()));
        AssertEx.Throws<ArgumentNullException>(() => _ = new IdentityEnvelope(
            new Principal("AgentSession", "a"), IngressPath.Mcp, Provenance.Automation, null!));
    }

    [Test]
    public void ProvenanceIsAClosedThreeValueVocabulary()
    {
        Assert.That(Enum.GetNames<Provenance>(), Is.EqualTo(new[]
        {
            "HumanDirected", "Automation", "Unknown",
        }));
    }
}
