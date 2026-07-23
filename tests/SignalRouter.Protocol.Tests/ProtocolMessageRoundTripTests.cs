using System.Text;
using NUnit.Framework;
using SignalRouter;

namespace SignalRouter.Protocol.Tests;

public sealed class ProtocolMessageRoundTripTests
{
    private const string Epoch = "epoch-1";
    private const int Limit = ProtocolLimits.DefaultMaxReceiveMessageBytes;

    [Test]
    public void HelloRoundTripsEveryField()
    {
        var hello = new HelloMessage(
            "m-1",
            Epoch,
            "SignalRouter.Unity 0.1.0",
            new[] { "capability-b", "capability-a" },
            ProtocolLimits.BootstrapMaxMessageBytes,
            "token-value",
            new ProtocolVersion(1, 0));

        var decoded = (HelloMessage)RoundTrip(hello);

        Assert.That(decoded.Protocol, Is.EqualTo(hello.Protocol));
        Assert.That(decoded.MessageId, Is.EqualTo(hello.MessageId));
        Assert.That(decoded.Type, Is.EqualTo(hello.Type));
        Assert.That(decoded.SessionEpoch, Is.EqualTo(hello.SessionEpoch));
        Assert.That(decoded.RequestId, Is.Null);
        Assert.That(decoded.InReplyTo, Is.Null);
        Assert.That(decoded.PeerVersion, Is.EqualTo(hello.PeerVersion));
        Assert.That(decoded.Capabilities, Is.EqualTo(new[] { "capability-a", "capability-b" }));
        Assert.That(decoded.MaxReceiveMessageBytes, Is.EqualTo(hello.MaxReceiveMessageBytes));
        Assert.That(decoded.AuthToken, Is.EqualTo("token-value"));
    }

    [Test]
    public void HelloWithoutAnAuthTokenOmitsTheProperty()
    {
        var hello = new HelloMessage(
            "m-1",
            Epoch,
            "peer 1.0",
            Array.Empty<string>(),
            Limit);

        var encoded = Encoding.UTF8.GetString(ProtocolMessageWriter.Encode(hello, Limit));
        var decoded = (HelloMessage)RoundTrip(hello);

        Assert.That(encoded, Does.Not.Contain("authToken"));
        Assert.That(decoded.AuthToken, Is.Null);
    }

    [Test]
    public void WelcomeRoundTripsEveryField()
    {
        var welcome = new WelcomeMessage(
            "m-2",
            Epoch,
            "m-1",
            "SignalRouter.McpHost 0.1.0",
            new[] { "capability-a" },
            Limit,
            new ProtocolVersion(1, 0));

        var decoded = (WelcomeMessage)RoundTrip(welcome);

        Assert.That(decoded.InReplyTo, Is.EqualTo("m-1"));
        Assert.That(decoded.PeerVersion, Is.EqualTo(welcome.PeerVersion));
        Assert.That(decoded.Capabilities, Is.EqualTo(new[] { "capability-a" }));
        Assert.That(decoded.MaxReceiveMessageBytes, Is.EqualTo(Limit));
    }

    [Test]
    public void ErrorRoundTripsItsOptionalEnvelopeFields()
    {
        var error = new ErrorMessage(
            "m-3",
            ProtocolErrorCodes.ResultUnavailable,
            "The result is no longer retained.",
            Epoch,
            "r-1",
            "m-2");

        var decoded = (ErrorMessage)RoundTrip(error);

        Assert.That(decoded.Code, Is.EqualTo(ProtocolErrorCodes.ResultUnavailable));
        Assert.That(decoded.Message, Is.EqualTo(error.Message));
        Assert.That(decoded.SessionEpoch, Is.EqualTo(Epoch));
        Assert.That(decoded.RequestId, Is.EqualTo("r-1"));
        Assert.That(decoded.InReplyTo, Is.EqualTo("m-2"));
    }

    [Test]
    public void PingAndPongRoundTrip()
    {
        var ping = (PingMessage)RoundTrip(new PingMessage("m-4", Epoch));
        var pong = (PongMessage)RoundTrip(new PongMessage("m-5", "m-4", Epoch));

        Assert.That(ping.SessionEpoch, Is.EqualTo(Epoch));
        Assert.That(pong.InReplyTo, Is.EqualTo("m-4"));
    }

    [Test]
    public void ExecuteInteractionRoundTripsWithByteExactArguments()
    {
        var argumentsJson = "{\"value\":\"text\",\"nested\":{\"flag\":true},\"count\":3}";
        var execute = new ExecuteInteractionMessage(
            "m-6",
            Epoch,
            "r-1",
            "set_value",
            2,
            "target-1",
            argumentsJson,
            "correlation-1",
            "idempotency-1");

        var decoded = (ExecuteInteractionMessage)RoundTrip(execute);

        Assert.That(decoded.RequestId, Is.EqualTo("r-1"));
        Assert.That(decoded.CommandName, Is.EqualTo("set_value"));
        Assert.That(decoded.CommandVersion, Is.EqualTo(2));
        Assert.That(decoded.TargetId, Is.EqualTo("target-1"));
        Assert.That(decoded.ArgumentsJson, Is.EqualTo(argumentsJson));
        Assert.That(decoded.CorrelationId, Is.EqualTo("correlation-1"));
        Assert.That(decoded.IdempotencyKey, Is.EqualTo("idempotency-1"));
    }

    [Test]
    public void ExecuteInteractionWithoutOptionalFieldsRoundTrips()
    {
        var execute = new ExecuteInteractionMessage(
            "m-6",
            Epoch,
            "r-1",
            "click",
            1,
            "target-1",
            "{}");

        var decoded = (ExecuteInteractionMessage)RoundTrip(execute);

        Assert.That(decoded.CorrelationId, Is.Null);
        Assert.That(decoded.IdempotencyKey, Is.Null);
    }

    [Test]
    public void InteractionAcceptedRoundTrips()
    {
        var accepted = new InteractionAcceptedMessage("m-7", Epoch, "r-1", "m-6", 42);

        var decoded = (InteractionAcceptedMessage)RoundTrip(accepted);

        Assert.That(decoded.RequestId, Is.EqualTo("r-1"));
        Assert.That(decoded.InReplyTo, Is.EqualTo("m-6"));
        Assert.That(decoded.Sequence, Is.EqualTo(42));
    }

    [TestCase(InteractionStatus.Succeeded)]
    [TestCase(InteractionStatus.Rejected)]
    [TestCase(InteractionStatus.Faulted)]
    [TestCase(InteractionStatus.Cancelled)]
    public void InteractionResultRoundTripsEveryStatusShape(InteractionStatus status)
    {
        var message = new InteractionResultMessage(
            "m-8",
            Epoch,
            CreateOutcome(status),
            "m-6");

        var decoded = (InteractionResultMessage)RoundTrip(message);

        Assert.That(decoded.RequestId, Is.EqualTo(message.RequestId));
        Assert.That(decoded.InReplyTo, Is.EqualTo("m-6"));
        Assert.That(decoded.Result, Is.EqualTo(message.Result));
    }

    [Test]
    public void FaultedResultsWithoutAnApplicationCodeRoundTripAsNull()
    {
        var outcome = new ProtocolInteractionOutcome(
            5,
            "r-2",
            "target-1",
            "click",
            1,
            InteractionOrigin.Agent,
            InteractionStatus.Faulted,
            new[] { new InteractionStageProgress("apply", 0, InteractionStageStatus.Faulted) },
            null,
            null,
            StateObservation.Empty,
            StateObservation.Empty);
        var message = new InteractionResultMessage("m-8", Epoch, outcome);

        var decoded = (InteractionResultMessage)RoundTrip(message);

        Assert.That(decoded.Result.FaultCode, Is.Null);
        Assert.That(decoded.Result, Is.EqualTo(outcome));
    }

    [Test]
    public void QueryCancelAndSnapshotRequestsRoundTrip()
    {
        var query = (GetInteractionResultMessage)RoundTrip(
            new GetInteractionResultMessage("m-9", Epoch, "r-1"));
        var cancel = (CancelInteractionMessage)RoundTrip(
            new CancelInteractionMessage("m-10", Epoch, "r-1"));
        var snapshotRequest = (GetRegistrySnapshotMessage)RoundTrip(
            new GetRegistrySnapshotMessage("m-11", Epoch));

        Assert.That(query.RequestId, Is.EqualTo("r-1"));
        Assert.That(cancel.RequestId, Is.EqualTo("r-1"));
        Assert.That(snapshotRequest.SessionEpoch, Is.EqualTo(Epoch));
    }

    [Test]
    public void RegistrySnapshotRoundTripsWithByteExactSnapshot()
    {
        var snapshotJson = "{\"sessionEpoch\":\"epoch-1\",\"revision\":7,\"targets\":[{\"id\":\"a\"}]}";
        var snapshot = new RegistrySnapshotMessage("m-12", Epoch, "m-11", 3, snapshotJson);

        var decoded = (RegistrySnapshotMessage)RoundTrip(snapshot);

        Assert.That(decoded.ProbeVersion, Is.EqualTo(3));
        Assert.That(decoded.SnapshotJson, Is.EqualTo(snapshotJson));
        Assert.That(decoded.InReplyTo, Is.EqualTo("m-11"));
    }

    [Test]
    public void SanitizedResultBytesNeverCarryExceptionOrRejectionDetail()
    {
        var faulted = new InteractionResult(
            1,
            "r-1",
            "target-1",
            "click",
            1,
            InteractionOrigin.Agent,
            InteractionStatus.Faulted,
            null,
            new FaultInfo(
                "System.InvalidOperationException",
                "Secret failure detail.",
                "at Secret.Frame()",
                "app-code",
                "apply",
                0,
                Array.Empty<string>()),
            new StageProgress(new[]
            {
                new InteractionStageProgress("apply", 0, InteractionStageStatus.Faulted),
            }),
            StateObservation.Empty,
            StateObservation.Empty,
            StateDiff.Empty);
        var rejected = new InteractionResult(
            2,
            "r-2",
            "target-1",
            "click",
            1,
            InteractionOrigin.Agent,
            InteractionStatus.Rejected,
            new RejectionInfo(InteractionRejectionCode.Disabled, "Secret rejection detail."),
            null,
            StageProgress.Empty,
            StateObservation.Empty,
            StateObservation.Empty,
            StateDiff.Empty);

        var faultedBytes = Encoding.UTF8.GetString(ProtocolMessageWriter.Encode(
            new InteractionResultMessage(
                "m-1",
                Epoch,
                ProtocolInteractionOutcome.FromResult(faulted)),
            Limit));
        var rejectedBytes = Encoding.UTF8.GetString(ProtocolMessageWriter.Encode(
            new InteractionResultMessage(
                "m-2",
                Epoch,
                ProtocolInteractionOutcome.FromResult(rejected)),
            Limit));

        Assert.That(faultedBytes, Does.Contain("app-code"));
        Assert.That(faultedBytes, Does.Not.Contain("Secret failure detail"));
        Assert.That(faultedBytes, Does.Not.Contain("InvalidOperationException"));
        Assert.That(faultedBytes, Does.Not.Contain("Secret.Frame"));
        Assert.That(rejectedBytes, Does.Contain("Disabled"));
        Assert.That(rejectedBytes, Does.Not.Contain("Secret rejection detail"));
    }

    private static ProtocolMessage RoundTrip(ProtocolMessage message)
    {
        var encoded = ProtocolMessageWriter.Encode(message, Limit);
        var result = ProtocolMessageReader.Read(encoded, Limit);

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Success), result.ErrorMessage);
        Assert.That(result.Message!.Protocol, Is.EqualTo(message.Protocol));
        return result.Message;
    }

    private static ProtocolInteractionOutcome CreateOutcome(InteractionStatus status)
    {
        var observation = new StateObservation(
            new[] { new StateProbeObservation("probe-a", "hash-1") });
        switch (status)
        {
            case InteractionStatus.Succeeded:
                return new ProtocolInteractionOutcome(
                    1,
                    "r-1",
                    "target-1",
                    "click",
                    1,
                    InteractionOrigin.Agent,
                    InteractionStatus.Succeeded,
                    new[]
                    {
                        new InteractionStageProgress("focus", 0, InteractionStageStatus.Completed),
                        new InteractionStageProgress("apply", 1, InteractionStageStatus.Completed),
                    },
                    null,
                    null,
                    observation,
                    observation);
            case InteractionStatus.Rejected:
                return new ProtocolInteractionOutcome(
                    2,
                    "r-1",
                    "target-1",
                    "click",
                    1,
                    InteractionOrigin.Agent,
                    InteractionStatus.Rejected,
                    Array.Empty<InteractionStageProgress>(),
                    InteractionRejectionCode.TargetNotFound,
                    null,
                    StateObservation.Empty,
                    StateObservation.Empty);
            case InteractionStatus.Faulted:
                return new ProtocolInteractionOutcome(
                    3,
                    "r-1",
                    "target-1",
                    "click",
                    1,
                    InteractionOrigin.Agent,
                    InteractionStatus.Faulted,
                    new[]
                    {
                        new InteractionStageProgress("apply", 0, InteractionStageStatus.Faulted),
                    },
                    null,
                    "app-code",
                    observation,
                    observation);
            default:
                return new ProtocolInteractionOutcome(
                    4,
                    "r-1",
                    "target-1",
                    "click",
                    1,
                    InteractionOrigin.Agent,
                    InteractionStatus.Cancelled,
                    Array.Empty<InteractionStageProgress>(),
                    null,
                    null,
                    StateObservation.Empty,
                    StateObservation.Empty);
        }
    }
}
