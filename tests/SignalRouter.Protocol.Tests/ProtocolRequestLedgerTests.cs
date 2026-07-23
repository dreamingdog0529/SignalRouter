using NUnit.Framework;
using SignalRouter;

namespace SignalRouter.Protocol.Tests;

public sealed class ProtocolRequestLedgerTests
{
    private const string Epoch = "epoch-1";
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);

    [Test]
    public void AFirstSubmissionIsAdmittedAsReceived()
    {
        var ledger = CreateLedger();

        var submission = ledger.Submit(CreateExecute("r-1"));

        Assert.That(submission.Status, Is.EqualTo(ProtocolLedgerSubmissionStatus.Admitted));
        Assert.That(submission.Entry!.RequestId, Is.EqualTo("r-1"));
        Assert.That(submission.Entry.State, Is.EqualTo(ProtocolRequestState.Received));
        Assert.That(submission.Entry.Sequence, Is.Null);
        Assert.That(ledger.Count, Is.EqualTo(1));
    }

    [Test]
    public void AByteExactResendIsAnsweredFromTheLedgerWithoutDispatching()
    {
        var ledger = CreateLedger();
        ledger.Submit(CreateExecute("r-1"));
        ledger.MarkQueued("r-1", 3);

        var resend = ledger.Submit(CreateExecute("r-1"));

        Assert.That(resend.Status, Is.EqualTo(ProtocolLedgerSubmissionStatus.Duplicate));
        Assert.That(resend.Entry!.State, Is.EqualTo(ProtocolRequestState.Queued));
        Assert.That(resend.Entry.Sequence, Is.EqualTo(3));
        Assert.That(ledger.Count, Is.EqualTo(1));
    }

    [Test]
    public void AResendAfterCompletionCarriesTheTerminalOutcome()
    {
        var ledger = CreateLedger();
        ledger.Submit(CreateExecute("r-1"));
        AdvanceToTerminal(ledger, "r-1");

        var resend = ledger.Submit(CreateExecute("r-1"));

        Assert.That(resend.Status, Is.EqualTo(ProtocolLedgerSubmissionStatus.Duplicate));
        Assert.That(resend.Entry!.State, Is.EqualTo(ProtocolRequestState.Terminal));
        Assert.That(resend.Entry.Outcome!.RequestId, Is.EqualTo("r-1"));
    }

    [Test]
    public void ReusingARequestIdWithDifferentContentIsAConflict()
    {
        var ledger = CreateLedger();
        ledger.Submit(CreateExecute("r-1"));

        var conflict = ledger.Submit(CreateExecute("r-1", argumentsJson: "{\"other\":true}"));

        Assert.That(conflict.Status, Is.EqualTo(ProtocolLedgerSubmissionStatus.Conflict));
        Assert.That(conflict.ErrorCode, Is.EqualTo(ProtocolErrorCodes.RequestIdConflict));
        Assert.That(conflict.Entry, Is.Null);
    }

    [Test]
    public void StatesOnlyMoveForward()
    {
        var ledger = CreateLedger();
        ledger.Submit(CreateExecute("r-1"));
        ledger.MarkQueued("r-1", 1);
        ledger.MarkRunning("r-1");

        NUnitCompat.Throws<InvalidOperationException>(() => ledger.MarkQueued("r-1", 2));
        NUnitCompat.Throws<InvalidOperationException>(() => ledger.MarkRunning("r-1"));
        ledger.MarkTerminal(
            "r-1",
            ProtocolInteractionOutcomeTests.CreateSucceededOutcome("r-1"));
        NUnitCompat.Throws<InvalidOperationException>(() => ledger.MarkTerminal(
            "r-1",
            ProtocolInteractionOutcomeTests.CreateSucceededOutcome("r-1")));
    }

    [Test]
    public void ARejectionMayCompleteWithoutEverBeingQueued()
    {
        var ledger = CreateLedger();
        ledger.Submit(CreateExecute("r-1"));

        ledger.MarkTerminal(
            "r-1",
            ProtocolInteractionOutcomeTests.CreateSucceededOutcome("r-1"));

        Assert.That(ledger.TryGet("r-1")!.State, Is.EqualTo(ProtocolRequestState.Terminal));
    }

    [Test]
    public void TerminalOutcomesMustBelongToTheirRequest()
    {
        var ledger = CreateLedger();
        ledger.Submit(CreateExecute("r-1"));

        NUnitCompat.Throws<ArgumentException>(() => ledger.MarkTerminal(
            "r-1",
            ProtocolInteractionOutcomeTests.CreateSucceededOutcome("r-2")));
    }

    [Test]
    public void TerminalOutcomesMustMatchTheQueuedSequence()
    {
        var ledger = CreateLedger();
        ledger.Submit(CreateExecute("r-1"));
        ledger.MarkQueued("r-1", 2);

        NUnitCompat.Throws<ArgumentException>(() => ledger.MarkTerminal(
            "r-1",
            ProtocolInteractionOutcomeTests.CreateSucceededOutcome("r-1")));
    }

    [Test]
    public void UntrackedRequestsFailFast()
    {
        var ledger = CreateLedger();

        NUnitCompat.Throws<InvalidOperationException>(() => ledger.MarkQueued("r-9", 1));
        NUnitCompat.Throws<InvalidOperationException>(() => ledger.MarkRunning("r-9"));
    }

    [Test]
    public void TerminalResultsExpireAfterTheRetentionWindow()
    {
        var clock = new FakeClock();
        var ledger = CreateLedger(clock: clock);
        ledger.Submit(CreateExecute("r-1"));
        AdvanceToTerminal(ledger, "r-1");

        clock.Advance(Retention + TimeSpan.FromSeconds(1));

        Assert.That(ledger.TryGet("r-1"), Is.Null);
        Assert.That(ledger.Count, Is.EqualTo(0));
    }

    [Test]
    public void ResultsWithinTheRetentionWindowSurvive()
    {
        var clock = new FakeClock();
        var ledger = CreateLedger(clock: clock);
        ledger.Submit(CreateExecute("r-1"));
        AdvanceToTerminal(ledger, "r-1");

        clock.Advance(Retention - TimeSpan.FromSeconds(1));

        Assert.That(ledger.TryGet("r-1"), Is.Not.Null);
    }

    [Test]
    public void PendingRequestsAreNeverEvicted()
    {
        var clock = new FakeClock();
        var ledger = CreateLedger(clock: clock);
        ledger.Submit(CreateExecute("r-1"));
        ledger.MarkQueued("r-1", 1);

        clock.Advance(TimeSpan.FromDays(365));

        Assert.That(ledger.TryGet("r-1"), Is.Not.Null);
        Assert.That(ledger.TryGet("r-1")!.State, Is.EqualTo(ProtocolRequestState.Queued));
    }

    [Test]
    public void AFullLedgerRefusesNewWorkInsteadOfForgettingOldWork()
    {
        var ledger = CreateLedger(capacity: 2);
        ledger.Submit(CreateExecute("r-1"));
        ledger.Submit(CreateExecute("r-2"));

        var refused = ledger.Submit(CreateExecute("r-3"));
        var resend = ledger.Submit(CreateExecute("r-1"));

        Assert.That(refused.Status, Is.EqualTo(ProtocolLedgerSubmissionStatus.CapacityExhausted));
        Assert.That(refused.ErrorCode, Is.EqualTo(ProtocolErrorCodes.CapacityExhausted));
        Assert.That(resend.Status, Is.EqualTo(ProtocolLedgerSubmissionStatus.Duplicate));
    }

    [Test]
    public void ExpiredResultsFreeCapacityForNewWork()
    {
        var clock = new FakeClock();
        var ledger = CreateLedger(capacity: 1, clock: clock);
        ledger.Submit(CreateExecute("r-1"));
        AdvanceToTerminal(ledger, "r-1");
        clock.Advance(Retention + TimeSpan.FromSeconds(1));

        var admitted = ledger.Submit(CreateExecute("r-2"));

        Assert.That(admitted.Status, Is.EqualTo(ProtocolLedgerSubmissionStatus.Admitted));
        Assert.That(ledger.TryGet("r-1"), Is.Null);
    }

    [Test]
    public void RequestsFromAForeignEpochAreALocalWiringBug()
    {
        var ledger = CreateLedger();

        NUnitCompat.Throws<ArgumentException>(() => ledger.Submit(new ExecuteInteractionMessage(
            "m-1",
            "epoch-2",
            "r-1",
            "click",
            1,
            "target-1",
            "{}")));
    }

    [Test]
    public void ConstructionValidatesItsBounds()
    {
        NUnitCompat.Throws<ArgumentOutOfRangeException>(
            () => _ = new ProtocolRequestLedger(Epoch, 0, Retention, new FakeClock()));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(
            () => _ = new ProtocolRequestLedger(Epoch, 1, TimeSpan.Zero, new FakeClock()));
        NUnitCompat.Throws<ArgumentNullException>(
            () => _ = new ProtocolRequestLedger(Epoch, 1, Retention, null!));
    }

    private static ProtocolRequestLedger CreateLedger(
        int capacity = 16,
        FakeClock? clock = null)
    {
        return new ProtocolRequestLedger(Epoch, capacity, Retention, clock ?? new FakeClock());
    }

    private static void AdvanceToTerminal(ProtocolRequestLedger ledger, string requestId)
    {
        ledger.MarkQueued(requestId, 1);
        ledger.MarkRunning(requestId);
        ledger.MarkTerminal(
            requestId,
            ProtocolInteractionOutcomeTests.CreateSucceededOutcome(requestId));
    }

    private static ExecuteInteractionMessage CreateExecute(
        string requestId,
        string argumentsJson = "{}")
    {
        return new ExecuteInteractionMessage(
            "m-" + requestId,
            Epoch,
            requestId,
            "click",
            1,
            "target-1",
            argumentsJson);
    }

    private sealed class FakeClock : IInteractionClock
    {
        private DateTimeOffset now = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow
        {
            get { return now; }
        }

        public void Advance(TimeSpan delta)
        {
            now += delta;
        }
    }
}
