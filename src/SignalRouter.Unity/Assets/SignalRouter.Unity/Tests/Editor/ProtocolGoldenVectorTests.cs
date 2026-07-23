using System;
using System.Text;
using NUnit.Framework;
using SignalRouter.Protocol;

namespace SignalRouter.Tests;

// Runs the protocol codecs against the System.Text.Json build Unity actually
// bundles (8.0, resolved from the precompiled reference), so a behavioral
// difference between the net10.0 test runtime and the Unity player surface
// cannot hide behind the pure .NET suite. The golden envelope is byte-exact:
// the writers own property order, so encode output is deterministic and any
// divergence under Unity's serializer build fails the comparison.
public sealed class ProtocolGoldenVectorTests
{
    private const string GoldenExecuteEnvelope =
        "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"execute_interaction\","
        + "\"sessionEpoch\":\"epoch-1\",\"requestId\":\"r-1\","
        + "\"payload\":{\"command\":{\"name\":\"set_value\",\"version\":1,"
        + "\"targetId\":\"target-1\",\"arguments\":{\"value\":\"text\"}},"
        + "\"correlationId\":\"correlation-1\"}}";

    [Test]
    public void GoldenExecuteEnvelopeEncodesByteExactly()
    {
        var message = new ExecuteInteractionMessage(
            "m-1",
            "epoch-1",
            "r-1",
            "set_value",
            1,
            "target-1",
            "{\"value\":\"text\"}",
            "correlation-1");

        var encoded = Encoding.UTF8.GetString(ProtocolMessageWriter.Encode(
            message,
            ProtocolLimits.DefaultMaxReceiveMessageBytes));

        Assert.That(encoded, Is.EqualTo(GoldenExecuteEnvelope));
    }

    [Test]
    public void GoldenExecuteEnvelopeDecodesToTheSameMessage()
    {
        var result = ProtocolMessageReader.Read(
            Encoding.UTF8.GetBytes(GoldenExecuteEnvelope),
            ProtocolLimits.DefaultMaxReceiveMessageBytes);

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Success));
        var message = (ExecuteInteractionMessage)result.Message;
        Assert.That(message.MessageId, Is.EqualTo("m-1"));
        Assert.That(message.SessionEpoch, Is.EqualTo("epoch-1"));
        Assert.That(message.RequestId, Is.EqualTo("r-1"));
        Assert.That(message.CommandName, Is.EqualTo("set_value"));
        Assert.That(message.ArgumentsJson, Is.EqualTo("{\"value\":\"text\"}"));
        Assert.That(message.CorrelationId, Is.EqualTo("correlation-1"));
        Assert.That(message.IdempotencyKey, Is.Null);
    }

    [Test]
    public void HandshakeMessagesRoundTripUnderTheBundledSerializer()
    {
        var hello = new HelloMessage(
            "m-1",
            "epoch-1",
            "SignalRouter.Unity 0.1.0",
            new[] { "capability-a" },
            ProtocolLimits.DefaultMaxReceiveMessageBytes,
            "token-value");

        var encoded = ProtocolMessageWriter.Encode(
            hello,
            ProtocolLimits.BootstrapMaxMessageBytes);
        var result = ProtocolMessageReader.Read(
            encoded,
            ProtocolLimits.BootstrapMaxMessageBytes);

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Success));
        var decoded = (HelloMessage)result.Message;
        Assert.That(decoded.PeerVersion, Is.EqualTo(hello.PeerVersion));
        Assert.That(decoded.AuthToken, Is.EqualTo("token-value"));
        Assert.That(decoded.MaxReceiveMessageBytes, Is.EqualTo(hello.MaxReceiveMessageBytes));
    }

    [Test]
    public void SanitizedResultsRoundTripUnderTheBundledSerializer()
    {
        var outcome = new ProtocolInteractionOutcome(
            7,
            "r-1",
            "target-1",
            "click",
            1,
            InteractionOrigin.Agent,
            InteractionStatus.Faulted,
            new[] { new InteractionStageProgress("apply", 0, InteractionStageStatus.Faulted) },
            null,
            "app-code",
            new StateObservation(new[] { new StateProbeObservation("probe-a", "hash-1") }),
            new StateObservation(new[] { new StateProbeObservation("probe-a", "hash-2") }));
        var message = new InteractionResultMessage("m-1", "epoch-1", outcome);

        var result = ProtocolMessageReader.Read(
            ProtocolMessageWriter.Encode(message, ProtocolLimits.DefaultMaxReceiveMessageBytes),
            ProtocolLimits.DefaultMaxReceiveMessageBytes);

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Success));
        var decoded = (InteractionResultMessage)result.Message;
        Assert.That(decoded.Result, Is.EqualTo(outcome));
    }

    [Test]
    public void MalformedInputStaysMalformedUnderTheBundledSerializer()
    {
        var duplicateMember = Encoding.UTF8.GetBytes(
            "{\"protocol\":\"1.0\",\"protocol\":\"1.0\",\"messageId\":\"m-1\","
            + "\"type\":\"ping\",\"payload\":{}}");

        var result = ProtocolMessageReader.Read(
            duplicateMember,
            ProtocolLimits.DefaultMaxReceiveMessageBytes);

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
    }
}
