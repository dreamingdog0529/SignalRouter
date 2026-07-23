using System.Text;
using System.Threading.Channels;
using NUnit.Framework;
using SignalRouter;
using SignalRouter.Protocol.Transport;

namespace SignalRouter.Protocol.Tests;

public sealed class RuntimeBridgeSessionTests
{
    private const string Epoch = "epoch-1";

    [Test]
    public async Task TheHandshakeAdvertisesTheLedgerRetentionAndEstablishesTheSession()
    {
        using var harness = new SessionHarness();

        var hello = (HelloMessage)await harness.HostReceiveAsync();
        Assert.That(hello.SessionEpoch, Is.EqualTo(Epoch));
        Assert.That(
            hello.RecoveryWindowMs,
            Is.EqualTo((int)harness.Ledger.Retention.TotalMilliseconds));

        harness.HostDeliver(harness.CreateWelcome(hello));
        await harness.WaitForSessionAsync();
        Assert.That(harness.Session.Session!.SessionEpoch, Is.EqualTo(Epoch));
    }

    [Test]
    public async Task ExecuteFlowsThroughAcceptedToTerminalResult()
    {
        using var harness = new SessionHarness();
        await harness.CompleteHandshakeAsync();

        harness.HostDeliver(harness.CreateExecute("r-1", "m-exec"));
        var accepted = (InteractionAcceptedMessage)await harness.HostReceiveAsync();
        Assert.That(accepted.RequestId, Is.EqualTo("r-1"));
        Assert.That(accepted.InReplyTo, Is.EqualTo("m-exec"));
        Assert.That(accepted.Sequence, Is.EqualTo(1));
        Assert.That(harness.Submitter.Calls, Is.EqualTo(1));

        harness.Submitter.Start();
        harness.Submitter.Complete(SucceededResult("r-1"));
        var result = (InteractionResultMessage)await harness.HostReceiveAsync();
        Assert.That(result.RequestId, Is.EqualTo("r-1"));
        Assert.That(result.Result.Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(
            harness.Ledger.TryGet("r-1")!.State,
            Is.EqualTo(ProtocolRequestState.Terminal));
    }

    [Test]
    public async Task AByteExactResendIsAnsweredFromTheLedgerWithoutRedispatching()
    {
        using var harness = new SessionHarness();
        await harness.CompleteHandshakeAsync();
        var execute = harness.CreateExecute("r-1", "m-exec");
        harness.HostDeliver(execute);
        _ = await harness.HostReceiveAsync();
        harness.Submitter.Start();
        harness.Submitter.Complete(SucceededResult("r-1"));
        _ = await harness.HostReceiveAsync();

        harness.HostDeliver(execute);
        var replayed = (InteractionResultMessage)await harness.HostReceiveAsync();

        Assert.That(replayed.RequestId, Is.EqualTo("r-1"));
        Assert.That(replayed.Result.Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(harness.Submitter.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task AnImmediateRejectionAnswersWithAResultNotAnAcceptance()
    {
        using var harness = new SessionHarness();
        harness.Submitter.RejectImmediately = true;
        await harness.CompleteHandshakeAsync();

        harness.HostDeliver(harness.CreateExecute("r-1", "m-exec"));
        var result = (InteractionResultMessage)await harness.HostReceiveAsync();

        Assert.That(result.Result.Status, Is.EqualTo(InteractionStatus.Rejected));
        Assert.That(
            result.Result.RejectionCode,
            Is.EqualTo(InteractionRejectionCode.CommandNotRegistered));
        Assert.That(
            harness.Ledger.TryGet("r-1")!.State,
            Is.EqualTo(ProtocolRequestState.Terminal));
    }

    [Test]
    public async Task QueriesAnswerPendingStatusTerminalResultOrUnavailability()
    {
        using var harness = new SessionHarness();
        await harness.CompleteHandshakeAsync();
        harness.HostDeliver(harness.CreateExecute("r-1", "m-exec"));
        _ = await harness.HostReceiveAsync();

        harness.HostDeliver(harness.CreateQuery("r-1", "m-q1"));
        var pending = (InteractionStatusMessage)await harness.HostReceiveAsync();
        Assert.That(pending.State, Is.EqualTo(ProtocolRequestState.Queued));
        Assert.That(pending.Sequence, Is.EqualTo(1));
        Assert.That(pending.InReplyTo, Is.EqualTo("m-q1"));

        harness.Submitter.Start();
        harness.Submitter.Complete(SucceededResult("r-1"));
        _ = await harness.HostReceiveAsync();

        harness.HostDeliver(harness.CreateQuery("r-1", "m-q2"));
        var terminal = (InteractionResultMessage)await harness.HostReceiveAsync();
        Assert.That(terminal.InReplyTo, Is.EqualTo("m-q2"));

        harness.HostDeliver(harness.CreateQuery("r-unknown", "m-q3"));
        var unavailable = (ErrorMessage)await harness.HostReceiveAsync();
        Assert.That(unavailable.Code, Is.EqualTo(ProtocolErrorCodes.ResultUnavailable));
        Assert.That(unavailable.RequestId, Is.EqualTo("r-unknown"));
    }

    [Test]
    public async Task CancellationMarksIntentAndReachesTheRuntime()
    {
        using var harness = new SessionHarness();
        await harness.CompleteHandshakeAsync();
        harness.HostDeliver(harness.CreateExecute("r-1", "m-exec"));
        _ = await harness.HostReceiveAsync();

        harness.HostDeliver(harness.CreateCancel("r-1", "m-cancel"));
        harness.HostDeliver(harness.CreateQuery("r-1", "m-q"));
        var status = (InteractionStatusMessage)await harness.HostReceiveAsync();

        Assert.That(status.CancelRequested, Is.True);
        Assert.That(harness.CancelledRequestIds, Is.EqualTo(new[] { "r-1" }));
    }

    [Test]
    public async Task ACoreAdmissionFailureAbandonsTheReservationAndReportsBusy()
    {
        using var harness = new SessionHarness();
        harness.Submitter.ThrowOnSubmit = true;
        await harness.CompleteHandshakeAsync();

        harness.HostDeliver(harness.CreateExecute("r-1", "m-exec"));
        var busy = (ErrorMessage)await harness.HostReceiveAsync();
        Assert.That(busy.Code, Is.EqualTo(ProtocolErrorCodes.RuntimeBusy));
        Assert.That(harness.Ledger.TryGet("r-1"), Is.Null);

        // The reservation was abandoned, so an honest resend is admitted.
        harness.Submitter.ThrowOnSubmit = false;
        harness.HostDeliver(harness.CreateExecute("r-1", "m-exec2"));
        var accepted = (InteractionAcceptedMessage)await harness.HostReceiveAsync();
        Assert.That(accepted.RequestId, Is.EqualTo("r-1"));
    }

    [Test]
    public async Task AForeignEpochMessageClosesTheConnection()
    {
        using var harness = new SessionHarness();
        await harness.CompleteHandshakeAsync();

        harness.HostDeliver(ProtocolMessageWriter.Encode(
            new ExecuteInteractionMessage(
                "m-foreign",
                "epoch-2",
                "r-1",
                "click",
                1,
                "target-1",
                "{}"),
            ProtocolLimits.DefaultMaxReceiveMessageBytes));

        var error = (ErrorMessage)await harness.HostReceiveAsync();
        Assert.That(error.Code, Is.EqualTo(ProtocolErrorCodes.SessionEpochMismatch));
        await harness.WaitForRunCompletionAsync();
    }

    [Test]
    public async Task SnapshotRequestsAnswerWithTheAgentViewDocument()
    {
        using var harness = new SessionHarness();
        await harness.CompleteHandshakeAsync();

        harness.HostDeliver(ProtocolMessageWriter.Encode(
            new GetRegistrySnapshotMessage("m-snap", Epoch),
            ProtocolLimits.DefaultMaxReceiveMessageBytes));
        var snapshot = (RegistrySnapshotMessage)await harness.HostReceiveAsync();

        Assert.That(snapshot.ProbeVersion, Is.EqualTo(1));
        Assert.That(snapshot.SnapshotJson, Is.EqualTo("{\"targets\":[]}"));
        Assert.That(snapshot.InReplyTo, Is.EqualTo("m-snap"));
    }

    [Test]
    public async Task MalformedInputIsAnsweredWithoutDroppingTheConnection()
    {
        using var harness = new SessionHarness();
        await harness.CompleteHandshakeAsync();

        harness.HostDeliver(Encoding.UTF8.GetBytes("not json"));
        var error = (ErrorMessage)await harness.HostReceiveAsync();
        Assert.That(error.Code, Is.EqualTo(ProtocolErrorCodes.MalformedMessage));

        harness.HostDeliver(ProtocolMessageWriter.Encode(
            new PingMessage("m-ping", Epoch),
            ProtocolLimits.DefaultMaxReceiveMessageBytes));
        var pong = (PongMessage)await harness.HostReceiveAsync();
        Assert.That(pong.InReplyTo, Is.EqualTo("m-ping"));
    }

    [Test]
    public async Task ASendFailureEndsTheSessionNormallySoTheOwnerCanReconnect()
    {
        using var harness = new SessionHarness();
        await harness.CompleteHandshakeAsync();

        harness.FailSends = true;
        harness.HostDeliver(harness.CreateExecute("r-1", "m-exec"));

        // RunAsync must complete without throwing — session-local teardown has
        // to look exactly like a peer disconnect to the reconnect loop.
        await harness.WaitForRunCompletionAsync();
    }

    [Test]
    public async Task ARejectedMainThreadPostEndsTheSessionQuietly()
    {
        using var harness = new SessionHarness(postThrows: true);
        await harness.CompleteHandshakeAsync();

        harness.HostDeliver(harness.CreateExecute("r-1", "m-exec"));

        await harness.WaitForRunCompletionAsync();
    }

    [Test]
    public async Task AnOversizedReplyAnswersPayloadTooLargeInsteadOfDroppingTheConnection()
    {
        var hugeSnapshot = "{\"blob\":\"" + new string('x', 80 * 1024) + "\"}";
        using var harness = new SessionHarness(snapshotJson: hugeSnapshot);
        await harness.CompleteHandshakeAsync(
            hostReceiveLimit: ProtocolLimits.BootstrapMaxMessageBytes);

        harness.HostDeliver(ProtocolMessageWriter.Encode(
            new GetRegistrySnapshotMessage("m-snap", Epoch),
            ProtocolLimits.DefaultMaxReceiveMessageBytes));
        var error = (ErrorMessage)await harness.HostReceiveAsync();

        Assert.That(error.Code, Is.EqualTo(ProtocolErrorCodes.PayloadTooLarge));
        Assert.That(error.InReplyTo, Is.EqualTo("m-snap"));

        // The connection survives the size verdict.
        harness.HostDeliver(ProtocolMessageWriter.Encode(
            new PingMessage("m-ping", Epoch),
            ProtocolLimits.DefaultMaxReceiveMessageBytes));
        var pong = (PongMessage)await harness.HostReceiveAsync();
        Assert.That(pong.InReplyTo, Is.EqualTo("m-ping"));
    }

    private static InteractionResult SucceededResult(string requestId)
    {
        return new InteractionResult(
            1,
            requestId,
            "target-1",
            "click",
            1,
            InteractionOrigin.Agent,
            InteractionStatus.Succeeded,
            null,
            null,
            StageProgress.Empty,
            StateObservation.Empty,
            StateObservation.Empty,
            StateDiff.Empty);
    }

    // The session under test wired to an in-memory duplex; the test plays the
    // host peer. PostToMainThread executes inline, so ledger access stays on
    // the delivering thread deterministically.
    private sealed class SessionHarness : IDisposable
    {
        private readonly InMemoryDuplexChannel channel = new();
        private readonly Task runTask;
        private int nextMessageId;

        public SessionHarness(bool postThrows = false, string? snapshotJson = null)
        {
            Ledger = new ProtocolRequestLedger(
                Epoch,
                16,
                TimeSpan.FromMinutes(5),
                new FixedClock());
            Submitter = new FakeSubmitter();
            var options = new RuntimeBridgeSessionOptions(
                Ledger,
                new ProtocolPeerOptions(
                    "SignalRouter.Unity test",
                    Array.Empty<string>(),
                    ProtocolLimits.DefaultMaxReceiveMessageBytes),
                postThrows
                    ? new Action<Action>(_ => throw new InvalidOperationException(
                        "The interaction runtime is shut down."))
                    : action => action(),
                Submitter.Submit,
                requestId =>
                {
                    CancelledRequestIds.Add(requestId);
                    return true;
                },
                () => new RegistrySnapshotDocument(1, snapshotJson ?? "{\"targets\":[]}"),
                null,
                () => "s-" + (++nextMessageId));
            Session = new RuntimeBridgeSession(channel, options);
            runTask = Session.RunAsync();
        }

        public bool FailSends
        {
            get => channel.FailSends;
            set => channel.FailSends = value;
        }

        public ProtocolRequestLedger Ledger { get; }

        public FakeSubmitter Submitter { get; }

        public RuntimeBridgeSession Session { get; }

        public List<string> CancelledRequestIds { get; } = new();

        public async Task<ProtocolMessage> HostReceiveAsync()
        {
            var payload = await channel.HostReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var read = ProtocolMessageReader.Read(
                payload,
                ProtocolLimits.DefaultMaxReceiveMessageBytes);
            Assert.That(
                read.Status,
                Is.EqualTo(ProtocolReadStatus.Success),
                read.ErrorMessage);
            return read.Message!;
        }

        public void HostDeliver(byte[] payload)
        {
            channel.HostDeliver(payload);
        }

        public async Task CompleteHandshakeAsync(
            int hostReceiveLimit = ProtocolLimits.DefaultMaxReceiveMessageBytes)
        {
            var hello = (HelloMessage)await HostReceiveAsync();
            HostDeliver(CreateWelcome(hello, hostReceiveLimit));
            await WaitForSessionAsync();
        }

        public byte[] CreateWelcome(
            HelloMessage hello,
            int hostReceiveLimit = ProtocolLimits.DefaultMaxReceiveMessageBytes)
        {
            return ProtocolMessageWriter.Encode(
                new WelcomeMessage(
                    "h-welcome",
                    hello.SessionEpoch!,
                    hello.MessageId,
                    "SignalRouter.McpHost test",
                    Array.Empty<string>(),
                    hostReceiveLimit),
                ProtocolLimits.BootstrapMaxMessageBytes);
        }

        public byte[] CreateExecute(string requestId, string messageId)
        {
            return ProtocolMessageWriter.Encode(
                new ExecuteInteractionMessage(
                    messageId,
                    Epoch,
                    requestId,
                    "click",
                    1,
                    "target-1",
                    "{}"),
                ProtocolLimits.DefaultMaxReceiveMessageBytes);
        }

        public byte[] CreateQuery(string requestId, string messageId)
        {
            return ProtocolMessageWriter.Encode(
                new GetInteractionResultMessage(messageId, Epoch, requestId),
                ProtocolLimits.DefaultMaxReceiveMessageBytes);
        }

        public byte[] CreateCancel(string requestId, string messageId)
        {
            return ProtocolMessageWriter.Encode(
                new CancelInteractionMessage(messageId, Epoch, requestId),
                ProtocolLimits.DefaultMaxReceiveMessageBytes);
        }

        public async Task WaitForSessionAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Session.Session == null)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("The session never became established.");
                }

                await Task.Delay(5);
            }
        }

        public Task WaitForRunCompletionAsync()
        {
            return runTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            channel.HostClose();
            try
            {
                runTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                Assert.Fail("The session loop did not stop after the channel closed.");
            }

            channel.Dispose();
        }

        private sealed class FixedClock : IInteractionClock
        {
            public DateTimeOffset UtcNow { get; } =
                new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        }
    }

    internal sealed class FakeSubmitter
    {
        private TaskCompletionSource<bool>? started;
        private TaskCompletionSource<InteractionResult>? completion;

        public int Calls { get; private set; }

        public bool ThrowOnSubmit { get; set; }

        public bool RejectImmediately { get; set; }

        public InteractionSubmission Submit(ExecuteInteractionMessage message)
        {
            Calls++;
            if (ThrowOnSubmit)
            {
                throw new InvalidOperationException("The dispatcher is busy.");
            }

            if (RejectImmediately)
            {
                var rejected = new InteractionResult(
                    Calls,
                    message.RequestId!,
                    message.TargetId,
                    message.CommandName,
                    message.CommandVersion,
                    InteractionOrigin.Agent,
                    InteractionStatus.Rejected,
                    new RejectionInfo(
                        InteractionRejectionCode.CommandNotRegistered,
                        "The command is not registered."),
                    null,
                    StageProgress.Empty,
                    StateObservation.Empty,
                    StateObservation.Empty,
                    StateDiff.Empty);
                return new InteractionSubmission(
                    InteractionAdmissionKind.Completed,
                    message.RequestId!,
                    Calls,
                    Task.FromResult(false),
                    Task.FromResult(rejected));
            }

            started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completion = new TaskCompletionSource<InteractionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new InteractionSubmission(
                InteractionAdmissionKind.Queued,
                message.RequestId!,
                Calls,
                started.Task,
                completion.Task);
        }

        public void Start()
        {
            started!.TrySetResult(true);
        }

        public void Complete(InteractionResult result)
        {
            started!.TrySetResult(true);
            completion!.TrySetResult(result);
        }
    }

    // The session's side of an in-memory duplex; the test drives the other
    // side directly with encoded payloads.
    internal sealed class InMemoryDuplexChannel : IProtocolChannel
    {
        private readonly Channel<ProtocolChannelFrame> toSession =
            Channel.CreateUnbounded<ProtocolChannelFrame>();

        private readonly Channel<byte[]> toHost = Channel.CreateUnbounded<byte[]>();

        public bool FailSends { get; set; }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken)
        {
            if (FailSends)
            {
                throw new InvalidOperationException("The transport dropped the send.");
            }

            return toHost.Writer.WriteAsync(message.ToArray(), cancellationToken);
        }

        public async ValueTask<ProtocolChannelFrame> ReceiveAsync(
            int maxMessageBytes,
            CancellationToken cancellationToken)
        {
            ProtocolChannelFrame frame;
            try
            {
                frame = await toSession.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return ProtocolChannelFrame.Closed();
            }

            if (frame.Kind == ProtocolChannelFrameKind.Message
                && frame.Payload!.Length > maxMessageBytes)
            {
                return ProtocolChannelFrame.Overflow();
            }

            return frame;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            toSession.Writer.TryComplete();
            return default;
        }

        public void HostDeliver(byte[] payload)
        {
            toSession.Writer.TryWrite(ProtocolChannelFrame.Message(payload));
        }

        public void HostClose()
        {
            toSession.Writer.TryComplete();
        }

        public Task<byte[]> HostReceiveAsync()
        {
            return toHost.Reader.ReadAsync().AsTask();
        }

        public void Dispose()
        {
            toSession.Writer.TryComplete();
            toHost.Writer.TryComplete();
        }
    }
}
