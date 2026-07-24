using System.Text;
using NUnit.Framework;

namespace SignalRouter.Protocol.Tests;

public sealed class ProtocolMessageModelTests
{
    private const string Epoch = "epoch-1";

    [Test]
    public void HelloValidatesItsEnvelopeAndPayload()
    {
        var hello = new HelloMessage(
            "m-1",
            Epoch,
            "SignalRouter.Unity 0.1.0",
            new[] { "b", "a" },
            ProtocolLimits.DefaultMaxReceiveMessageBytes,
            "token-value");

        Assert.That(hello.Type, Is.EqualTo(ProtocolMessageTypes.Hello));
        Assert.That(hello.Protocol, Is.EqualTo(ProtocolVersion.Current));
        Assert.That(hello.MessageId, Is.EqualTo("m-1"));
        Assert.That(hello.SessionEpoch, Is.EqualTo(Epoch));
        Assert.That(hello.RequestId, Is.Null);
        Assert.That(hello.InReplyTo, Is.Null);
        Assert.That(hello.Capabilities, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(hello.AuthToken, Is.EqualTo("token-value"));
    }

    [Test]
    public void HelloAcceptsAnExplicitProtocolVersionForNegotiationTests()
    {
        var hello = CreateHello(protocol: new ProtocolVersion(1, 7));

        Assert.That(hello.Protocol, Is.EqualTo(new ProtocolVersion(1, 7)));
    }

    [TestCase("")]
    [TestCase(" padded ")]
    [TestCase("line\nbreak")]
    public void MessageIdsMustBeWireSafeIdentifiers(string messageId)
    {
        NUnitCompat.Throws<ArgumentException>(() => _ = new PingMessage(messageId));
    }

    [Test]
    public void IdentifiersAreLengthCapped()
    {
        var oversized = new string('x', ProtocolLimits.MaxIdentifierChars + 1);

        NUnitCompat.Throws<ArgumentException>(() => _ = new PingMessage(oversized));
        NUnitCompat.ThatThrows(
            () => _ = new PingMessage(new string('x', ProtocolLimits.MaxIdentifierChars)),
            Throws.Nothing);
    }

    [Test]
    public void HelloRequiresASessionEpoch()
    {
        NUnitCompat.Throws<ArgumentNullException>(() => _ = new HelloMessage(
            "m-1",
            null!,
            "peer 1.0",
            Array.Empty<string>(),
            ProtocolLimits.DefaultMaxReceiveMessageBytes));
    }

    [Test]
    public void HelloRejectsDuplicateAndOversizedCapabilities()
    {
        NUnitCompat.Throws<ArgumentException>(() => CreateHello(capabilities: new[] { "a", "a" }));
        NUnitCompat.Throws<ArgumentException>(() => CreateHello(
            capabilities: new[] { new string('c', ProtocolLimits.MaxCapabilityChars + 1) }));
        NUnitCompat.Throws<ArgumentException>(() => CreateHello(
            capabilities: BuildCapabilities(ProtocolLimits.MaxCapabilities + 1)));
        NUnitCompat.ThatThrows(
            () => CreateHello(capabilities: BuildCapabilities(ProtocolLimits.MaxCapabilities)),
            Throws.Nothing);
    }

    [Test]
    public void ReceiveLimitsBelowTheBootstrapSizeAreContractViolations()
    {
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => CreateHello(
            maxReceiveMessageBytes: ProtocolLimits.BootstrapMaxMessageBytes - 1));
        NUnitCompat.ThatThrows(
            () => CreateHello(maxReceiveMessageBytes: ProtocolLimits.BootstrapMaxMessageBytes),
            Throws.Nothing);
    }

    [Test]
    public void WelcomeRequiresTheHelloItAnswers()
    {
        var welcome = new WelcomeMessage(
            "m-2",
            Epoch,
            "m-1",
            "SignalRouter.McpHost 0.1.0",
            Array.Empty<string>(),
            ProtocolLimits.DefaultMaxReceiveMessageBytes);

        Assert.That(welcome.InReplyTo, Is.EqualTo("m-1"));
        NUnitCompat.Throws<ArgumentNullException>(() => _ = new WelcomeMessage(
            "m-2",
            Epoch,
            null!,
            "SignalRouter.McpHost 0.1.0",
            Array.Empty<string>(),
            ProtocolLimits.DefaultMaxReceiveMessageBytes));
    }

    [Test]
    public void ErrorMessagesCarryBoundedSingleLineText()
    {
        var error = new ErrorMessage(
            "m-3",
            ProtocolErrorCodes.MalformedMessage,
            "The envelope is not a JSON object.");

        Assert.That(error.Code, Is.EqualTo(ProtocolErrorCodes.MalformedMessage));
        NUnitCompat.Throws<ArgumentException>(() => _ = new ErrorMessage(
            "m-3",
            ProtocolErrorCodes.MalformedMessage,
            "line\nbreak"));
        NUnitCompat.Throws<ArgumentException>(() => _ = new ErrorMessage(
            "m-3",
            ProtocolErrorCodes.MalformedMessage,
            new string('x', ProtocolLimits.MaxErrorMessageChars + 1)));
        NUnitCompat.Throws<ArgumentException>(() => _ = new ErrorMessage(
            "m-3",
            "bad\tcode",
            "message"));
    }

    [Test]
    public void PongRequiresThePingItAnswers()
    {
        Assert.That(new PongMessage("m-5", "m-4").InReplyTo, Is.EqualTo("m-4"));
        NUnitCompat.Throws<ArgumentNullException>(() => _ = new PongMessage("m-5", null!));
    }

    [Test]
    public void ExecuteInteractionValidatesItsCommandFields()
    {
        var execute = CreateExecute();

        Assert.That(execute.RequestId, Is.EqualTo("r-1"));
        Assert.That(execute.SessionEpoch, Is.EqualTo(Epoch));
        Assert.That(execute.ArgumentsJson, Is.EqualTo("{\"value\":\"text\"}"));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => CreateExecute(commandVersion: 0));
        NUnitCompat.Throws<ArgumentNullException>(() => CreateExecute(requestId: null));
        NUnitCompat.Throws<ArgumentNullException>(() => CreateExecute(sessionEpoch: null));
    }

    [TestCase("[]")]
    [TestCase("\"text\"")]
    [TestCase("not json")]
    [TestCase("{\"a\":1} trailing")]
    public void ExecuteInteractionArgumentsMustBeAStandaloneJsonObject(string argumentsJson)
    {
        NUnitCompat.Throws<ArgumentException>(() => CreateExecute(argumentsJson: argumentsJson));
    }

    [Test]
    public void ExecuteInteractionArgumentsHonorTheDepthBudget()
    {
        NUnitCompat.ThatThrows(
            () => CreateExecute(argumentsJson: BuildNestedObject(61)),
            Throws.Nothing);
        NUnitCompat.Throws<ArgumentException>(
            () => CreateExecute(argumentsJson: BuildNestedObject(62)));
    }

    [Test]
    public void InteractionAcceptedRequiresAPositiveSequence()
    {
        var accepted = new InteractionAcceptedMessage("m-6", Epoch, "r-1", "m-5", 7);

        Assert.That(accepted.Sequence, Is.EqualTo(7));
        Assert.That(accepted.RequestId, Is.EqualTo("r-1"));
        Assert.That(accepted.InReplyTo, Is.EqualTo("m-5"));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(
            () => _ = new InteractionAcceptedMessage("m-6", Epoch, "r-1", "m-5", 0));
    }

    [Test]
    public void HelloRequiresAPositiveRecoveryWindow()
    {
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new HelloMessage(
            "m-1",
            Epoch,
            "peer 1.0",
            Array.Empty<string>(),
            ProtocolLimits.DefaultMaxReceiveMessageBytes,
            null,
            recoveryWindowMs: 0));
    }

    [Test]
    public void InteractionStatusValidatesItsStateShape()
    {
        NUnitCompat.Throws<ArgumentException>(() => _ = new InteractionStatusMessage(
            "m-1",
            Epoch,
            "r-1",
            "m-0",
            ProtocolRequestState.Terminal,
            1,
            false));
        NUnitCompat.Throws<ArgumentException>(() => _ = new InteractionStatusMessage(
            "m-1",
            Epoch,
            "r-1",
            "m-0",
            ProtocolRequestState.Received,
            1,
            false));
        NUnitCompat.Throws<ArgumentException>(() => _ = new InteractionStatusMessage(
            "m-1",
            Epoch,
            "r-1",
            "m-0",
            ProtocolRequestState.Queued,
            null,
            false));
        NUnitCompat.Throws<ArgumentNullException>(() => _ = new InteractionStatusMessage(
            "m-1",
            Epoch,
            "r-1",
            null!,
            ProtocolRequestState.Received,
            null,
            false));
    }

    [Test]
    public void InteractionResultStampsTheEnvelopeRequestIdFromTheOutcome()
    {
        var outcome = ProtocolInteractionOutcomeTests.CreateSucceededOutcome("r-9");
        var message = new InteractionResultMessage("m-7", Epoch, outcome);

        Assert.That(message.RequestId, Is.EqualTo("r-9"));
        Assert.That(message.Result, Is.SameAs(outcome));
        NUnitCompat.Throws<ArgumentNullException>(
            () => _ = new InteractionResultMessage("m-7", Epoch, null!));
    }

    [Test]
    public void QueryAndCancelMessagesRequireSessionEpochAndRequestId()
    {
        Assert.That(
            new GetInteractionResultMessage("m-8", Epoch, "r-1").RequestId,
            Is.EqualTo("r-1"));
        Assert.That(
            new CancelInteractionMessage("m-9", Epoch, "r-1").RequestId,
            Is.EqualTo("r-1"));
        NUnitCompat.Throws<ArgumentNullException>(
            () => _ = new GetInteractionResultMessage("m-8", null!, "r-1"));
        NUnitCompat.Throws<ArgumentNullException>(
            () => _ = new CancelInteractionMessage("m-9", Epoch, null!));
    }

    [Test]
    public void WaitForValidatesItsConditionShape()
    {
        NUnitCompat.Throws<ArgumentException>(() => _ = new WaitForMessage(
            "m-1", Epoch, "future_condition", null, 1000));
        NUnitCompat.Throws<ArgumentException>(() => _ = new WaitForMessage(
            "m-1", Epoch, ProtocolWaitConditions.Idle, "target-1", 1000));
        NUnitCompat.Throws<ArgumentException>(() => _ = new WaitForMessage(
            "m-1", Epoch, ProtocolWaitConditions.TargetPresent, null, 1000));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new WaitForMessage(
            "m-1", Epoch, ProtocolWaitConditions.Idle, null, 0));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new WaitForMessage(
            "m-1",
            Epoch,
            ProtocolWaitConditions.Idle,
            null,
            ProtocolLimits.MaxWaitTimeoutMs + 1));
    }

    [Test]
    public void RecordingMessagesValidateHandlesAndOutcomes()
    {
        NUnitCompat.Throws<ArgumentException>(() => _ = new RecordingStartedMessage(
            "m-1", Epoch, "op-1", "bad handle", Epoch));
        NUnitCompat.Throws<ArgumentException>(() => _ = new ReplayRecordingMessage(
            "m-1", Epoch, "op-1", "../escape"));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new RecordingStoppedMessage(
            "m-1", Epoch, "op-1", "rec-0", -1, Epoch));
        NUnitCompat.Throws<ArgumentException>(() => _ = new ReplayReportMessage(
            "m-1", Epoch, "op-1", "future_outcome", Epoch));
        NUnitCompat.Throws<ArgumentException>(() => _ = new StartRecordingMessage(
            "m-1", Epoch, "op-1", new string('x', ProtocolLimits.MaxLabelChars + 1)));
    }

    [Test]
    public void RecordingAcknowledgmentsRejectAContradictoryEpoch()
    {
        // The new-epoch payload must equal the envelope epoch the message
        // arrives on; a mismatch is a contradictory acknowledgment.
        NUnitCompat.Throws<ArgumentException>(() => _ = new RecordingStartedMessage(
            "m-1", Epoch, "op-1", "rec-0", "epoch-other"));
        NUnitCompat.Throws<ArgumentException>(() => _ = new RecordingStoppedMessage(
            "m-1", Epoch, "op-1", "rec-0", 0, "epoch-other"));
        NUnitCompat.Throws<ArgumentException>(() => _ = new ReplayReportMessage(
            "m-1", Epoch, "op-1", ProtocolReplayOutcomes.Completed, "epoch-other"));
    }

    [Test]
    public void ControlOperationResultValidatesStateEpochAndPayload()
    {
        // Unknown state.
        NUnitCompat.Throws<ArgumentException>(() => _ = new ControlOperationResultMessage(
            "m-1", Epoch, "op-1", "future_state", Epoch));
        // Contradictory epoch.
        NUnitCompat.Throws<ArgumentException>(() => _ = new ControlOperationResultMessage(
            "m-1", Epoch, "op-1", ProtocolControlOperationStates.Completed, "epoch-other"));
        // A non-terminal state must carry no result payload.
        NUnitCompat.Throws<ArgumentException>(() => _ = new ControlOperationResultMessage(
            "m-1", Epoch, "op-1", ProtocolControlOperationStates.Pending, Epoch,
            recordingHandle: "rec-0"));
        // Malformed handle / negative count / unknown outcome are still rejected
        // when present on a terminal result.
        NUnitCompat.Throws<ArgumentException>(() => _ = new ControlOperationResultMessage(
            "m-1", Epoch, "op-1", ProtocolControlOperationStates.Completed, Epoch,
            recordingHandle: "../escape"));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new ControlOperationResultMessage(
            "m-1", Epoch, "op-1", ProtocolControlOperationStates.Completed, Epoch,
            entryCount: -1));
        NUnitCompat.Throws<ArgumentException>(() => _ = new ControlOperationResultMessage(
            "m-1", Epoch, "op-1", ProtocolControlOperationStates.Completed, Epoch,
            outcomeKind: "future_outcome"));
    }

    [Test]
    public void RegistrySnapshotValidatesItsPayload()
    {
        var snapshot = new RegistrySnapshotMessage(
            "m-11",
            Epoch,
            "m-10",
            1,
            "{\"targets\":[]}");

        Assert.That(snapshot.ProbeVersion, Is.EqualTo(1));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => _ = new RegistrySnapshotMessage(
            "m-11",
            Epoch,
            "m-10",
            0,
            "{}"));
        NUnitCompat.Throws<ArgumentException>(() => _ = new RegistrySnapshotMessage(
            "m-11",
            Epoch,
            "m-10",
            1,
            "[]"));
        NUnitCompat.ThatThrows(
            () => _ = new RegistrySnapshotMessage(
                "m-11",
                Epoch,
                "m-10",
                1,
                BuildNestedObject(62)),
            Throws.Nothing);
        NUnitCompat.Throws<ArgumentException>(() => _ = new RegistrySnapshotMessage(
            "m-11",
            Epoch,
            "m-10",
            1,
            BuildNestedObject(63)));
    }

    private static HelloMessage CreateHello(
        IEnumerable<string>? capabilities = null,
        int maxReceiveMessageBytes = ProtocolLimits.DefaultMaxReceiveMessageBytes,
        ProtocolVersion? protocol = null)
    {
        return new HelloMessage(
            "m-1",
            Epoch,
            "peer 1.0",
            capabilities ?? Array.Empty<string>(),
            maxReceiveMessageBytes,
            null,
            protocol: protocol);
    }

    private static ExecuteInteractionMessage CreateExecute(
        string? sessionEpoch = Epoch,
        string? requestId = "r-1",
        int commandVersion = 1,
        string argumentsJson = "{\"value\":\"text\"}")
    {
        return new ExecuteInteractionMessage(
            "m-5",
            sessionEpoch!,
            requestId!,
            "set_value",
            commandVersion,
            "target-1",
            argumentsJson);
    }

    private static string[] BuildCapabilities(int count)
    {
        var capabilities = new string[count];
        for (var index = 0; index < count; index++)
        {
            capabilities[index] = "capability-" + index;
        }

        return capabilities;
    }

    internal static string BuildNestedObject(int depth)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < depth - 1; index++)
        {
            builder.Append("{\"a\":");
        }

        builder.Append("{}");
        for (var index = 0; index < depth - 1; index++)
        {
            builder.Append('}');
        }

        return builder.ToString();
    }
}
