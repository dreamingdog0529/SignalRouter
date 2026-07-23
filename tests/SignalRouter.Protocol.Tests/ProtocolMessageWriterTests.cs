using NUnit.Framework;

namespace SignalRouter.Protocol.Tests;

public sealed class ProtocolMessageWriterTests
{
    private const string Epoch = "epoch-1";
    private const int Limit = ProtocolLimits.DefaultMaxReceiveMessageBytes;

    [Test]
    public void EncodingEnforcesTheSizeLimitWhileWriting()
    {
        var message = new ErrorMessage(
            "m-1",
            ProtocolErrorCodes.MalformedMessage,
            new string('x', ProtocolLimits.MaxErrorMessageChars));

        NUnitCompat.Throws<InvalidOperationException>(
            () => _ = ProtocolMessageWriter.Encode(message, 64));
        NUnitCompat.ThatThrows(
            () => _ = ProtocolMessageWriter.Encode(message, Limit),
            Throws.Nothing);
    }

    [Test]
    public void EncodedMessagesAtTheDepthBudgetSurviveTheReaderSymmetrically()
    {
        var execute = new ExecuteInteractionMessage(
            "m-1",
            Epoch,
            "r-1",
            "click",
            1,
            "target-1",
            ProtocolMessageModelTests.BuildNestedObject(61));
        var snapshot = new RegistrySnapshotMessage(
            "m-2",
            Epoch,
            "m-1",
            1,
            ProtocolMessageModelTests.BuildNestedObject(62));

        var executeResult = ProtocolMessageReader.Read(
            ProtocolMessageWriter.Encode(execute, Limit),
            Limit);
        var snapshotResult = ProtocolMessageReader.Read(
            ProtocolMessageWriter.Encode(snapshot, Limit),
            Limit);

        Assert.That(executeResult.Status, Is.EqualTo(ProtocolReadStatus.Success));
        Assert.That(snapshotResult.Status, Is.EqualTo(ProtocolReadStatus.Success));
    }

    [Test]
    public void OversizedOpaquePayloadsFailWithoutGrowingTowardsTheirSize()
    {
        var hugeArguments = "{\"value\":\"" + new string('x', 512 * 1024) + "\"}";
        var message = new ExecuteInteractionMessage(
            "m-1",
            Epoch,
            "r-1",
            "set_value",
            1,
            "target-1",
            hugeArguments);

        NUnitCompat.Throws<InvalidOperationException>(
            () => _ = ProtocolMessageWriter.Encode(message, 1024));
    }

    [Test]
    public void EncodeValidatesItsArguments()
    {
        NUnitCompat.Throws<ArgumentNullException>(
            () => _ = ProtocolMessageWriter.Encode(null!, Limit));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(
            () => _ = ProtocolMessageWriter.Encode(new PingMessage("m-1"), 0));
    }
}
