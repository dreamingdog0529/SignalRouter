using System.Threading.Channels;
using NUnit.Framework;
using SignalRouter;
using SignalRouter.Protocol;
using SignalRouter.Protocol.Transport;

namespace SignalRouter.McpHost.Tests;

public sealed class HostBridgeTests
{
    private const string Epoch = "epoch-1";

    [Test]
    public async Task TheHandshakeEchoesTheEpochAndSelectsTheVersion()
    {
        using var harness = new HostHarness();
        using var peer = await harness.ConnectRuntimeAsync();

        var welcome = await peer.PerformHandshakeAsync();

        Assert.That(welcome.SessionEpoch, Is.EqualTo(Epoch));
        Assert.That(welcome.InReplyTo, Is.EqualTo(peer.HelloMessageId));
        Assert.That(welcome.Protocol, Is.EqualTo(ProtocolVersion.Current));
        Assert.That(harness.Bridge.IsConnected, Is.True);
        Assert.That(harness.Bridge.SessionEpoch, Is.EqualTo(Epoch));
    }

    [Test]
    public async Task ExecuteRoundTripsToACompletedReport()
    {
        using var harness = new HostHarness();
        using var peer = await harness.ConnectRuntimeAsync();
        await peer.PerformHandshakeAsync();

        var executeTask = harness.Bridge.ExecuteInteractionAsync(
            "r-1",
            "target-1",
            "click",
            1,
            "{}",
            null,
            CancellationToken.None);

        var execute = (ExecuteInteractionMessage)await peer.ReceiveAsync();
        Assert.That(execute.RequestId, Is.EqualTo("r-1"));
        await peer.SendAsync(new InteractionAcceptedMessage(
            peer.NextId(), Epoch, "r-1", execute.MessageId, 1));
        await peer.SendAsync(new InteractionResultMessage(
            peer.NextId(), Epoch, SucceededOutcome("r-1"), execute.MessageId));

        var report = await executeTask;
        Assert.That(report.Status, Is.EqualTo("completed"));
        Assert.That(report.Outcome!.Status, Is.EqualTo(InteractionStatus.Succeeded));
    }

    [Test]
    public async Task AToolTimeoutAnswersPendingAndTheResultStaysQueryable()
    {
        using var harness = new HostHarness(toolTimeout: TimeSpan.FromMilliseconds(100));
        using var peer = await harness.ConnectRuntimeAsync();
        await peer.PerformHandshakeAsync();

        var report = await harness.Bridge.ExecuteInteractionAsync(
            "r-1", "target-1", "click", 1, "{}", null, CancellationToken.None);
        Assert.That(report.Status, Is.EqualTo("pending"));

        // The runtime completes later; the host answers a follow-up query with
        // the terminal result relayed by the runtime.
        var execute = (ExecuteInteractionMessage)await peer.ReceiveAsync();
        var queryTask = harness.Bridge.GetInteractionResultAsync("r-1", CancellationToken.None);
        var query = (GetInteractionResultMessage)await peer.ReceiveAsync();
        await peer.SendAsync(new InteractionResultMessage(
            peer.NextId(), Epoch, SucceededOutcome("r-1"), query.MessageId));

        var reply = await queryTask;
        Assert.That(reply, Is.InstanceOf<InteractionResultMessage>());
        Assert.That(execute.RequestId, Is.EqualTo("r-1"));
    }

    [Test]
    public async Task RecoveryQueriesFirstAndResendsByteExactWithinTheWindow()
    {
        using var harness = new HostHarness(toolTimeout: TimeSpan.FromMilliseconds(100));
        var firstPeer = await harness.ConnectRuntimeAsync();
        await firstPeer.PerformHandshakeAsync();

        var report = await harness.Bridge.ExecuteInteractionAsync(
            "r-1", "target-1", "click", 1, "{}", null, CancellationToken.None);
        Assert.That(report.Status, Is.EqualTo("pending"));
        var originalExecute = await firstPeer.ReceiveRawAsync();

        // The connection dies before any reply; the request must survive.
        firstPeer.Drop();
        await harness.WaitForDisconnectAsync();

        using var secondPeer = await harness.ConnectRuntimeAsync();
        await secondPeer.PerformHandshakeAsync();

        // Query-first: the host asks before ever resending.
        var recoveryQuery = (GetInteractionResultMessage)await secondPeer.ReceiveAsync();
        Assert.That(recoveryQuery.RequestId, Is.EqualTo("r-1"));
        await secondPeer.SendAsync(new ErrorMessage(
            secondPeer.NextId(),
            ProtocolErrorCodes.ResultUnavailable,
            "No result is retained for this request.",
            Epoch,
            "r-1",
            recoveryQuery.MessageId));

        // Unavailable within the recovery window → the byte-exact original.
        var resent = await secondPeer.ReceiveRawAsync();
        Assert.That(resent, Is.EqualTo(originalExecute));
        firstPeer.Dispose();
    }

    [Test]
    public async Task AnEpochChangeFailsPendingWorkAsSessionLost()
    {
        using var harness = new HostHarness(toolTimeout: TimeSpan.FromSeconds(20));
        var firstPeer = await harness.ConnectRuntimeAsync();
        await firstPeer.PerformHandshakeAsync();

        var executeTask = harness.Bridge.ExecuteInteractionAsync(
            "r-1", "target-1", "click", 1, "{}", null, CancellationToken.None);
        _ = await firstPeer.ReceiveAsync();
        firstPeer.Drop();
        await harness.WaitForDisconnectAsync();

        using var secondPeer = await harness.ConnectRuntimeAsync("epoch-2");
        await secondPeer.PerformHandshakeAsync();

        var report = await executeTask;
        Assert.That(report.Status, Is.EqualTo("outcome_unknown"));
        Assert.That(report.Detail, Is.EqualTo("session_lost"));
        firstPeer.Dispose();
    }

    [Test]
    public async Task ASecondConcurrentRuntimeIsRefused()
    {
        using var harness = new HostHarness();
        using var firstPeer = await harness.ConnectRuntimeAsync();
        await firstPeer.PerformHandshakeAsync();

        using var secondPeer = await harness.ConnectRuntimeAsync();
        var refusal = (ErrorMessage)await secondPeer.ReceiveAsync();

        Assert.That(refusal.Code, Is.EqualTo(ProtocolErrorCodes.RuntimeBusy));
    }

    [Test]
    public async Task SnapshotAndWaitRoundTrip()
    {
        using var harness = new HostHarness();
        using var peer = await harness.ConnectRuntimeAsync();
        await peer.PerformHandshakeAsync();

        var snapshotTask = harness.Bridge.GetRegistrySnapshotAsync(CancellationToken.None);
        var snapshotRequest = (GetRegistrySnapshotMessage)await peer.ReceiveAsync();
        await peer.SendAsync(new RegistrySnapshotMessage(
            peer.NextId(), Epoch, snapshotRequest.MessageId, 1, "{\"targets\":[]}"));
        var snapshot = await snapshotTask;
        Assert.That(snapshot!.SnapshotJson, Is.EqualTo("{\"targets\":[]}"));

        var waitTask = harness.Bridge.WaitForAsync(
            ProtocolWaitConditions.Idle, null, 2000, CancellationToken.None);
        var waitRequest = (WaitForMessage)await peer.ReceiveAsync();
        await peer.SendAsync(new WaitResultMessage(
            peer.NextId(), Epoch, waitRequest.MessageId, waitRequest.Condition, true, 7));
        var wait = await waitTask;
        Assert.That(wait!.Satisfied, Is.True);
        Assert.That(wait.ElapsedMs, Is.EqualTo(7));
    }

    [Test]
    public async Task ExecuteBeforeAnyRuntimeConnectionAnswersDisconnected()
    {
        using var harness = new HostHarness();

        var report = await harness.Bridge.ExecuteInteractionAsync(
            "r-1", "target-1", "click", 1, "{}", null, CancellationToken.None);

        Assert.That(report.Status, Is.EqualTo("disconnected"));
    }

    [Test]
    public async Task CancellationIntentIsForwardedAndResentAfterReconnect()
    {
        using var harness = new HostHarness(toolTimeout: TimeSpan.FromMilliseconds(100));
        var firstPeer = await harness.ConnectRuntimeAsync();
        await firstPeer.PerformHandshakeAsync();

        _ = await harness.Bridge.ExecuteInteractionAsync(
            "r-1", "target-1", "click", 1, "{}", null, CancellationToken.None);
        _ = await firstPeer.ReceiveAsync();

        Assert.That(
            await harness.Bridge.CancelInteractionAsync("r-1", CancellationToken.None),
            Is.True);
        var cancel = (CancelInteractionMessage)await firstPeer.ReceiveAsync();
        Assert.That(cancel.RequestId, Is.EqualTo("r-1"));

        firstPeer.Drop();
        await harness.WaitForDisconnectAsync();
        using var secondPeer = await harness.ConnectRuntimeAsync();
        await secondPeer.PerformHandshakeAsync();

        // Recovery replays both the query and the cancel intent.
        var query = (GetInteractionResultMessage)await secondPeer.ReceiveAsync();
        var resentCancel = (CancelInteractionMessage)await secondPeer.ReceiveAsync();
        Assert.That(query.RequestId, Is.EqualTo("r-1"));
        Assert.That(resentCancel.RequestId, Is.EqualTo("r-1"));
        firstPeer.Dispose();
    }

    private static ProtocolInteractionOutcome SucceededOutcome(string requestId)
    {
        return new ProtocolInteractionOutcome(
            1,
            requestId,
            "target-1",
            "click",
            1,
            InteractionOrigin.Agent,
            InteractionStatus.Succeeded,
            new[] { new InteractionStageProgress("apply", 0, InteractionStageStatus.Completed) },
            null,
            null,
            StateObservation.Empty,
            StateObservation.Empty);
    }

    // The bridge under test plus scripted runtime peers over in-memory
    // duplex channels.
    private sealed class HostHarness : IDisposable
    {
        private readonly List<Task> connectionTasks = new();

        public HostHarness(TimeSpan? toolTimeout = null)
        {
            var counter = 0;
            Bridge = new HostBridge(new HostBridgeOptions(
                "SignalRouter.McpHost test",
                toolTimeout ?? TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10),
                () => "h-" + (++counter),
                () => new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero)));
        }

        public HostBridge Bridge { get; }

        public async Task<RuntimePeer> ConnectRuntimeAsync(string epoch = Epoch)
        {
            var channel = new InMemoryDuplexChannel();
            connectionTasks.Add(Bridge.RunConnectionAsync(channel, CancellationToken.None));
            var peer = new RuntimePeer(channel, epoch);
            await Task.Yield();
            return peer;
        }

        public async Task WaitForDisconnectAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Bridge.IsConnected)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("The bridge never observed the disconnect.");
                }

                await Task.Delay(5);
            }
        }

        public void Dispose()
        {
            Bridge.Dispose();
        }
    }

    private sealed class RuntimePeer : IDisposable
    {
        private readonly InMemoryDuplexChannel channel;
        private readonly string epoch;
        private int nextId;

        public RuntimePeer(InMemoryDuplexChannel channel, string epoch)
        {
            this.channel = channel;
            this.epoch = epoch;
        }

        public string HelloMessageId { get; private set; } = "";

        public string NextId() => "r-msg-" + (++nextId);

        public async Task<WelcomeMessage> PerformHandshakeAsync()
        {
            HelloMessageId = NextId();
            await SendAsync(new HelloMessage(
                HelloMessageId,
                epoch,
                "SignalRouter.Unity test",
                Array.Empty<string>(),
                ProtocolLimits.DefaultMaxReceiveMessageBytes,
                null,
                recoveryWindowMs: 60_000));
            return (WelcomeMessage)await ReceiveAsync();
        }

        public Task SendAsync(ProtocolMessage message)
        {
            return channel.PeerDeliver(ProtocolMessageWriter.Encode(
                message,
                ProtocolLimits.DefaultMaxReceiveMessageBytes));
        }

        public async Task<ProtocolMessage> ReceiveAsync()
        {
            var payload = await ReceiveRawAsync();
            var read = ProtocolMessageReader.Read(
                payload,
                ProtocolLimits.DefaultMaxReceiveMessageBytes);
            Assert.That(read.Status, Is.EqualTo(ProtocolReadStatus.Success), read.ErrorMessage);
            return read.Message!;
        }

        public Task<byte[]> ReceiveRawAsync()
        {
            return channel.PeerReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Drop()
        {
            channel.PeerClose();
        }

        public void Dispose()
        {
            channel.PeerClose();
        }
    }

    // The host-side channel of an in-memory duplex; the test scripts the
    // runtime side directly with encoded payloads.
    private sealed class InMemoryDuplexChannel : IProtocolChannel
    {
        private readonly Channel<ProtocolChannelFrame> toHost =
            Channel.CreateUnbounded<ProtocolChannelFrame>();

        private readonly Channel<byte[]> toPeer = Channel.CreateUnbounded<byte[]>();

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken)
        {
            return toPeer.Writer.WriteAsync(message.ToArray(), cancellationToken);
        }

        public async ValueTask<ProtocolChannelFrame> ReceiveAsync(
            int maxMessageBytes,
            CancellationToken cancellationToken)
        {
            try
            {
                return await toHost.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return ProtocolChannelFrame.Closed();
            }
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            toHost.Writer.TryComplete();
            return default;
        }

        public Task PeerDeliver(byte[] payload)
        {
            toHost.Writer.TryWrite(ProtocolChannelFrame.Message(payload));
            return Task.CompletedTask;
        }

        public Task<byte[]> PeerReceiveAsync()
        {
            return toPeer.Reader.ReadAsync().AsTask();
        }

        public void PeerClose()
        {
            toHost.Writer.TryComplete();
        }

        public void Dispose()
        {
            toHost.Writer.TryComplete();
            toPeer.Writer.TryComplete();
        }
    }
}
