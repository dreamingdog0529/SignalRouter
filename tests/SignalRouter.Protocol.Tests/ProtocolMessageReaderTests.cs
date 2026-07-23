using System.Text;
using NUnit.Framework;

namespace SignalRouter.Protocol.Tests;

public sealed class ProtocolMessageReaderTests
{
    private const int Limit = ProtocolLimits.DefaultMaxReceiveMessageBytes;

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("\"text\"")]
    [TestCase("42")]
    [TestCase("{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"ping\",\"payload\":{}} trailing")]
    public void NonEnvelopeInputIsMalformed(string input)
    {
        var result = Read(input);

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
        Assert.That(result.ErrorCode, Is.EqualTo(ProtocolErrorCodes.MalformedMessage));
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void InvalidUtf8IsMalformed()
    {
        var result = ProtocolMessageReader.Read(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D }, Limit);

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
    }

    [TestCase("{\"messageId\":\"m-1\",\"type\":\"ping\",\"payload\":{}}")]
    [TestCase("{\"protocol\":\"1\",\"messageId\":\"m-1\",\"type\":\"ping\",\"payload\":{}}")]
    [TestCase("{\"protocol\":1.0,\"messageId\":\"m-1\",\"type\":\"ping\",\"payload\":{}}")]
    [TestCase("{\"protocol\":\"1.0\",\"type\":\"ping\",\"payload\":{}}")]
    [TestCase("{\"protocol\":\"1.0\",\"messageId\":42,\"type\":\"ping\",\"payload\":{}}")]
    [TestCase("{\"protocol\":\"1.0\",\"messageId\":\" padded \",\"type\":\"ping\",\"payload\":{}}")]
    [TestCase("{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"payload\":{}}")]
    [TestCase("{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"ping\"}")]
    [TestCase("{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"ping\",\"payload\":[]}")]
    [TestCase("{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"ping\",\"sessionEpoch\":null,\"payload\":{}}")]
    public void EnvelopesWithMissingOrInvalidRequiredFieldsAreMalformed(string input)
    {
        Assert.That(Read(input).Status, Is.EqualTo(ProtocolReadStatus.Malformed));
    }

    [Test]
    public void DuplicateEnvelopeMembersAreMalformed()
    {
        var result = Read(
            "{\"protocol\":\"1.0\",\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"ping\",\"payload\":{}}");

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
    }

    [Test]
    public void DuplicatePayloadMembersAreMalformed()
    {
        var result = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"error\","
            + "\"payload\":{\"code\":\"a\",\"code\":\"a\",\"message\":\"x\"}}");

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
    }

    [Test]
    public void UnknownEnvelopeAndPayloadMembersAreIgnored()
    {
        var result = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"ping\","
            + "\"futureEnvelopeField\":true,\"payload\":{\"futurePayloadField\":[1,2]}}");

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Success));
        Assert.That(result.Message, Is.InstanceOf<PingMessage>());
    }

    [Test]
    public void UnknownMessageTypesAreReportedButNeverDecoded()
    {
        var result = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"future_operation\","
            + "\"payload\":{\"anything\":true}}");

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.UnknownMessageType));
        Assert.That(result.ErrorCode, Is.EqualTo(ProtocolErrorCodes.UnknownMessageType));
        Assert.That(result.Message, Is.Null);
        Assert.That(result.MessageId, Is.EqualTo("m-1"));
        Assert.That(result.MessageType, Is.EqualTo("future_operation"));
    }

    [Test]
    public void DifferentMajorVersionsStopAtTheEnvelope()
    {
        var higher = Read(
            "{\"protocol\":\"2.0\",\"messageId\":\"m-1\",\"type\":\"hello\",\"payload\":{}}");
        var lower = Read(
            "{\"protocol\":\"0.9\",\"messageId\":\"m-2\",\"type\":\"hello\",\"payload\":{}}");

        Assert.That(higher.Status, Is.EqualTo(ProtocolReadStatus.UnsupportedVersion));
        Assert.That(higher.ErrorCode, Is.EqualTo(ProtocolErrorCodes.ProtocolVersionIncompatible));
        Assert.That(higher.MessageId, Is.EqualTo("m-1"));
        Assert.That(higher.MessageType, Is.EqualTo("hello"));
        Assert.That(lower.Status, Is.EqualTo(ProtocolReadStatus.UnsupportedVersion));
    }

    [Test]
    public void HigherMinorVersionsWithTheSameMajorDecodeNormally()
    {
        var result = Read(
            "{\"protocol\":\"1.9\",\"messageId\":\"m-1\",\"type\":\"ping\",\"payload\":{}}");

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Success));
        Assert.That(result.Message!.Protocol, Is.EqualTo(new ProtocolVersion(1, 9)));
    }

    [Test]
    public void MessageIdIsRecoveredOnlyAfterItValidates()
    {
        var badPayload = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"error\",\"payload\":{}}");
        var badMessageId = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"bad\\u0000id\",\"type\":\"error\",\"payload\":{}}");

        Assert.That(badPayload.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
        Assert.That(badPayload.MessageId, Is.EqualTo("m-1"));
        Assert.That(badPayload.MessageType, Is.EqualTo("error"));
        Assert.That(badMessageId.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
        Assert.That(badMessageId.MessageId, Is.Null);
    }

    [Test]
    public void OversizedInputIsRejectedBeforeParsing()
    {
        var garbage = new byte[ProtocolLimits.BootstrapMaxMessageBytes + 1];
        for (var index = 0; index < garbage.Length; index++)
        {
            garbage[index] = (byte)'x';
        }

        var result = ProtocolMessageReader.Read(
            garbage,
            ProtocolLimits.BootstrapMaxMessageBytes);

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.MessageTooLarge));
        Assert.That(result.ErrorCode, Is.EqualTo(ProtocolErrorCodes.PayloadTooLarge));
    }

    [Test]
    public void InputAtExactlyTheLimitIsAccepted()
    {
        var encoded = ProtocolMessageWriter.Encode(new PingMessage("m-1"), Limit);

        var atLimit = ProtocolMessageReader.Read(encoded, encoded.Length);
        var belowLimit = ProtocolMessageReader.Read(encoded, encoded.Length - 1);

        Assert.That(atLimit.Status, Is.EqualTo(ProtocolReadStatus.Success));
        Assert.That(belowLimit.Status, Is.EqualTo(ProtocolReadStatus.MessageTooLarge));
    }

    [Test]
    public void NestingBeyondTheTotalDepthLimitIsMalformed()
    {
        var deep = ProtocolMessageModelTests.BuildNestedObject(ProtocolLimits.MaxJsonDepth);
        var result = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"ping\","
            + "\"payload\":{\"deep\":" + deep + "}}");

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
    }

    [Test]
    public void FailureMessagesNeverEchoPayloadContent()
    {
        var result = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"error\","
            + "\"payload\":{\"code\":42,\"message\":\"SECRET-CONTENT\"}}");

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
        Assert.That(result.ErrorMessage, Does.Not.Contain("SECRET-CONTENT"));
        Assert.That(result.ErrorMessage, Does.Not.Contain("42"));
    }

    [Test]
    public void EnvelopeFieldsForbiddenByTheMessageTypeAreMalformed()
    {
        var helloWithRequestId = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"hello\","
            + "\"sessionEpoch\":\"epoch-1\",\"requestId\":\"r-1\","
            + "\"payload\":{\"peerVersion\":\"peer 1.0\",\"capabilities\":[],"
            + "\"maxReceiveMessageBytes\":65536}}");
        var executeWithoutRequestId = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"execute_interaction\","
            + "\"sessionEpoch\":\"epoch-1\","
            + "\"payload\":{\"command\":{\"name\":\"click\",\"version\":1,"
            + "\"targetId\":\"target-1\",\"arguments\":{}}}}");

        Assert.That(helloWithRequestId.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
        Assert.That(executeWithoutRequestId.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
    }

    [Test]
    public void ConstructorContractViolationsAreMalformed()
    {
        var undersizedReceiveLimit = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"hello\","
            + "\"sessionEpoch\":\"epoch-1\","
            + "\"payload\":{\"peerVersion\":\"peer 1.0\",\"capabilities\":[],"
            + "\"maxReceiveMessageBytes\":100}}");
        var zeroSequence = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"interaction_accepted\","
            + "\"sessionEpoch\":\"epoch-1\",\"requestId\":\"r-1\",\"inReplyTo\":\"m-0\","
            + "\"payload\":{\"sequence\":0}}");
        var duplicateCapabilities = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"hello\","
            + "\"sessionEpoch\":\"epoch-1\","
            + "\"payload\":{\"peerVersion\":\"peer 1.0\",\"capabilities\":[\"a\",\"a\"],"
            + "\"maxReceiveMessageBytes\":65536}}");

        Assert.That(undersizedReceiveLimit.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
        Assert.That(zeroSequence.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
        Assert.That(duplicateCapabilities.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
    }

    [TestCase("\"agent\"")]
    [TestCase("\"1\"")]
    [TestCase("1")]
    public void ResultEnumsRequireExactNames(string origin)
    {
        var result = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"interaction_result\","
            + "\"sessionEpoch\":\"epoch-1\",\"requestId\":\"r-1\","
            + "\"payload\":{\"result\":{\"sequence\":1,\"targetId\":\"target-1\","
            + "\"command\":{\"name\":\"click\",\"version\":1},"
            + "\"origin\":" + origin + ",\"status\":\"Succeeded\",\"stages\":[],"
            + "\"state\":{\"before\":{},\"after\":{}}}}}");

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Malformed));
    }

    [Test]
    public void WellFormedResultPayloadDecodes()
    {
        var result = Read(
            "{\"protocol\":\"1.0\",\"messageId\":\"m-1\",\"type\":\"interaction_result\","
            + "\"sessionEpoch\":\"epoch-1\",\"requestId\":\"r-1\","
            + "\"payload\":{\"result\":{\"sequence\":1,\"targetId\":\"target-1\","
            + "\"command\":{\"name\":\"click\",\"version\":1},"
            + "\"origin\":\"Agent\",\"status\":\"Succeeded\","
            + "\"stages\":[{\"id\":\"apply\",\"status\":\"Completed\"}],"
            + "\"state\":{\"before\":{\"probe-a\":\"hash-1\"},\"after\":{\"probe-a\":\"hash-1\"}}}}}");

        Assert.That(result.Status, Is.EqualTo(ProtocolReadStatus.Success));
        var message = (InteractionResultMessage)result.Message!;
        Assert.That(message.Result.RequestId, Is.EqualTo("r-1"));
        Assert.That(message.Result.Stages, Has.Count.EqualTo(1));
    }

    [Test]
    public void SizeLimitArgumentMustBePositive()
    {
        NUnitCompat.Throws<ArgumentOutOfRangeException>(
            () => _ = ProtocolMessageReader.Read(new byte[] { 0x7B }, 0));
    }

    private static ProtocolReadResult Read(string json)
    {
        return ProtocolMessageReader.Read(Encoding.UTF8.GetBytes(json), Limit);
    }
}
