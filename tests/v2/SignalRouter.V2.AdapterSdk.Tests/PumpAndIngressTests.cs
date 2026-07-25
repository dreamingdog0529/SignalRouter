using System;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.AdapterSdk.Tests;

/// <summary>
/// kernel-execution.md §3–§6 — pump inputs/report invariants and the ingress
/// message shapes.
/// </summary>
public sealed class PumpAndIngressTests
{
    [Test]
    public void PumpBudgetRequiresTurnsAndAPhase()
    {
        var budget = new PumpBudget(8, deadline: 1000, new LogicalTime(50), FramePhase.Update);
        Assert.That(budget.MaxTurns, Is.EqualTo(8));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new PumpBudget(
            0, 1000, new LogicalTime(50), FramePhase.Update));
        AssertEx.Throws<ArgumentException>(() => _ = new PumpBudget(
            8, 1000, new LogicalTime(50), default));
    }

    [Test]
    public void PumpReportsRejectNegativeCountsAndDefaultAwaitedPhases()
    {
        var report = new PumpReport(
            turnsExecuted: 3,
            workRemaining: true,
            controlQueueDepth: 1,
            sourcePublicationQueueDepth: 0,
            mutationQueueDepth: 2,
            awaitingAdapterCompletion: true,
            awaitingFramePhase: FramePhase.LateUpdate);
        Assert.That(report.AwaitingFramePhase, Is.EqualTo(FramePhase.LateUpdate));

        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new PumpReport(
            -1, false, 0, 0, 0, false, null));
        AssertEx.Throws<ArgumentException>(() => _ = new PumpReport(
            0, false, 0, 0, 0, false, default(FramePhase)));
    }

    [Test]
    public void LogicalTimeIsComparable()
    {
        Assert.That(new LogicalTime(1), Is.LessThan(new LogicalTime(2)));
        Assert.That(new LogicalTime(2) >= new LogicalTime(2), Is.True);
    }

    [Test]
    public void SubmissionsCarryPayloadAndEnvelope()
    {
        var submission = new IntentSubmission(
            SdkTestData.Request("r1"),
            SdkTestData.Invocation,
            InvocationPayload.Empty,
            SdkTestData.Envelope,
            observer: null);
        Assert.That(submission.Observer, Is.Null);
        AssertEx.Throws<ArgumentException>(() => _ = new IntentSubmission(
            default, SdkTestData.Invocation, InvocationPayload.Empty, SdkTestData.Envelope, null));
    }

    [Test]
    public void SourceDocumentsHaveUniqueFieldsAndPublicationsCarryCausation()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new SourceDocument(
            ValueList<NamedField>.From(new[]
            {
                new NamedField("count", FieldValue.Of(1L)),
                new NamedField("count", FieldValue.Of(2L)),
            })));

        var publication = new SourcePublication(
            new StateSourceKey("inventory"),
            new SourceDocument(ValueList<NamedField>.From(new[]
            {
                new NamedField("count", FieldValue.Of(1L)),
            })),
            EventCausation.OfRequest(SdkTestData.Request("r1")));
        Assert.That(publication.Causation.Kind, Is.EqualTo(EventCausationKind.Request));
    }

    [Test]
    public void ObservedExternalReportsRequireASourceHint()
    {
        var report = new ObservedExternalReport("native-toggle", SdkTestData.Node(4), null);
        Assert.That(report.AuthorKey, Is.Null);
        AssertEx.Throws<ArgumentNullException>(() => _ = new ObservedExternalReport(null!, null, null));
    }

    [Test]
    public void RegistrationReceiptsCarryStableCodesOnly()
    {
        var success = RegistrationReceipt.Success(SdkTestData.Node(1));
        Assert.That(success.Succeeded, Is.True);
        Assert.That(success.FailureCode, Is.Null);

        var failure = RegistrationReceipt.Failure("DuplicateAuthorKey");
        Assert.That(failure.Succeeded, Is.False);
        Assert.That(failure.Node, Is.Null);
        AssertEx.Throws<ArgumentException>(() => RegistrationReceipt.Failure("free text"));
    }
}
