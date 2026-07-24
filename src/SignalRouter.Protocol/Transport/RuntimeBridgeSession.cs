using System;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRouter.Protocol.Transport
{
    // How the bridge session reaches the runtime it fronts. Every callback is
    // invoked on the runtime's main thread (the session marshals through
    // PostToMainThread per design §17.2); the session itself never touches
    // registry, dispatcher, or ledger off that thread.
    public sealed class RuntimeBridgeSessionOptions
    {
        public RuntimeBridgeSessionOptions(
            ProtocolRequestLedger ledger,
            ProtocolPeerOptions localOptions,
            Action<Action> postToMainThread,
            Func<ExecuteInteractionMessage, InteractionSubmission> submit,
            Func<string, bool> tryCancel,
            Func<RegistrySnapshotDocument> captureSnapshot,
            Action<WaitForMessage, Action<bool, long>> beginWait,
            string? authToken = null,
            Func<string>? messageIdSource = null,
            Action<RuntimeControlRequest, Action<RuntimeControlAck>>? beginControlOperation = null,
            Action<string, Action<RuntimeControlQueryResult>>? queryControlOperation = null)
        {
            Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            LocalOptions = localOptions ?? throw new ArgumentNullException(nameof(localOptions));
            PostToMainThread = postToMainThread
                ?? throw new ArgumentNullException(nameof(postToMainThread));
            Submit = submit ?? throw new ArgumentNullException(nameof(submit));
            TryCancel = tryCancel ?? throw new ArgumentNullException(nameof(tryCancel));
            CaptureSnapshot = captureSnapshot
                ?? throw new ArgumentNullException(nameof(captureSnapshot));
            BeginWait = beginWait ?? throw new ArgumentNullException(nameof(beginWait));
            ProtocolContract.RequireOptionalIdentifier(authToken, nameof(authToken));
            AuthToken = authToken;
            MessageIdSource = messageIdSource ?? DefaultMessageId;

            // Without a runtime supervisor wired, control operations are refused
            // rather than dropped: a host that sends one gets a clean answer, not a
            // hang. The supervisor (item 8d) supplies the real handlers.
            BeginControlOperation = beginControlOperation ?? RefuseControlOperation;
            QueryControlOperation = queryControlOperation ?? RefuseControlQuery;
        }

        public ProtocolRequestLedger Ledger { get; }

        public ProtocolPeerOptions LocalOptions { get; }

        public Action<Action> PostToMainThread { get; }

        public Func<ExecuteInteractionMessage, InteractionSubmission> Submit { get; }

        public Func<string, bool> TryCancel { get; }

        public Func<RegistrySnapshotDocument> CaptureSnapshot { get; }

        // Registers a wait on the runtime's frame loop (main thread); the
        // completion callback reports (satisfied, elapsedMs) exactly once, on
        // the main thread, when the condition holds or the timeout passes.
        public Action<WaitForMessage, Action<bool, long>> BeginWait { get; }

        public string? AuthToken { get; }

        public Func<string> MessageIdSource { get; }

        // Hands a recording/replay control operation to the runtime supervisor and
        // fires the completion callback exactly once, on the main thread, with the
        // terminal ack.
        public Action<RuntimeControlRequest, Action<RuntimeControlAck>> BeginControlOperation { get; }

        // Answers a get_control_operation_result query from the supervisor's
        // operation ledger, on the main thread.
        public Action<string, Action<RuntimeControlQueryResult>> QueryControlOperation { get; }

        private static string DefaultMessageId()
        {
            return Guid.NewGuid().ToString("N");
        }

        private static void RefuseControlOperation(
            RuntimeControlRequest request,
            Action<RuntimeControlAck> complete)
        {
            complete(RuntimeControlAck.Refused(
                request.OperationId,
                ProtocolErrorCodes.RecordingUnavailable,
                "The runtime has no recording supervisor."));
        }

        private static void RefuseControlQuery(
            string operationId,
            Action<RuntimeControlQueryResult> complete)
        {
            complete(RuntimeControlQueryResult.Terminal(RuntimeControlAck.Refused(
                operationId,
                ProtocolErrorCodes.RecordingUnavailable,
                "The runtime has no recording supervisor.")));
        }
    }

    // The agent-view semantic snapshot the runtime serves for
    // get_registry_snapshot (design §13, §18.2).
    public readonly struct RegistrySnapshotDocument
    {
        public RegistrySnapshotDocument(int probeVersion, string snapshotJson)
        {
            if (probeVersion < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(probeVersion),
                    probeVersion,
                    "Probe version must be positive.");
            }

            ProtocolContract.RequireJsonObject(
                snapshotJson,
                ProtocolLimits.SnapshotMaxDepth,
                nameof(snapshotJson));
            ProbeVersion = probeVersion;
            SnapshotJson = snapshotJson;
        }

        public int ProbeVersion { get; }

        public string SnapshotJson { get; }
    }

    // One connection's runtime-side protocol driver (design §17.2, §18): owns
    // the handshake, the receive loop, and the ledger bookkeeping, but no
    // socket and no thread — the channel is injected, receive-side parsing
    // runs on whatever thread awaits RunAsync, and everything that touches the
    // runtime or the ledger is marshalled through PostToMainThread. Send
    // failures deliberately drop the connection: the ledger makes reconnect
    // recovery correct, so the send path stays simple and brutal (ADR 0007).
    public sealed class RuntimeBridgeSession
    {
        private readonly IProtocolChannel channel;
        private readonly RuntimeBridgeSessionOptions options;
        private readonly ProtocolConnectionStateMachine machine;
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource loopCancellation;
        private ProtocolSession? session;

        public RuntimeBridgeSession(
            IProtocolChannel channel,
            RuntimeBridgeSessionOptions options,
            CancellationToken cancellationToken = default)
        {
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            machine = new ProtocolConnectionStateMachine(
                ProtocolConnectionRole.Runtime,
                options.LocalOptions);
            loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public ProtocolSession? Session
        {
            get { return session; }
        }

        // Drives the connection to completion: handshake, then the receive
        // loop until the channel closes, the peer misbehaves fatally, or the
        // caller cancels. Completes normally in every one of those cases —
        // session-local teardown (a failed send, a rejected post) must look
        // identical to a peer disconnect so the owning reconnect loop simply
        // retries. Always closes the channel on the way out, with a bounded
        // close so a peer that ignores the handshake cannot wedge the loop.
        public async Task RunAsync()
        {
            var cancellationToken = loopCancellation.Token;
            try
            {
                if (!await PerformHandshakeAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation — external or session-local — is a normal way
                // for a connection to end, never an error of RunAsync itself.
            }
            finally
            {
                machine.Close();
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await channel.CloseAsync(closeTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The channel is already unusable; RunAsync's contract is
                    // that the connection is finished either way, and the
                    // reconnect loop owns what happens next.
                }
            }
        }

        private async Task<bool> PerformHandshakeAsync(CancellationToken cancellationToken)
        {
            var hello = new HelloMessage(
                options.MessageIdSource(),
                options.Ledger.SessionEpoch,
                options.LocalOptions.PeerVersion,
                options.LocalOptions.Capabilities,
                options.LocalOptions.MaxReceiveMessageBytes,
                options.AuthToken,
                ToRecoveryWindowMs(options.Ledger.Retention));
            machine.OnHelloSent(hello);
            await SendAsync(hello, ProtocolLimits.BootstrapMaxMessageBytes, cancellationToken)
                .ConfigureAwait(false);

            var frame = await channel.ReceiveAsync(
                ProtocolLimits.BootstrapMaxMessageBytes,
                cancellationToken).ConfigureAwait(false);
            if (frame.Kind != ProtocolChannelFrameKind.Message)
            {
                return false;
            }

            var read = ProtocolMessageReader.Read(
                frame.Payload!,
                ProtocolLimits.BootstrapMaxMessageBytes);
            if (read.Status != ProtocolReadStatus.Success)
            {
                await TrySendReadFailureAsync(read, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var decision = machine.OnMessageReceived(read.Message!);
            if (decision.Verdict != ProtocolConnectionVerdict.Accept
                || machine.Phase != ProtocolConnectionPhase.Ready)
            {
                if (decision.ErrorCode != null)
                {
                    await TrySendErrorAsync(
                        decision.ErrorCode,
                        decision.ErrorMessage!,
                        null,
                        read.Message!.MessageId,
                        cancellationToken).ConfigureAwait(false);
                }

                return false;
            }

            session = machine.Session;
            return true;
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var negotiated = session!;
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await channel.ReceiveAsync(
                    negotiated.MaxReceiveMessageBytes,
                    cancellationToken).ConfigureAwait(false);
                if (frame.Kind == ProtocolChannelFrameKind.Closed)
                {
                    return;
                }

                if (frame.Kind == ProtocolChannelFrameKind.Overflow)
                {
                    // The channel already aborted the socket; nothing can be
                    // replied on it.
                    return;
                }

                var read = ProtocolMessageReader.Read(
                    frame.Payload!,
                    negotiated.MaxReceiveMessageBytes);
                if (read.Status != ProtocolReadStatus.Success)
                {
                    await TrySendReadFailureAsync(read, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var decision = machine.OnMessageReceived(read.Message!);
                switch (decision.Verdict)
                {
                    case ProtocolConnectionVerdict.Accept:
                        HandleAccepted(read.Message!, cancellationToken);
                        break;
                    case ProtocolConnectionVerdict.Reject:
                        await TrySendErrorAsync(
                            decision.ErrorCode!,
                            decision.ErrorMessage!,
                            read.Message!.RequestId,
                            read.Message.MessageId,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        await TrySendErrorAsync(
                            decision.ErrorCode!,
                            decision.ErrorMessage!,
                            read.Message!.RequestId,
                            read.Message.MessageId,
                            cancellationToken).ConfigureAwait(false);
                        return;
                }
            }
        }

        private void HandleAccepted(ProtocolMessage message, CancellationToken cancellationToken)
        {
            switch (message)
            {
                case ExecuteInteractionMessage execute:
                    TryPost(() => HandleExecute(execute, cancellationToken));
                    return;
                case GetInteractionResultMessage query:
                    TryPost(() => HandleQuery(query, cancellationToken));
                    return;
                case CancelInteractionMessage cancel:
                    TryPost(() =>
                    {
                        options.Ledger.TryMarkCancelRequested(cancel.RequestId!);
                        options.TryCancel(cancel.RequestId!);
                    });
                    return;
                case GetRegistrySnapshotMessage snapshotRequest:
                    TryPost(() => HandleSnapshotRequest(snapshotRequest, cancellationToken));
                    return;
                case WaitForMessage waitFor:
                    TryPost(() => options.BeginWait(
                        waitFor,
                        (satisfied, elapsedMs) => FireSend(
                            new WaitResultMessage(
                                options.MessageIdSource(),
                                session!.SessionEpoch,
                                waitFor.MessageId,
                                waitFor.Condition,
                                satisfied,
                                elapsedMs),
                            cancellationToken)));
                    return;
                case PingMessage ping:
                    // The pong round-trips through the main-thread pump on
                    // purpose: a protocol-level ping proves the runtime can
                    // still service work, not merely that the socket loop is
                    // alive (§17.2).
                    TryPost(() => FireSend(
                        new PongMessage(
                            options.MessageIdSource(),
                            ping.MessageId,
                            session!.SessionEpoch),
                        cancellationToken));
                    return;
                case StartRecordingMessage start:
                    PostControlOperation(
                        new RuntimeControlRequest(
                            RuntimeControlKind.StartRecording,
                            start.OperationId,
                            start.MessageId,
                            null,
                            start.Label),
                        cancellationToken);
                    return;
                case StopRecordingMessage stop:
                    PostControlOperation(
                        new RuntimeControlRequest(
                            RuntimeControlKind.StopRecording,
                            stop.OperationId,
                            stop.MessageId,
                            null,
                            null),
                        cancellationToken);
                    return;
                case ReplayRecordingMessage replay:
                    PostControlOperation(
                        new RuntimeControlRequest(
                            RuntimeControlKind.ReplayRecording,
                            replay.OperationId,
                            replay.MessageId,
                            replay.RecordingHandle,
                            null),
                        cancellationToken);
                    return;
                case GetControlOperationResultMessage controlQuery:
                    TryPost(() => options.QueryControlOperation(
                        controlQuery.OperationId,
                        result => FireSend(
                            BuildControlOperationResult(controlQuery.OperationId, result),
                            cancellationToken)));
                    return;
                default:
                    // Pong and error carry no runtime action in v1; the state
                    // machine already filtered everything else.
                    return;
            }
        }

        private void PostControlOperation(
            RuntimeControlRequest request,
            CancellationToken cancellationToken)
        {
            // The supervisor runs the operation on the main thread and calls back
            // exactly once with the terminal ack, which the session maps onto the
            // wire. No epoch changes in v1, so the ack rides the same session.
            TryPost(() => options.BeginControlOperation(
                request,
                ack => FireSend(MapAckToMessage(request, ack), cancellationToken)));
        }

        private ProtocolMessage MapAckToMessage(RuntimeControlRequest request, RuntimeControlAck ack)
        {
            var epoch = session!.SessionEpoch;
            var id = options.MessageIdSource();
            switch (ack.Kind)
            {
                case RuntimeControlAckKind.RecordingStarted:
                    return new RecordingStartedMessage(
                        id, epoch, ack.OperationId, ack.RecordingHandle!, epoch);
                case RuntimeControlAckKind.RecordingStopped:
                    return new RecordingStoppedMessage(
                        id, epoch, ack.OperationId, ack.RecordingHandle!, ack.EntryCount, epoch);
                case RuntimeControlAckKind.ReplayReport:
                    return new ReplayReportMessage(
                        id, epoch, ack.OperationId, ack.OutcomeKind!, epoch, ack.Detail);
                case RuntimeControlAckKind.Refused:
                    // A refusal correlates to the exact control message so the host
                    // completes the operation instead of hanging.
                    return new ErrorMessage(
                        id,
                        ack.RefusalCode!,
                        ack.Detail ?? "The control operation was refused.",
                        epoch,
                        null,
                        request.ControlMessageId);
                default:
                    throw new InvalidOperationException("Unknown control acknowledgment kind.");
            }
        }

        private ProtocolMessage BuildControlOperationResult(
            string operationId,
            RuntimeControlQueryResult result)
        {
            var epoch = session!.SessionEpoch;
            var id = options.MessageIdSource();
            if (result.TerminalAck == null)
            {
                return new ControlOperationResultMessage(id, epoch, operationId, result.State, epoch);
            }

            var ack = result.TerminalAck.Value;
            switch (ack.Kind)
            {
                case RuntimeControlAckKind.RecordingStarted:
                    return new ControlOperationResultMessage(
                        id, epoch, operationId, result.State, epoch,
                        recordingHandle: ack.RecordingHandle);
                case RuntimeControlAckKind.RecordingStopped:
                    return new ControlOperationResultMessage(
                        id, epoch, operationId, result.State, epoch,
                        recordingHandle: ack.RecordingHandle, entryCount: ack.EntryCount);
                case RuntimeControlAckKind.ReplayReport:
                    return new ControlOperationResultMessage(
                        id, epoch, operationId, result.State, epoch,
                        outcomeKind: ack.OutcomeKind, detail: ack.Detail);
                case RuntimeControlAckKind.Refused:
                    return new ControlOperationResultMessage(
                        id, epoch, operationId, result.State, epoch,
                        detail: ack.Detail ?? ack.RefusalCode);
                default:
                    throw new InvalidOperationException("Unknown control acknowledgment kind.");
            }
        }

        // A rejected post means the runtime is shutting down: this connection
        // cannot service work anymore, so it ends — quietly, like any other
        // disconnect — instead of letting the rejection escape as an unhandled
        // exception.
        private void TryPost(Action work)
        {
            try
            {
                options.PostToMainThread(work);
            }
            catch (Exception)
            {
                loopCancellation.Cancel();
            }
        }

        private void HandleExecute(
            ExecuteInteractionMessage execute,
            CancellationToken cancellationToken)
        {
            var submission = options.Ledger.Submit(execute);
            switch (submission.Status)
            {
                case ProtocolLedgerSubmissionStatus.Conflict:
                case ProtocolLedgerSubmissionStatus.CapacityExhausted:
                    FireSend(
                        new ErrorMessage(
                            options.MessageIdSource(),
                            submission.ErrorCode!,
                            submission.Status == ProtocolLedgerSubmissionStatus.Conflict
                                ? "The request ID is already used with different content."
                                : "The runtime cannot take new work without breaking retention.",
                            session!.SessionEpoch,
                            execute.RequestId,
                            execute.MessageId),
                        cancellationToken);
                    return;
                case ProtocolLedgerSubmissionStatus.Duplicate:
                    ReplyFromLedgerEntry(
                        submission.Entry!,
                        execute.MessageId,
                        cancellationToken);
                    return;
            }

            InteractionSubmission coreSubmission;
            try
            {
                coreSubmission = options.Submit(execute);
            }
            catch (Exception)
            {
                // Core refused admission (replay lease, disposed dispatcher):
                // no acceptance was sent and nothing queued, so the
                // reservation is the one thing that may be forgotten — an
                // honest resend must not become a false duplicate (ADR 0007).
                options.Ledger.Abandon(execute.RequestId!);
                FireSend(
                    new ErrorMessage(
                        options.MessageIdSource(),
                        ProtocolErrorCodes.RuntimeBusy,
                        "The runtime cannot accept interaction work right now.",
                        session!.SessionEpoch,
                        execute.RequestId,
                        execute.MessageId),
                    cancellationToken);
                return;
            }

            if (coreSubmission.Kind == InteractionAdmissionKind.Completed)
            {
                var immediate = ProtocolInteractionOutcome.FromResult(
                    coreSubmission.Completion.Result);
                options.Ledger.MarkTerminal(execute.RequestId!, immediate);
                FireSend(
                    new InteractionResultMessage(
                        options.MessageIdSource(),
                        session!.SessionEpoch,
                        immediate,
                        execute.MessageId),
                    cancellationToken);
                return;
            }

            options.Ledger.MarkQueued(execute.RequestId!, coreSubmission.Sequence);
            FireSend(
                new InteractionAcceptedMessage(
                    options.MessageIdSource(),
                    session!.SessionEpoch,
                    execute.RequestId!,
                    execute.MessageId,
                    coreSubmission.Sequence),
                cancellationToken);

            ObserveStarted(execute.RequestId!, coreSubmission.Started);
            ObserveCompletion(
                execute.RequestId!,
                execute.MessageId,
                coreSubmission.Completion,
                cancellationToken);
        }

        private async void ObserveStarted(string requestId, Task<bool> started)
        {
            bool ran;
            try
            {
                ran = await started.ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (ran)
            {
                TryPost(() =>
                {
                    // The completion continuation may have already marked the
                    // entry terminal on this same pump; states only move
                    // forward, so a late start notification is simply dropped.
                    var entry = options.Ledger.TryGet(requestId);
                    if (entry != null && entry.State == ProtocolRequestState.Queued)
                    {
                        options.Ledger.MarkRunning(requestId);
                    }
                });
            }
        }

        private async void ObserveCompletion(
            string requestId,
            string executeMessageId,
            Task<InteractionResult> completion,
            CancellationToken cancellationToken)
        {
            InteractionResult result;
            try
            {
                result = await completion.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The dispatch failed without a terminal result (a recording
                // append failure). The entry stays pending and the host's
                // query eventually surfaces OutcomeUnknown; inventing a
                // terminal outcome here would violate design §8.
                return;
            }

            var outcome = ProtocolInteractionOutcome.FromResult(result);
            TryPost(() =>
            {
                options.Ledger.MarkTerminal(requestId, outcome);
                FireSend(
                    new InteractionResultMessage(
                        options.MessageIdSource(),
                        session!.SessionEpoch,
                        outcome,
                        executeMessageId),
                    cancellationToken);
            });
        }

        private void HandleQuery(
            GetInteractionResultMessage query,
            CancellationToken cancellationToken)
        {
            var entry = options.Ledger.TryGet(query.RequestId!);
            if (entry == null)
            {
                FireSend(
                    new ErrorMessage(
                        options.MessageIdSource(),
                        ProtocolErrorCodes.ResultUnavailable,
                        "No result is retained for this request.",
                        session!.SessionEpoch,
                        query.RequestId,
                        query.MessageId),
                    cancellationToken);
                return;
            }

            ReplyFromLedgerEntry(entry, query.MessageId, cancellationToken);
        }

        private void ReplyFromLedgerEntry(
            ProtocolLedgerEntry entry,
            string inReplyTo,
            CancellationToken cancellationToken)
        {
            if (entry.State == ProtocolRequestState.Terminal)
            {
                FireSend(
                    new InteractionResultMessage(
                        options.MessageIdSource(),
                        session!.SessionEpoch,
                        entry.Outcome!,
                        inReplyTo),
                    cancellationToken);
                return;
            }

            FireSend(
                new InteractionStatusMessage(
                    options.MessageIdSource(),
                    session!.SessionEpoch,
                    entry.RequestId,
                    inReplyTo,
                    entry.State,
                    entry.Sequence,
                    entry.CancelRequested),
                cancellationToken);
        }

        private void HandleSnapshotRequest(
            GetRegistrySnapshotMessage request,
            CancellationToken cancellationToken)
        {
            var document = options.CaptureSnapshot();
            FireSend(
                new RegistrySnapshotMessage(
                    options.MessageIdSource(),
                    session!.SessionEpoch,
                    request.MessageId,
                    document.ProbeVersion,
                    document.SnapshotJson),
                cancellationToken);
        }

        private async Task TrySendReadFailureAsync(
            ProtocolReadResult read,
            CancellationToken cancellationToken)
        {
            await TrySendErrorAsync(
                read.ErrorCode!,
                read.ErrorMessage!,
                null,
                read.MessageId,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task TrySendErrorAsync(
            string code,
            string message,
            string? requestId,
            string? inReplyTo,
            CancellationToken cancellationToken)
        {
            var error = new ErrorMessage(
                options.MessageIdSource(),
                code,
                message,
                session?.SessionEpoch,
                requestId,
                inReplyTo);
            try
            {
                await SendAsync(
                    error,
                    session?.MaxSendMessageBytes ?? ProtocolLimits.BootstrapMaxMessageBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection is dying; the receive loop will observe the
                // closed channel and the reconnect loop takes over.
            }
        }

        // Fire-and-forget send used by main-thread continuations. A failure
        // tears the connection down via the loop cancellation: the ledger
        // preserves every answer, so dropping the connection is always
        // recoverable and always simpler than per-send error handling.
        private void FireSend(ProtocolMessage message, CancellationToken cancellationToken)
        {
            _ = FireSendAsync(message, cancellationToken);
        }

        private async Task FireSendAsync(
            ProtocolMessage message,
            CancellationToken cancellationToken)
        {
            // An encode overflow is a deterministic size verdict, not a broken
            // connection: dropping the socket would make every reconnect and
            // query repeat the same failure. The peer gets payload_too_large
            // with the same correlation instead (design §19). Errors are
            // always small; if even the error overflows something is deeply
            // wrong and the connection drops.
            byte[] encoded;
            try
            {
                encoded = ProtocolMessageWriter.Encode(message, session!.MaxSendMessageBytes);
            }
            catch (InvalidOperationException) when (!(message is ErrorMessage))
            {
                message = new ErrorMessage(
                    options.MessageIdSource(),
                    ProtocolErrorCodes.PayloadTooLarge,
                    "The reply exceeds the peer's advertised receive limit.",
                    session!.SessionEpoch,
                    message.RequestId,
                    message.InReplyTo);
                try
                {
                    encoded = ProtocolMessageWriter.Encode(message, session.MaxSendMessageBytes);
                }
                catch (InvalidOperationException)
                {
                    loopCancellation.Cancel();
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                loopCancellation.Cancel();
                return;
            }

            try
            {
                await SendEncodedAsync(encoded, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                loopCancellation.Cancel();
            }
        }

        private async Task SendAsync(
            ProtocolMessage message,
            int maxMessageBytes,
            CancellationToken cancellationToken)
        {
            var encoded = ProtocolMessageWriter.Encode(message, maxMessageBytes);
            await SendEncodedAsync(encoded, cancellationToken).ConfigureAwait(false);
        }

        private async Task SendEncodedAsync(
            byte[] encoded,
            CancellationToken cancellationToken)
        {
            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await channel.SendAsync(encoded, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }

        private static int ToRecoveryWindowMs(TimeSpan retention)
        {
            var totalMs = retention.TotalMilliseconds;
            return totalMs >= int.MaxValue ? int.MaxValue : (int)totalMs;
        }
    }
}
