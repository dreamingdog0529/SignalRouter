using System;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// guarantees.md §5: single-cut well-formedness enforced by constructors. Cross-cut
/// rules are deliberately NOT exceptions — see StructureRuleTests.
/// </summary>
public sealed class EvidenceCutTests
{
    [Test]
    public void TerminalNeverRecordsOutcomeUnknown()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.OutcomeUnknown,
            effectPermitted: false,
            TestData.Content("after")));
    }

    [Test]
    public void RejectedTerminalRequiresNoPermittedEffectAndAReason()
    {
        // guarantees.md §3.1: "Rejected MUST imply effectPermitted = false".
        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Rejected,
            effectPermitted: true,
            TestData.Content("after"),
            rejectionReason: RejectionReason.RequestIdConflict));

        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Rejected,
            effectPermitted: false,
            TestData.Content("after")));
    }

    [Test]
    public void FaultedRequiresAFaultCodeAndOnlyEvidenceUnavailableFaultsWithoutAPermit()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Faulted,
            effectPermitted: true,
            TestData.Content("after")));

        // guarantees.md §3.1: pre-effect evidence failure is the sole Faulted with effectPermitted = false.
        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Faulted,
            effectPermitted: false,
            TestData.Content("after"),
            faultCode: new FaultCode("AppFault")));

        var preEffectEvidenceFailure = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Faulted,
            effectPermitted: false,
            TestData.Content("after"),
            faultCode: FaultCode.EvidenceUnavailable);
        Assert.That(preEffectEvidenceFailure.FaultCode, Is.EqualTo(FaultCode.EvidenceUnavailable));

        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Faulted,
            effectPermitted: true,
            TestData.Content("after"),
            faultCode: FaultCode.EvidenceUnavailable));
    }

    [Test]
    public void SucceededRequiresCompletionEvidenceAndOthersRefuseIt()
    {
        // guarantees.md §5.4: completion evidence is required exactly for Succeeded.
        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Succeeded,
            effectPermitted: true,
            TestData.Content("after")));

        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Rejected,
            effectPermitted: false,
            TestData.Content("after"),
            rejectionReason: RejectionReason.RequestIdConflict,
            completionEvidence: TestData.Completion()));
    }

    [Test]
    public void EveryTerminalCarriesAnAfterView()
    {
        // guarantees.md §5.4: the after record-view ContentId is present in every E4.
        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Rejected,
            effectPermitted: false,
            afterView: default,
            rejectionReason: RejectionReason.RequestIdConflict));
    }

    [Test]
    public void CancelledRequiresConsistentCancellationEvidence()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Cancelled,
            effectPermitted: false,
            TestData.Content("after")));

        // Evidence permit flag must agree with the terminal's.
        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Cancelled,
            effectPermitted: false,
            TestData.Content("after"),
            cancellation: TestData.Cancellation(CancellationPhase.DuringEffect)));
    }

    [Test]
    public void CancellationEvidenceEnforcesPhaseFlagConsistency()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new CancellationEvidence(
            new LogicalOrder(1), new LogicalOrder(2), CancellationPhase.BeforeEffect, "Honored", true, true));
        AssertEx.Throws<ArgumentException>(() => _ = new CancellationEvidence(
            new LogicalOrder(1), new LogicalOrder(2), CancellationPhase.DuringEffect, "Honored", false, false));
        AssertEx.Throws<ArgumentException>(() => _ = new CancellationEvidence(
            new LogicalOrder(3), new LogicalOrder(2), CancellationPhase.BeforeEffect, "Honored", false, false));
    }

    [Test]
    public void PostconditionFaultCouplingIsEnforced()
    {
        // verification.md §3.4: Faulted(CompletionPostconditionNotSatisfied) carries
        // the stable False | TimedOut | Unknown detail — and that detail appears
        // with no other terminal.
        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Faulted,
            effectPermitted: true,
            TestData.Content("after"),
            faultCode: FaultCode.CompletionPostconditionNotSatisfied));

        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Faulted,
            effectPermitted: true,
            TestData.Content("after"),
            faultCode: FaultCode.CompletionPostconditionNotSatisfied,
            postcondition: PostconditionResult.Satisfied));

        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Succeeded,
            effectPermitted: true,
            TestData.Content("after"),
            completionEvidence: TestData.Completion(),
            postcondition: PostconditionResult.False));

        var postconditionFault = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Faulted,
            effectPermitted: true,
            TestData.Content("after"),
            faultCode: FaultCode.CompletionPostconditionNotSatisfied,
            postcondition: PostconditionResult.TimedOut);
        Assert.That(postconditionFault.Postcondition, Is.EqualTo(PostconditionResult.TimedOut));
    }

    [Test]
    public void DuplicateContinuationOrdinalsAreRejected()
    {
        // guarantees.md §5.8: replay binds by (ParentRequestId, ContinuationOrdinal).
        AssertEx.Throws<ArgumentException>(() => _ = new TerminalCut(
            new EvidenceSequence(1),
            TestData.Request("r1"),
            new LogicalOrder(1),
            InteractionOutcome.Succeeded,
            effectPermitted: true,
            TestData.Content("after"),
            completionEvidence: TestData.Completion(),
            continuations: ValueArray<ContinuationCommitment>.From(new[]
            {
                new ContinuationCommitment(0, TestData.Fingerprint("a")),
                new ContinuationCommitment(0, TestData.Fingerprint("b")),
            })));
    }

    [Test]
    public void CloseRejectsTheDefaultReason()
    {
        // A default RecordingCloseReason bypasses both factories; a malformed close
        // must be rejected at construction, never crash the reader later.
        AssertEx.Throws<ArgumentException>(() => _ = new RecordingClosed(
            new EvidenceSequence(2),
            default,
            declaredEventCount: 2,
            TestData.Content("final"),
            ValueArray<ContentId>.Empty));
        Assert.That(default(RecordingCloseReason).IsDefault, Is.True);
    }

    [Test]
    public void CloseReasonPairsIncompleteWithAReason()
    {
        AssertEx.Throws<ArgumentException>(() => RecordingCloseReason.Incomplete(default));
        AssertEx.Throws<InvalidOperationException>(() => _ = RecordingCloseReason.Completed.Reason);
        Assert.That(
            RecordingCloseReason.Incomplete(IncompleteReason.SinkFault).Reason,
            Is.EqualTo(IncompleteReason.SinkFault));
    }

    [Test]
    public void ContaminationIntervalCannotEndBeforeItBegins()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new ExternalMutationBarrier(
            new EvidenceSequence(9),
            new EvidenceSequence(5),
            new EvidenceSequence(4),
            new SourceRevision(1),
            "hint",
            ValueArray<RequestId>.Empty));
    }

    [Test]
    public void ClosedArtifactDeclaresAtLeastOpenAndCloseCuts()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new RecordingClosed(
            new EvidenceSequence(2),
            RecordingCloseReason.Completed,
            declaredEventCount: 1,
            TestData.Content("final"),
            ValueArray<ContentId>.Empty));
    }
}
