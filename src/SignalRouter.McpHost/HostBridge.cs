using SignalRouter;
using SignalRouter.Protocol;
using SignalRouter.Protocol.Transport;

namespace SignalRouter.McpHost;

public sealed class HostBridgeOptions
{
    public HostBridgeOptions(
        string peerVersion,
        TimeSpan toolTimeout,
        TimeSpan replyTimeout,
        Func<string>? messageIdSource = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrEmpty(peerVersion))
        {
            throw new ArgumentException("A peer version is required.", nameof(peerVersion));
        }

        if (toolTimeout <= TimeSpan.Zero || replyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toolTimeout),
                "Timeouts must be positive.");
        }

        PeerVersion = peerVersion;
        ToolTimeout = toolTimeout;
        ReplyTimeout = replyTimeout;
        MessageIdSource = messageIdSource ?? (() => Guid.NewGuid().ToString("N"));
        Clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static HostBridgeOptions CreateDefault()
    {
        // The tool timeout stays well under typical MCP client timeouts so a
        // long-running interaction answers {status: pending} instead of
        // hanging the stdio call (plan 8c).
        return new HostBridgeOptions(
            "SignalRouter.McpHost " + (typeof(HostBridge).Assembly.GetName().Version?.ToString() ?? "dev"),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(10));
    }

    public string PeerVersion { get; }

    public TimeSpan ToolTimeout { get; }

    public TimeSpan ReplyTimeout { get; }

    public Func<string> MessageIdSource { get; }

    public Func<DateTimeOffset> Clock { get; }
}

// How one execute request ultimately answered, as seen by the MCP surface.
public sealed class HostExecuteReport
{
    private HostExecuteReport(
        string status,
        string requestId,
        ProtocolInteractionOutcome? outcome,
        string? errorCode,
        string? detail)
    {
        Status = status;
        RequestId = requestId;
        Outcome = outcome;
        ErrorCode = errorCode;
        Detail = detail;
    }

    // "completed" | "pending" | "outcome_unknown" | "refused" | "disconnected"
    public string Status { get; }

    public string RequestId { get; }

    public ProtocolInteractionOutcome? Outcome { get; }

    public string? ErrorCode { get; }

    public string? Detail { get; }

    public static HostExecuteReport Completed(string requestId, ProtocolInteractionOutcome outcome)
        => new("completed", requestId, outcome, null, null);

    public static HostExecuteReport Pending(string requestId)
        => new("pending", requestId, null, null, null);

    public static HostExecuteReport OutcomeUnknown(string requestId, string detail)
        => new("outcome_unknown", requestId, null, null, detail);

    public static HostExecuteReport Refused(string requestId, string errorCode, string detail)
        => new("refused", requestId, null, errorCode, detail);

    public static HostExecuteReport Disconnected(string requestId)
        => new("disconnected", requestId, null, null, "No runtime session has connected yet.");
}

// How a recording or replay control operation ultimately answered.
public sealed class HostOperationReport
{
    private HostOperationReport(
        string status,
        string operationId,
        string? recordingHandle,
        long? entryCount,
        string? newSessionEpoch,
        string? outcomeKind,
        string? detail)
    {
        Status = status;
        OperationId = operationId;
        RecordingHandle = recordingHandle;
        EntryCount = entryCount;
        NewSessionEpoch = newSessionEpoch;
        OutcomeKind = outcomeKind;
        Detail = detail;
    }

    // "recording_started" | "recording_stopped" | "replayed" | "pending" |
    // "disconnected"
    public string Status { get; }

    public string OperationId { get; }

    public string? RecordingHandle { get; }

    public long? EntryCount { get; }

    public string? NewSessionEpoch { get; }

    public string? OutcomeKind { get; }

    public string? Detail { get; }

    public static HostOperationReport RecordingStarted(
        string operationId,
        string handle,
        string newEpoch)
        => new("recording_started", operationId, handle, null, newEpoch, null, null);

    public static HostOperationReport RecordingStopped(
        string operationId,
        string handle,
        long entryCount,
        string newEpoch)
        => new("recording_stopped", operationId, handle, entryCount, newEpoch, null, null);

    public static HostOperationReport Replayed(
        string operationId,
        string outcomeKind,
        string newEpoch,
        string? detail)
        => new("replayed", operationId, null, null, newEpoch, outcomeKind, detail);

    public static HostOperationReport Refused(string operationId, string errorCode, string detail)
        => new("refused", operationId, null, null, null, null, errorCode + ": " + detail);

    // A refusal reconstructed from a queried control_operation_result, whose
    // detail already carries the runtime's non-secret summary.
    public static HostOperationReport RefusedDetail(string operationId, string? detail)
        => new("refused", operationId, null, null, null, null, detail);

    public static HostOperationReport Pending(string operationId)
        => new("pending", operationId, null, null, null, null, null);

    public static HostOperationReport Disconnected()
        => new("disconnected", string.Empty, null, null, null, null, null);
}

// The host side of the loopback protocol (design §18.1): accepts exactly one
// runtime connection at a time, drives the Host-role state machine, correlates
// replies by message ID, and owns the query-first recovery flow — after a
// reconnect every non-terminal request is queried; an unavailable answer
// within the runtime-advertised recovery window resends the byte-exact
// original (the runtime ledger deduplicates), outside it the honest answer is
// OutcomeUnknown, and a changed session epoch fails everything pending as
// session_lost (design §8, ADR 0007). The channel is injected, so the whole
// contract is tested over an in-memory duplex with a scripted runtime peer.
public sealed class HostBridge : IDisposable
{
    private readonly object gate = new();
    private readonly HostBridgeOptions options;
    private readonly ProtocolPeerOptions peerOptions;
    private readonly Dictionary<string, PendingExecute> pendingExecutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<ProtocolMessage>> pendingReplies =
        new(StringComparer.Ordinal);

    // Recording and replay operations, keyed by their host-assigned operation
    // ID. Unlike executes and replies, these deliberately SURVIVE an epoch
    // transition: the operation itself causes the epoch change, and its
    // acknowledgment arrives on the reconnected session under the new epoch
    // (item 8d).
    private readonly Dictionary<string, PendingOperation> pendingOperations =
        new(StringComparer.Ordinal);

    // Maps a control message's transmission ID to its operation ID so an
    // error reply (correlated by inReplyTo, and without a request ID) can
    // complete the operation as a refusal instead of hanging until the tool
    // timeout (Codex review, item 8d).
    private readonly Dictionary<string, string> controlMessageToOperation =
        new(StringComparer.Ordinal);

    // Outstanding get_control_operation_result queries, keyed by the operation ID
    // they ask about. A query reconciles an operation the tool call could not
    // observe synchronously — after its timeout, or after a reconnect (item 8d).
    private readonly Dictionary<string, TaskCompletionSource<ControlOperationResultMessage>> pendingQueries =
        new(StringComparer.Ordinal);

    // `active` claims the single-runtime slot at accept time; `ready` is the
    // same connection once its handshake completed. Tool calls only ever see
    // `ready`, so a half-open connection can neither receive protocol traffic
    // nor masquerade as a live session.
    private ActiveConnection? active;
    private ActiveConnection? ready;
    private string? sessionEpoch;
    private bool disposed;

    public HostBridge(HostBridgeOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        peerOptions = new ProtocolPeerOptions(
            options.PeerVersion,
            Array.Empty<string>(),
            ProtocolLimits.DefaultMaxReceiveMessageBytes);
    }

    public bool IsConnected
    {
        get
        {
            lock (gate)
            {
                return ready != null;
            }
        }
    }

    public string? SessionEpoch
    {
        get
        {
            lock (gate)
            {
                return sessionEpoch;
            }
        }
    }

    // Drives one accepted runtime connection to completion. The MVP topology
    // is one runtime per host (plan 8c): a second concurrent connection is
    // refused with an explicit error instead of silently displacing the first.
    public async Task RunConnectionAsync(IProtocolChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        ActiveConnection connection;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active != null)
            {
                connection = null!;
            }
            else
            {
                connection = new ActiveConnection(channel);
                active = connection;
            }
        }

        if (connection == null)
        {
            await TrySendRawAsync(
                channel,
                new ErrorMessage(
                    options.MessageIdSource(),
                    ProtocolErrorCodes.RuntimeBusy,
                    "The host already has a connected runtime."),
                ProtocolLimits.BootstrapMaxMessageBytes,
                cancellationToken).ConfigureAwait(false);
            await CloseQuietlyAsync(channel).ConfigureAwait(false);
            return;
        }

        try
        {
            if (await PerformHandshakeAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                lock (gate)
                {
                    ready = connection;
                }

                await BeginRecoveryAsync(connection, cancellationToken).ConfigureAwait(false);
                await ReceiveLoopAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A vanished connection and a cancelled one look identical to the
            // pending table: requests stay pending for the next connection.
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(active, connection))
                {
                    active = null;
                }

                if (ReferenceEquals(ready, connection))
                {
                    ready = null;
                }
            }

            connection.Machine?.Close();
            await CloseQuietlyAsync(channel).ConfigureAwait(false);
        }
    }

    // Submits one interaction and waits up to the tool timeout for its
    // terminal outcome; a slower interaction answers "pending" and stays
    // recoverable via get_interaction_result (design §18.2).
    public async Task<HostExecuteReport> ExecuteInteractionAsync(
        string? callerRequestId,
        string targetId,
        string commandName,
        int commandVersion,
        string argumentsJson,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var requestId = callerRequestId ?? Guid.NewGuid().ToString("N");

        PendingExecute pending;
        ActiveConnection? connection;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessionEpoch == null)
            {
                return HostExecuteReport.Disconnected(requestId);
            }

            if (pendingExecutes.ContainsKey(requestId))
            {
                return HostExecuteReport.Refused(
                    requestId,
                    ProtocolErrorCodes.RequestIdConflict,
                    "The request ID is already in flight on this host.");
            }

            var execute = new ExecuteInteractionMessage(
                options.MessageIdSource(),
                sessionEpoch,
                requestId,
                commandName,
                commandVersion,
                targetId,
                argumentsJson,
                null,
                idempotencyKey);
            pending = new PendingExecute(
                requestId,
                ProtocolMessageWriter.Encode(execute, ProtocolLimits.DefaultMaxReceiveMessageBytes));
            pendingExecutes.Add(requestId, pending);
            connection = ready;
        }

        if (connection != null)
        {
            pending.MarkSendAttempted(options.Clock());
            await TrySendEncodedAsync(connection, pending.EncodedExecute, cancellationToken)
                .ConfigureAwait(false);
        }

        var completed = await Task.WhenAny(
            pending.Completion.Task,
            Task.Delay(options.ToolTimeout, cancellationToken)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, pending.Completion.Task))
        {
            return HostExecuteReport.Pending(requestId);
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    // Queries a request by ID; answers the terminal outcome, a pending status
    // projection, or outcome_unknown per §8.
    public async Task<ProtocolMessage?> GetInteractionResultAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        ActiveConnection? connection;
        string? epoch;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            connection = ready;
            epoch = sessionEpoch;
        }

        if (connection == null || epoch == null)
        {
            return null;
        }

        var query = new GetInteractionResultMessage(
            options.MessageIdSource(),
            epoch,
            requestId);
        return await SendAndAwaitReplyAsync(connection, query, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RegistrySnapshotMessage?> GetRegistrySnapshotAsync(
        CancellationToken cancellationToken)
    {
        ActiveConnection? connection;
        string? epoch;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            connection = ready;
            epoch = sessionEpoch;
        }

        if (connection == null || epoch == null)
        {
            return null;
        }

        var request = new GetRegistrySnapshotMessage(options.MessageIdSource(), epoch);
        var reply = await SendAndAwaitReplyAsync(connection, request, cancellationToken)
            .ConfigureAwait(false);
        return reply as RegistrySnapshotMessage;
    }

    public async Task<WaitResultMessage?> WaitForAsync(
        string condition,
        string? targetId,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        ActiveConnection? connection;
        string? epoch;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            connection = ready;
            epoch = sessionEpoch;
        }

        if (connection == null || epoch == null)
        {
            return null;
        }

        var request = new WaitForMessage(
            options.MessageIdSource(),
            epoch,
            condition,
            targetId,
            timeoutMs);
        var reply = await SendAndAwaitWaitAsync(connection, request, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        return reply as WaitResultMessage;
    }

    // Records cancel intent and forwards it; the intent is resent after every
    // reconnect until a terminal result arrives (ADR 0007: cancellation is
    // idempotent intent, never a guarantee).
    public async Task<bool> CancelInteractionAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        ActiveConnection? connection;
        string? epoch;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!pendingExecutes.TryGetValue(requestId, out var pending))
            {
                return false;
            }

            pending.CancelRequested = true;
            connection = ready;
            epoch = sessionEpoch;
        }

        if (connection != null && epoch != null)
        {
            await TrySendAsync(
                connection,
                new CancelInteractionMessage(options.MessageIdSource(), epoch, requestId),
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    // Begins recording a fresh session. The runtime recreates itself under a
    // new epoch and answers via recording_started on the reconnected session,
    // correlated by operationId across the epoch change.
    public Task<HostOperationReport> StartRecordingAsync(
        string? label,
        CancellationToken cancellationToken)
    {
        return RunControlOperationAsync(
            HostControlOperationKind.StartRecording,
            operationId => new StartRecordingMessage(
                options.MessageIdSource(),
                RequireEpoch(),
                operationId,
                label),
            cancellationToken);
    }

    public Task<HostOperationReport> StopRecordingAsync(CancellationToken cancellationToken)
    {
        return RunControlOperationAsync(
            HostControlOperationKind.StopRecording,
            operationId => new StopRecordingMessage(
                options.MessageIdSource(),
                RequireEpoch(),
                operationId),
            cancellationToken);
    }

    public Task<HostOperationReport> ReplayRecordingAsync(
        string recordingHandle,
        CancellationToken cancellationToken)
    {
        return RunControlOperationAsync(
            HostControlOperationKind.ReplayRecording,
            operationId => new ReplayRecordingMessage(
                options.MessageIdSource(),
                RequireEpoch(),
                operationId,
                recordingHandle),
            cancellationToken);
    }

    // Reconciles a control operation the caller only saw as "pending" — because
    // the tool timed out or the connection dropped. Queries the runtime's
    // operation ledger by ID; a terminal answer clears the single-flight slot so
    // new control operations can proceed (item 8d).
    public async Task<HostOperationReport> QueryControlOperationAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            throw new ArgumentException(
                "The operation ID must be provided.",
                nameof(operationId));
        }

        ActiveConnection? connection;
        TaskCompletionSource<ControlOperationResultMessage> query;
        GetControlOperationResultMessage? message = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessionEpoch == null || ready == null)
            {
                return HostOperationReport.Disconnected();
            }

            // Coalesce concurrent queries for the same operation onto one waiter,
            // so a single control_operation_result reaches every caller instead of
            // the last registration alone (Codex review, item 8d). Only the owner —
            // the caller that created the waiter — sends the query and clears it.
            if (pendingQueries.TryGetValue(operationId, out var existing))
            {
                query = existing;
            }
            else
            {
                query = new TaskCompletionSource<ControlOperationResultMessage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pendingQueries[operationId] = query;
                message = new GetControlOperationResultMessage(
                    options.MessageIdSource(),
                    sessionEpoch,
                    operationId);
            }

            connection = ready;
        }

        var isOwner = message != null;
        if (isOwner)
        {
            await TrySendAsync(connection, message!, cancellationToken).ConfigureAwait(false);
        }

        var completed = await Task.WhenAny(
            query.Task,
            Task.Delay(options.ToolTimeout, cancellationToken)).ConfigureAwait(false);

        if (isOwner)
        {
            lock (gate)
            {
                if (pendingQueries.TryGetValue(operationId, out var current)
                    && ReferenceEquals(current, query))
                {
                    pendingQueries.Remove(operationId);
                }
            }
        }

        if (!ReferenceEquals(completed, query.Task))
        {
            return HostOperationReport.Pending(operationId);
        }

        return ResolveQueryResult(await query.Task.ConfigureAwait(false));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }
    }

    private async Task<HostOperationReport> RunControlOperationAsync(
        HostControlOperationKind kind,
        Func<string, ProtocolMessage> build,
        CancellationToken cancellationToken)
    {
        var operationId = options.MessageIdSource() + "-op";
        PendingOperation pending;
        ActiveConnection? connection;
        ProtocolMessage message;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessionEpoch == null || ready == null)
            {
                return HostOperationReport.Disconnected();
            }

            // Single-flight: one control operation at a time. The runtime enforces
            // the same, but refusing here avoids a wasted round trip. A prior
            // operation that only timed out still holds the slot until a late ack
            // or a query resolves it.
            if (pendingOperations.Count > 0)
            {
                return HostOperationReport.Refused(
                    operationId,
                    ProtocolErrorCodes.ControlInProgress,
                    "another control operation is in progress");
            }

            message = build(operationId);
            pending = new PendingOperation(operationId, kind, message);
            pending.MarkSendAttempted(options.Clock());
            pendingOperations.Add(operationId, pending);
            controlMessageToOperation[message.MessageId] = operationId;
            connection = ready;
        }

        await TrySendAsync(connection, message, cancellationToken).ConfigureAwait(false);

        // Control operations span a maintenance quiesce (and, for replay, an
        // isolated verification run), so they get the tool timeout rather than the
        // shorter reply budget.
        var completed = await Task.WhenAny(
            pending.Completion.Task,
            Task.Delay(options.ToolTimeout, cancellationToken)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, pending.Completion.Task))
        {
            // Gap B (Codex review): do NOT drop the operation on timeout. It stays
            // tracked so a late acknowledgment completes it, a reconnect resends
            // it, or a query reconciles it — and single-flight keeps blocking new
            // operations until then.
            return HostOperationReport.Pending(operationId);
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private string RequireEpoch()
    {
        return sessionEpoch
            ?? throw new InvalidOperationException("No runtime session is connected.");
    }

    private async Task<bool> PerformHandshakeAsync(
        ActiveConnection connection,
        CancellationToken cancellationToken)
    {
        // Bounded: a client that connects but never says hello occupies the
        // host's only runtime slot, locking every legitimate reconnect out
        // with runtime_busy until it goes away. The timeout releases the slot.
        using var handshakeBudget = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        handshakeBudget.CancelAfter(options.ReplyTimeout);

        var frame = await connection.Channel.ReceiveAsync(
            ProtocolLimits.BootstrapMaxMessageBytes,
            handshakeBudget.Token).ConfigureAwait(false);
        if (frame.Kind != ProtocolChannelFrameKind.Message)
        {
            return false;
        }

        var read = ProtocolMessageReader.Read(
            frame.Payload!,
            ProtocolLimits.BootstrapMaxMessageBytes);
        if (read.Status != ProtocolReadStatus.Success)
        {
            await TrySendRawAsync(
                connection.Channel,
                new ErrorMessage(
                    options.MessageIdSource(),
                    read.ErrorCode!,
                    read.ErrorMessage!,
                    null,
                    null,
                    read.MessageId),
                ProtocolLimits.BootstrapMaxMessageBytes,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        connection.Machine = new ProtocolConnectionStateMachine(
            ProtocolConnectionRole.Host,
            peerOptions);
        var decision = connection.Machine!.OnMessageReceived(read.Message!);
        if (decision.Verdict != ProtocolConnectionVerdict.Accept
            || connection.Machine.Session == null)
        {
            await TrySendRawAsync(
                connection.Channel,
                new ErrorMessage(
                    options.MessageIdSource(),
                    decision.ErrorCode ?? ProtocolErrorCodes.MalformedMessage,
                    decision.ErrorMessage ?? "The handshake failed.",
                    null,
                    null,
                    read.Message!.MessageId),
                ProtocolLimits.BootstrapMaxMessageBytes,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        var hello = (HelloMessage)read.Message!;
        connection.Session = connection.Machine.Session;
        HandleEpochTransition(hello.SessionEpoch!);

        var welcome = new WelcomeMessage(
            options.MessageIdSource(),
            hello.SessionEpoch!,
            hello.MessageId,
            options.PeerVersion,
            peerOptions.Capabilities,
            peerOptions.MaxReceiveMessageBytes,
            connection.Session.Version);
        await TrySendRawAsync(
            connection.Channel,
            welcome,
            ProtocolLimits.BootstrapMaxMessageBytes,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    // A changed epoch means the runtime was recreated: nothing pending can
    // ever complete, and inventing outcomes is forbidden (§8, §13.3).
    private void HandleEpochTransition(string newEpoch)
    {
        List<PendingExecute>? lost = null;
        List<TaskCompletionSource<ProtocolMessage>>? orphanedReplies = null;
        lock (gate)
        {
            if (sessionEpoch != null
                && !string.Equals(sessionEpoch, newEpoch, StringComparison.Ordinal))
            {
                lost = new List<PendingExecute>(pendingExecutes.Values);
                pendingExecutes.Clear();
                orphanedReplies = new List<TaskCompletionSource<ProtocolMessage>>(
                    pendingReplies.Values);
                pendingReplies.Clear();
            }

            sessionEpoch = newEpoch;
        }

        if (lost != null)
        {
            foreach (var pending in lost)
            {
                pending.Completion.TrySetResult(
                    HostExecuteReport.OutcomeUnknown(pending.RequestId, "session_lost"));
            }
        }

        if (orphanedReplies != null)
        {
            foreach (var reply in orphanedReplies)
            {
                reply.TrySetCanceled();
            }
        }
    }

    private async Task BeginRecoveryAsync(
        ActiveConnection connection,
        CancellationToken cancellationToken)
    {
        List<PendingExecute> snapshot;
        string epoch;
        lock (gate)
        {
            snapshot = new List<PendingExecute>(pendingExecutes.Values);
            epoch = sessionEpoch!;
        }

        foreach (var pending in snapshot)
        {
            await TrySendAsync(
                connection,
                new GetInteractionResultMessage(
                    options.MessageIdSource(),
                    epoch,
                    pending.RequestId),
                cancellationToken).ConfigureAwait(false);
            if (pending.CancelRequested)
            {
                await TrySendAsync(
                    connection,
                    new CancelInteractionMessage(
                        options.MessageIdSource(),
                        epoch,
                        pending.RequestId),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // A control operation whose connection dropped before its acknowledgment
        // arrived is resent, byte-for-byte, with the same operationId. The runtime
        // deduplicates against its operation ledger — a drop before it saw the
        // request re-runs it; a drop after re-delivers the retained result. The
        // recovery window anchors at the first send, past which a resend could
        // re-run against a runtime that forgot, so beyond it the operation is left
        // for an explicit query to reconcile.
        PendingOperation? operation;
        lock (gate)
        {
            operation = null;
            foreach (var candidate in pendingOperations.Values)
            {
                operation = candidate;
                break;
            }
        }

        if (operation != null
            && string.Equals(operation.Message.SessionEpoch, epoch, StringComparison.Ordinal))
        {
            // Resend only on a SAME-epoch reconnect (a transient drop). The
            // retained message is stamped with its original epoch; resending it
            // after an epoch change would be rejected as session_epoch_mismatch and
            // close the fresh connection. Across an epoch change the operation's
            // acknowledgment arrives on the new epoch through the surviving
            // pendingOperations entry, or a query reconciles it.
            var now = options.Clock();
            var window = connection.Session!.RecoveryWindow;
            if (operation.FirstSentAt == null || now - operation.FirstSentAt.Value < window)
            {
                operation.MarkSendAttempted(now);
                await TrySendAsync(connection, operation.Message, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ReceiveLoopAsync(
        ActiveConnection connection,
        CancellationToken cancellationToken)
    {
        var session = connection.Session!;
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await connection.Channel.ReceiveAsync(
                session.MaxReceiveMessageBytes,
                cancellationToken).ConfigureAwait(false);
            if (frame.Kind != ProtocolChannelFrameKind.Message)
            {
                return;
            }

            var read = ProtocolMessageReader.Read(frame.Payload!, session.MaxReceiveMessageBytes);
            if (read.Status != ProtocolReadStatus.Success)
            {
                await TrySendAsync(
                    connection,
                    new ErrorMessage(
                        options.MessageIdSource(),
                        read.ErrorCode!,
                        read.ErrorMessage!,
                        session.SessionEpoch,
                        null,
                        read.MessageId),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            var decision = connection.Machine!.OnMessageReceived(read.Message!);
            switch (decision.Verdict)
            {
                case ProtocolConnectionVerdict.Accept:
                    await HandleAcceptedAsync(connection, read.Message!, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ProtocolConnectionVerdict.Reject:
                    await TrySendAsync(
                        connection,
                        new ErrorMessage(
                            options.MessageIdSource(),
                            decision.ErrorCode!,
                            decision.ErrorMessage!,
                            session.SessionEpoch,
                            read.Message!.RequestId,
                            read.Message.MessageId),
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await TrySendAsync(
                        connection,
                        new ErrorMessage(
                            options.MessageIdSource(),
                            decision.ErrorCode!,
                            decision.ErrorMessage!,
                            session.SessionEpoch,
                            read.Message!.RequestId,
                            read.Message.MessageId),
                        cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task HandleAcceptedAsync(
        ActiveConnection connection,
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        switch (message)
        {
            case InteractionResultMessage result:
                CompletePendingExecute(result);
                CompleteReply(message);
                return;
            case InteractionAcceptedMessage accepted:
                lock (gate)
                {
                    if (pendingExecutes.TryGetValue(accepted.RequestId!, out var pending))
                    {
                        pending.Accepted = true;
                    }
                }

                return;
            case InteractionStatusMessage _:
            case RegistrySnapshotMessage _:
            case WaitResultMessage _:
                CompleteReply(message);
                return;
            case RecordingStartedMessage started:
                CompleteOperation(
                    started.OperationId,
                    HostControlOperationKind.StartRecording,
                    HostOperationReport.RecordingStarted(
                        started.OperationId,
                        started.RecordingHandle,
                        started.NewSessionEpoch));
                return;
            case RecordingStoppedMessage stopped:
                CompleteOperation(
                    stopped.OperationId,
                    HostControlOperationKind.StopRecording,
                    HostOperationReport.RecordingStopped(
                        stopped.OperationId,
                        stopped.RecordingHandle,
                        stopped.EntryCount,
                        stopped.NewSessionEpoch));
                return;
            case ReplayReportMessage replay:
                CompleteOperation(
                    replay.OperationId,
                    HostControlOperationKind.ReplayRecording,
                    HostOperationReport.Replayed(
                        replay.OperationId,
                        replay.OutcomeKind,
                        replay.NewSessionEpoch,
                        replay.Detail));
                return;
            case ControlOperationResultMessage controlResult:
                CompleteControlQuery(controlResult);
                return;
            case ErrorMessage error:
                await HandleErrorAsync(connection, error, cancellationToken).ConfigureAwait(false);
                return;
            case PingMessage ping:
                await TrySendAsync(
                    connection,
                    new PongMessage(
                        options.MessageIdSource(),
                        ping.MessageId,
                        connection.Session!.SessionEpoch),
                    cancellationToken).ConfigureAwait(false);
                return;
            default:
                return;
        }
    }

    private void CompletePendingExecute(InteractionResultMessage result)
    {
        PendingExecute? pending;
        lock (gate)
        {
            if (pendingExecutes.TryGetValue(result.RequestId!, out pending))
            {
                pendingExecutes.Remove(result.RequestId!);
            }
        }

        pending?.Completion.TrySetResult(
            HostExecuteReport.Completed(result.RequestId!, result.Result));
    }

    private void CompleteOperation(
        string operationId,
        HostControlOperationKind ackKind,
        HostOperationReport report)
    {
        PendingOperation? pending;
        lock (gate)
        {
            if (!pendingOperations.TryGetValue(operationId, out pending))
            {
                return;
            }

            // The acknowledgment must match the operation it claims to complete;
            // a start op is never completed by a replay report (Codex review). A
            // mismatch is left in place, unresolved.
            if (pending.Kind != ackKind)
            {
                return;
            }

            pendingOperations.Remove(operationId);
            ForgetControlMessageMapping(operationId);
        }

        pending.Completion.TrySetResult(report);
    }

    private void CompleteControlQuery(ControlOperationResultMessage result)
    {
        TaskCompletionSource<ControlOperationResultMessage>? query;
        HostControlOperationKind? trackedKind;
        lock (gate)
        {
            pendingQueries.TryGetValue(result.OperationId, out query);
            trackedKind = pendingOperations.TryGetValue(result.OperationId, out var op)
                ? op.Kind
                : (HostControlOperationKind?)null;
        }

        if (query != null)
        {
            // A waiting query resolves the operation via QueryControlOperationAsync.
            query.TrySetResult(result);
            return;
        }

        // Unsolicited terminal result (no waiter): resolve the tracked operation so
        // single-flight does not block forever (Codex review, item 8d).
        if (trackedKind == null)
        {
            return;
        }

        var report = MapControlResult(result, trackedKind);
        if (report != null && IsTerminalState(result.State))
        {
            ClearResolvedOperation(result.OperationId, report);
        }
    }

    // Maps a queried control_operation_result to a host report and, when the
    // answer is terminal, clears the operation from the single-flight slot so new
    // control operations can proceed.
    private HostOperationReport ResolveQueryResult(ControlOperationResultMessage result)
    {
        HostControlOperationKind? trackedKind;
        lock (gate)
        {
            trackedKind = pendingOperations.TryGetValue(result.OperationId, out var op)
                ? op.Kind
                : (HostControlOperationKind?)null;
        }

        var report = MapControlResult(result, trackedKind);
        if (report == null)
        {
            // The terminal result's shape does not match the operation the host is
            // tracking; do not resolve it on a mismatched answer.
            return HostOperationReport.Pending(result.OperationId);
        }

        if (IsTerminalState(result.State))
        {
            ClearResolvedOperation(result.OperationId, report);
        }

        return report;
    }

    // Maps a control result to a host report. Non-terminal states report pending.
    // For a terminal result, the shape is authoritative (the wire enforces exactly
    // one operation shape); when the operation is still tracked, the shape must
    // also match its kind, else the result is a mismatch (null) and must not
    // resolve it.
    private static HostOperationReport? MapControlResult(
        ControlOperationResultMessage result,
        HostControlOperationKind? expectedKind)
    {
        if (string.Equals(
                result.State,
                ProtocolControlOperationStates.Refused,
                StringComparison.Ordinal))
        {
            // A refusal can terminate any control operation.
            return HostOperationReport.RefusedDetail(result.OperationId, result.Detail);
        }

        if (!string.Equals(
                result.State,
                ProtocolControlOperationStates.Completed,
                StringComparison.Ordinal))
        {
            // pending / in_progress: still running.
            return HostOperationReport.Pending(result.OperationId);
        }

        HostControlOperationKind shapeKind;
        HostOperationReport report;
        if (result.OutcomeKind != null)
        {
            shapeKind = HostControlOperationKind.ReplayRecording;
            report = HostOperationReport.Replayed(
                result.OperationId,
                result.OutcomeKind,
                result.NewSessionEpoch,
                result.Detail);
        }
        else if (result.EntryCount != null)
        {
            shapeKind = HostControlOperationKind.StopRecording;
            report = HostOperationReport.RecordingStopped(
                result.OperationId,
                result.RecordingHandle!,
                result.EntryCount.Value,
                result.NewSessionEpoch);
        }
        else
        {
            shapeKind = HostControlOperationKind.StartRecording;
            report = HostOperationReport.RecordingStarted(
                result.OperationId,
                result.RecordingHandle!,
                result.NewSessionEpoch);
        }

        if (expectedKind != null && expectedKind.Value != shapeKind)
        {
            // A replay-shaped result for a tracked start operation, etc. — reject
            // it rather than complete the wrong operation (Codex review).
            return null;
        }

        return report;
    }

    private static bool IsTerminalState(string state)
    {
        return string.Equals(
                state,
                ProtocolControlOperationStates.Completed,
                StringComparison.Ordinal)
            || string.Equals(
                state,
                ProtocolControlOperationStates.Refused,
                StringComparison.Ordinal);
    }

    private void ClearResolvedOperation(string operationId, HostOperationReport report)
    {
        PendingOperation? pending;
        lock (gate)
        {
            if (pendingOperations.TryGetValue(operationId, out pending))
            {
                pendingOperations.Remove(operationId);
            }

            ForgetControlMessageMapping(operationId);
        }

        // The original tool call has already returned Pending (a query only runs
        // after that timeout), so completing its task now is just cleanup that
        // frees the waiter; the result is authoritative for the query caller.
        pending?.Completion.TrySetResult(report);
    }

    private void ForgetControlMessageMapping(string operationId)
    {
        string? messageId = null;
        foreach (var pair in controlMessageToOperation)
        {
            if (string.Equals(pair.Value, operationId, StringComparison.Ordinal))
            {
                messageId = pair.Key;
                break;
            }
        }

        if (messageId != null)
        {
            controlMessageToOperation.Remove(messageId);
        }
    }

    // Completes a control operation from an error the runtime returned for it
    // (e.g. stop with no active recording, replay of a missing handle). The
    // error correlates by inReplyTo to the sent control message, which maps to
    // the operation. Returns true when it belonged to an operation.
    private bool TryCompleteOperationFromError(ErrorMessage error)
    {
        if (error.InReplyTo == null)
        {
            return false;
        }

        PendingOperation? pending;
        lock (gate)
        {
            if (!controlMessageToOperation.TryGetValue(error.InReplyTo, out var operationId))
            {
                return false;
            }

            controlMessageToOperation.Remove(error.InReplyTo);
            if (pendingOperations.TryGetValue(operationId, out pending))
            {
                pendingOperations.Remove(operationId);
            }
        }

        pending?.Completion.TrySetResult(
            HostOperationReport.Refused(pending.OperationId, error.Code, error.Message));
        return true;
    }

    private void CompleteReply(ProtocolMessage message)
    {
        if (message.InReplyTo == null)
        {
            return;
        }

        TaskCompletionSource<ProtocolMessage>? reply;
        lock (gate)
        {
            if (pendingReplies.TryGetValue(message.InReplyTo, out reply))
            {
                pendingReplies.Remove(message.InReplyTo);
            }
        }

        reply?.TrySetResult(message);
    }

    private async Task HandleErrorAsync(
        ActiveConnection connection,
        ErrorMessage error,
        CancellationToken cancellationToken)
    {
        // A control operation's refusal correlates by inReplyTo, not requestId.
        if (TryCompleteOperationFromError(error))
        {
            return;
        }

        // Reply-correlated errors answer their waiting call directly.
        CompleteReply(error);

        if (error.RequestId == null)
        {
            return;
        }

        PendingExecute? pending;
        lock (gate)
        {
            pendingExecutes.TryGetValue(error.RequestId, out pending);
        }

        if (pending == null)
        {
            return;
        }

        switch (error.Code)
        {
            case ProtocolErrorCodes.ResultUnavailable:
                // The recovery window anchors at the FIRST transmission
                // attempt — the moment uncertainty began. A request that was
                // never transmitted has nothing to be uncertain about, so it
                // is always (re)sent; within the window an unavailable answer
                // proves the request never arrived, making the byte-exact
                // resend safe (the ledger deduplicates if it somehow did);
                // beyond it the honest answer is OutcomeUnknown.
                var window = connection.Session!.RecoveryWindow;
                var firstSent = pending.FirstSentAt;
                if (firstSent == null || options.Clock() - firstSent.Value < window)
                {
                    pending.MarkSendAttempted(options.Clock());
                    await TrySendEncodedAsync(connection, pending.EncodedExecute, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    RemoveAndComplete(
                        pending,
                        HostExecuteReport.OutcomeUnknown(
                            pending.RequestId,
                            "retention_expired"));
                }

                return;
            case ProtocolErrorCodes.RequestIdConflict:
            case ProtocolErrorCodes.CapacityExhausted:
            case ProtocolErrorCodes.RuntimeBusy:
                RemoveAndComplete(
                    pending,
                    HostExecuteReport.Refused(pending.RequestId, error.Code, error.Message));
                return;
            default:
                return;
        }
    }

    private void RemoveAndComplete(PendingExecute pending, HostExecuteReport report)
    {
        lock (gate)
        {
            pendingExecutes.Remove(pending.RequestId);
        }

        pending.Completion.TrySetResult(report);
    }

    private async Task<ProtocolMessage?> SendAndAwaitReplyAsync(
        ActiveConnection connection,
        ProtocolMessage request,
        CancellationToken cancellationToken)
    {
        var reply = RegisterReply(request.MessageId);
        await TrySendAsync(connection, request, cancellationToken).ConfigureAwait(false);
        return await AwaitReplyAsync(request.MessageId, reply, options.ReplyTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ProtocolMessage?> SendAndAwaitWaitAsync(
        ActiveConnection connection,
        WaitForMessage request,
        int waitTimeoutMs,
        CancellationToken cancellationToken)
    {
        var reply = RegisterReply(request.MessageId);
        await TrySendAsync(connection, request, cancellationToken).ConfigureAwait(false);

        // The runtime answers a timeout itself; the local margin only covers a
        // dead connection.
        var budget = TimeSpan.FromMilliseconds(waitTimeoutMs) + options.ReplyTimeout;
        return await AwaitReplyAsync(request.MessageId, reply, budget, cancellationToken)
            .ConfigureAwait(false);
    }

    private TaskCompletionSource<ProtocolMessage> RegisterReply(string messageId)
    {
        var completion = new TaskCompletionSource<ProtocolMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pendingReplies.Add(messageId, completion);
        }

        return completion;
    }

    private async Task<ProtocolMessage?> AwaitReplyAsync(
        string messageId,
        TaskCompletionSource<ProtocolMessage> reply,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(
            reply.Task,
            Task.Delay(budget, cancellationToken)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, reply.Task))
        {
            lock (gate)
            {
                pendingReplies.Remove(messageId);
            }

            return null;
        }

        try
        {
            return await reply.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Orphaned by an epoch transition.
            return null;
        }
    }

    // Post-handshake sends can race between the receive loop and tool calls,
    // so everything funnels through the connection's send gate.
    private async Task TrySendAsync(
        ActiveConnection connection,
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        byte[] encoded;
        try
        {
            encoded = ProtocolMessageWriter.Encode(
                message,
                connection.Session?.MaxSendMessageBytes ?? ProtocolLimits.BootstrapMaxMessageBytes);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        await TrySendEncodedAsync(connection, encoded, cancellationToken).ConfigureAwait(false);
    }

    private async Task TrySendEncodedAsync(
        ActiveConnection connection,
        byte[] encoded,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await connection.Channel.SendAsync(encoded, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                connection.SendGate.Release();
            }
        }
        catch (Exception)
        {
            // A failed send means the connection is dying; the receive loop
            // observes the closed channel and pending requests recover on the
            // next connection.
        }
    }

    private async Task TrySendRawAsync(
        IProtocolChannel channel,
        ProtocolMessage message,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var encoded = ProtocolMessageWriter.Encode(message, maxMessageBytes);
            await channel.SendAsync(encoded, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Same contract as TrySendEncodedAsync.
        }
    }

    private static async Task CloseQuietlyAsync(IProtocolChannel channel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await channel.CloseAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The channel is already unusable.
        }
    }

    private sealed class PendingExecute
    {
        public PendingExecute(string requestId, byte[] encodedExecute)
        {
            RequestId = requestId;
            EncodedExecute = encodedExecute;
            Completion = new TaskCompletionSource<HostExecuteReport>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string RequestId { get; }

        public byte[] EncodedExecute { get; }

        // The first moment the execute may have reached the runtime; null
        // while the request has only ever been queued locally. The recovery
        // window anchors here, never at local submission time.
        public DateTimeOffset? FirstSentAt { get; private set; }

        public TaskCompletionSource<HostExecuteReport> Completion { get; }

        public bool CancelRequested { get; set; }

        public bool Accepted { get; set; }

        public void MarkSendAttempted(DateTimeOffset time)
        {
            FirstSentAt ??= time;
        }
    }

    private sealed class PendingOperation
    {
        public PendingOperation(
            string operationId,
            HostControlOperationKind kind,
            ProtocolMessage message)
        {
            OperationId = operationId;
            Kind = kind;
            Message = message;
            Completion = new TaskCompletionSource<HostOperationReport>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string OperationId { get; }

        // The acknowledgment shape this operation expects, so a start op is never
        // completed by a replay report (Codex review, item 8d).
        public HostControlOperationKind Kind { get; }

        // The exact control message, retained so a same-epoch reconnect can resend
        // it byte-for-byte; the runtime's operation ledger deduplicates by
        // operationId.
        public ProtocolMessage Message { get; }

        // The first transmission attempt; the recovery window for a resend anchors
        // here, never at local creation time.
        public DateTimeOffset? FirstSentAt { get; private set; }

        public TaskCompletionSource<HostOperationReport> Completion { get; }

        public void MarkSendAttempted(DateTimeOffset time)
        {
            FirstSentAt ??= time;
        }
    }

    private enum HostControlOperationKind
    {
        StartRecording,
        StopRecording,
        ReplayRecording,
    }

    private sealed class ActiveConnection
    {
        public ActiveConnection(IProtocolChannel channel)
        {
            Channel = channel;
        }

        public IProtocolChannel Channel { get; }

        public SemaphoreSlim SendGate { get; } = new(1, 1);

        // Null until the hello arrived; the receive loop only runs afterwards.
        public ProtocolConnectionStateMachine? Machine { get; set; }

        public ProtocolSession? Session { get; set; }
    }
}
