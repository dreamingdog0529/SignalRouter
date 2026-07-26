using System;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// guarantees.md §3.5: the canonical reason-code tables, transcribed literally.
/// Vocabularies are open (unknown codes construct and present) and the
/// Unevaluable → Incomparable mapping preserves the reason string verbatim.
/// </summary>
public sealed class ReasonCodeTests
{
    [Test]
    public void RejectedReservesExactlyTheSpecCodes()
    {
        Assert.That(RejectionReason.RequestIdConflict.Value, Is.EqualTo("RequestIdConflict"));
        Assert.That(RejectionReason.CapacityExhausted.Value, Is.EqualTo("CapacityExhausted"));
        Assert.That(RejectionReason.ReentrantDispatch.Value, Is.EqualTo("ReentrantDispatch"));
        Assert.That(RejectionReason.TargetNotFound.Value, Is.EqualTo("TargetNotFound"));
        Assert.That(RejectionReason.CapabilityUnavailable.Value, Is.EqualTo("CapabilityUnavailable"));
        Assert.That(RejectionReason.PreconditionFailed.Value, Is.EqualTo("PreconditionFailed"));
        Assert.That(RejectionReason.IncarnationMismatch.Value, Is.EqualTo("IncarnationMismatch"));
        Assert.That(RejectionReason.UnkeyedTarget.Value, Is.EqualTo("UnkeyedTarget"));
        Assert.That(RejectionReason.RequestIdConflict.IsCanonical, Is.True);
        Assert.That(RejectionReason.TargetNotFound.IsCanonical, Is.True);
    }

    [Test]
    public void IncompleteReservesExactlyTheSpecCodes()
    {
        Assert.That(IncompleteReason.SizeLimit.Value, Is.EqualTo("SizeLimit"));
        Assert.That(IncompleteReason.ExternalMutation.Value, Is.EqualTo("ExternalMutation"));
        Assert.That(IncompleteReason.SinkFault.Value, Is.EqualTo("SinkFault"));
        Assert.That(IncompleteReason.ContractChanged.Value, Is.EqualTo("ContractChanged"));
        Assert.That(IncompleteReason.UnkeyedTarget.Value, Is.EqualTo("UnkeyedTarget"));
        Assert.That(IncompleteReason.IncarnationChanged.Value, Is.EqualTo("IncarnationChanged"));
    }

    [Test]
    public void IncomparableReservesExactlyTheSpecCodes()
    {
        Assert.That(IncomparableReason.UnsupportedProfileVersion.Value, Is.EqualTo("UnsupportedProfileVersion"));
        Assert.That(IncomparableReason.Incompleteness.Value, Is.EqualTo("Incompleteness"));
        Assert.That(IncomparableReason.UnknownMandatoryExtension.Value, Is.EqualTo("UnknownMandatoryExtension"));
        Assert.That(IncomparableReason.MissingMigration.Value, Is.EqualTo("MissingMigration"));
        Assert.That(IncomparableReason.Contamination.Value, Is.EqualTo("Contamination"));
        Assert.That(IncomparableReason.CancellationTiming.Value, Is.EqualTo("CancellationTiming"));
        Assert.That(IncomparableReason.TemporalPredicate.Value, Is.EqualTo("TemporalPredicate"));
        Assert.That(IncomparableReason.PredicateFault.Value, Is.EqualTo("PredicateFault"));
    }

    [Test]
    public void UnevaluableMirrorsTheCompletenessVocabulary()
    {
        Assert.That(UnevaluableReason.Redacted.Value, Is.EqualTo("Redacted"));
        Assert.That(UnevaluableReason.OutOfScope.Value, Is.EqualTo("OutOfScope"));
        Assert.That(UnevaluableReason.Incompleteness.Value, Is.EqualTo("Incompleteness"));
        Assert.That(UnevaluableReason.UnsupportedContract.Value, Is.EqualTo("UnsupportedContract"));
        Assert.That(UnevaluableReason.SourceUnavailable.Value, Is.EqualTo("SourceUnavailable"));
        Assert.That(UnevaluableReason.Stale.Value, Is.EqualTo("Stale"));
    }

    [Test]
    public void UnevaluableMapsToIncomparablePreservingTheReasonVerbatim()
    {
        var mapped = IncomparableReason.FromUnevaluable(UnevaluableReason.Redacted);
        Assert.That(mapped.Value, Is.EqualTo("Redacted"));
        Assert.That(mapped.IsCanonical, Is.True, "every Unevaluable reason is a valid Incomparable reason");
        Assert.That(
            IncomparableReason.FromUnevaluable(UnevaluableReason.Stale).Value,
            Is.EqualTo(UnevaluableReason.Stale.Value));
    }

    [Test]
    public void FaultCodeReservesThePostconditionAndEvidenceFailureCodes()
    {
        Assert.That(FaultCode.CompletionPostconditionNotSatisfied.Value, Is.EqualTo("CompletionPostconditionNotSatisfied"));
        Assert.That(FaultCode.EvidenceUnavailable.Value, Is.EqualTo("EvidenceUnavailable"));
        Assert.That(new FaultCode("AppSpecificFault").IsReserved, Is.False);
    }

    [Test]
    public void VocabulariesAreOpenButGrammarBounded()
    {
        var unknown = new RejectionReason("SomeFutureCode");
        Assert.That(unknown.IsCanonical, Is.False);
        Assert.That(unknown.Value, Is.EqualTo("SomeFutureCode"));
        AssertEx.Throws<ArgumentException>(() => _ = new RejectionReason("has space"));
        AssertEx.Throws<ArgumentException>(() => _ = new IncompleteReason(""));
    }
}
