#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SignalRouter.Protocol;
using SignalRouter.Protocol.Transport;
using UnityEngine;

namespace SignalRouter.Unity
{
    // Drives recording and replay control operations on the runtime's main thread
    // (item 8d). Recording attaches a recorder to the live dispatcher under a
    // maintenance lease — no runtime recreation, no epoch change. Replay pauses the
    // live runtime (holds a maintenance lease for the duration) and verifies the
    // recording on an isolated runtime built by an application-supplied factory.
    //
    // A session-independent operation ledger keeps every terminal outcome so a
    // reconnect resend or a query still finds it, and — the key recovery point —
    // when a resend for an in-flight operation arrives on a new connection, its
    // responder is swapped so the eventual acknowledgment reaches the live session
    // rather than the dead one the operation started on.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(InteractionRuntimeBridge))]
    [AddComponentMenu("SignalRouter/Interaction Session Supervisor")]
    public sealed class InteractionSessionSupervisor : MonoBehaviour
    {
        private readonly Dictionary<string, LedgerEntry> ledger = new(StringComparer.Ordinal);
        private readonly List<TaskCompletionSource<bool>> frameWaiters = new();
        private InteractionRuntime? runtime;
        private InteractionSessionSupervisorOptions? options;
        private IInteractionReplayEnvironmentFactory? replayFactory;
        private string? activeOperationId;
        private InteractionRecorder? currentRecorder;
        private string? currentHandle;
        private volatile bool isAdmitting = true;

        // True while the runtime accepts new wire executes. The bridge consults this
        // on its submit path; a control transition closes it so in-flight work can
        // drain before the recorder swap (or, for replay, so the live runtime stays
        // paused). Read off the main thread by the bridge, hence volatile.
        public bool IsAdmitting => isAdmitting;

        public void Configure(InteractionSessionSupervisorOptions supervisorOptions)
        {
            options = supervisorOptions
                ?? throw new ArgumentNullException(nameof(supervisorOptions));
        }

        // Supplies the isolated replay environment. Absent a factory, replay is
        // refused — recording still works.
        public void SetReplayEnvironmentFactory(IInteractionReplayEnvironmentFactory factory)
        {
            replayFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        private void Awake()
        {
            runtime = GetComponent<InteractionRuntime>();
            options ??= new InteractionSessionSupervisorOptions();
        }

        private void Update()
        {
            if (frameWaiters.Count == 0)
            {
                return;
            }

            var due = frameWaiters.ToArray();
            frameWaiters.Clear();
            foreach (var waiter in due)
            {
                waiter.TrySetResult(true);
            }
        }

        // --- Session callbacks (main thread) -----------------------------------

        internal void BeginControlOperation(
            RuntimeControlRequest request,
            Action<RuntimeControlAck> complete)
        {
            EvictExpired();
            var fingerprint = Fingerprint(request);

            if (ledger.TryGetValue(request.OperationId, out var existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    // Same operation id, different operation: the host guarantees
                    // uniqueness, so this is a conflict, not a resend.
                    complete(RuntimeControlAck.Refused(
                        request.OperationId,
                        ProtocolErrorCodes.ControlInProgress,
                        "The operation id is already in use for a different operation."));
                    return;
                }

                if (existing.State == LedgerState.InProgress)
                {
                    // A resend of the in-flight operation, arriving on a new
                    // connection: swap the responder so the acknowledgment reaches
                    // this (live) session instead of the one it started on.
                    existing.Responder = complete;
                    return;
                }

                // Terminal: re-deliver the retained acknowledgment.
                complete(existing.TerminalAck);
                return;
            }

            if (activeOperationId != null)
            {
                // Single-flight: one control operation at a time. Store the refusal
                // so a lost-refusal resend re-refuses instead of executing.
                var refusal = RuntimeControlAck.Refused(
                    request.OperationId,
                    ProtocolErrorCodes.ControlInProgress,
                    "Another control operation is in progress.");
                Record(request, fingerprint, LedgerState.Refused, refusal, null);
                complete(refusal);
                return;
            }

            activeOperationId = request.OperationId;
            ledger[request.OperationId] = new LedgerEntry(
                request.Kind,
                fingerprint,
                LedgerState.InProgress,
                default,
                Now(),
                complete);

            // Close admission synchronously, before the async work starts, so a
            // wire execute queued right behind this control message is refused
            // rather than admitted ahead of the transition.
            isAdmitting = false;
            _ = RunOperationAsync(request);
        }

        internal void QueryControlOperation(
            string operationId,
            Action<RuntimeControlQueryResult> complete)
        {
            EvictExpired();
            if (ledger.TryGetValue(operationId, out var entry))
            {
                complete(entry.State == LedgerState.InProgress
                    ? RuntimeControlQueryResult.InProgress()
                    : RuntimeControlQueryResult.Terminal(entry.TerminalAck));
                return;
            }

            // The runtime never saw this operation (its request was lost past the
            // recovery window). Report not-found so the host stops waiting.
            complete(RuntimeControlQueryResult.Terminal(RuntimeControlAck.Refused(
                operationId,
                "not_found",
                "The operation is not known to this runtime.")));
        }

        // --- Operation execution (main thread, async) --------------------------

        private async Task RunOperationAsync(RuntimeControlRequest request)
        {
            RuntimeControlAck ack;
            try
            {
                switch (request.Kind)
                {
                    case RuntimeControlKind.StartRecording:
                        ack = await RunStartAsync(request).ConfigureAwait(true);
                        break;
                    case RuntimeControlKind.StopRecording:
                        ack = await RunStopAsync(request).ConfigureAwait(true);
                        break;
                    case RuntimeControlKind.ReplayRecording:
                        ack = await RunReplayAsync(request).ConfigureAwait(true);
                        break;
                    default:
                        ack = RuntimeControlAck.Refused(
                            request.OperationId,
                            ProtocolErrorCodes.RecordingUnavailable,
                            "Unknown control operation.");
                        break;
                }
            }
            catch (Exception exception)
            {
                ack = RuntimeControlAck.Refused(
                    request.OperationId,
                    ProtocolErrorCodes.RecordingUnavailable,
                    Sanitize(exception.Message));
            }
            finally
            {
                isAdmitting = true;
            }

            CompleteOperation(request.OperationId, ack);
        }

        private async Task<RuntimeControlAck> RunStartAsync(RuntimeControlRequest request)
        {
            if (currentRecorder != null)
            {
                return RuntimeControlAck.Refused(
                    request.OperationId,
                    ProtocolErrorCodes.RecordingUnavailable,
                    "A recording is already active.");
            }

            // The file is created before the maintenance lease is held, keeping the
            // lease window — the interval the whole runtime is quiesced — to just
            // the attach.
            var (handle, recorder) = CreateRecording();
            var lease = await runtime!.Dispatcher
                .AcquireMaintenanceLeaseAsync(runtime.LifetimeToken)
                .ConfigureAwait(true);
            try
            {
                lease.AttachRecorder(recorder);
            }
            finally
            {
                lease.Dispose();
            }

            currentRecorder = recorder;
            currentHandle = handle;
            return RuntimeControlAck.RecordingStarted(request.OperationId, handle);
        }

        private async Task<RuntimeControlAck> RunStopAsync(RuntimeControlRequest request)
        {
            if (currentRecorder == null || currentHandle == null)
            {
                return RuntimeControlAck.Refused(
                    request.OperationId,
                    ProtocolErrorCodes.RecordingUnavailable,
                    "No recording is active.");
            }

            var lease = await runtime!.Dispatcher
                .AcquireMaintenanceLeaseAsync(runtime.LifetimeToken)
                .ConfigureAwait(true);
            try
            {
                lease.DetachRecorder();
            }
            finally
            {
                lease.Dispose();
            }

            var recorder = currentRecorder;
            var handle = currentHandle;
            currentRecorder = null;
            currentHandle = null;

            // Flush/close and count outside the lease: the runtime is live again.
            recorder.Dispose();
            var entryCount = ReadEntryCount(handle);
            return RuntimeControlAck.RecordingStopped(request.OperationId, handle, entryCount);
        }

        private async Task<RuntimeControlAck> RunReplayAsync(RuntimeControlRequest request)
        {
            if (replayFactory == null)
            {
                return RuntimeControlAck.Refused(
                    request.OperationId,
                    ProtocolErrorCodes.RecordingUnavailable,
                    "Replay is not configured on this runtime.");
            }

            var handle = request.RecordingHandle;
            if (handle == null || !RecordingHandles.IsValid(handle))
            {
                return RuntimeControlAck.Refused(
                    request.OperationId,
                    ProtocolErrorCodes.RecordingUnavailable,
                    "The recording handle is invalid.");
            }

            InteractionRecording recording;
            try
            {
                recording = InteractionRecordingReader.LoadFile(
                    Options.ArtifactRoot,
                    handle + ".jsonl");
            }
            catch (Exception exception)
                when (exception is IOException || exception is InteractionRecordingException)
            {
                return RuntimeControlAck.Refused(
                    request.OperationId,
                    ProtocolErrorCodes.RecordingUnavailable,
                    "The recording could not be loaded.");
            }

            // Pause the live runtime for the whole replay: an in-process replay
            // cannot isolate shared static/singleton state, so live interaction
            // must not run concurrently.
            var liveLease = await runtime!.Dispatcher
                .AcquireMaintenanceLeaseAsync(runtime.LifetimeToken)
                .ConfigureAwait(true);
            try
            {
                var environment = await replayFactory
                    .CreateAsync(recording, runtime.LifetimeToken)
                    .ConfigureAwait(true);
                try
                {
                    var report = await InteractionReplayer
                        .ReplayAsync(recording, environment.Runtime.Dispatcher)
                        .AsTask()
                        .ConfigureAwait(true);
                    return RuntimeControlAck.ReplayReport(
                        request.OperationId,
                        MapOutcome(report.Outcome),
                        MapDetail(report));
                }
                finally
                {
                    environment.Dispose();
                    // Let Unity's deferred destruction run before reporting done.
                    await NextFrameAsync().ConfigureAwait(true);
                }
            }
            finally
            {
                liveLease.Dispose();
            }
        }

        private void CompleteOperation(string operationId, RuntimeControlAck ack)
        {
            activeOperationId = null;
            if (!ledger.TryGetValue(operationId, out var entry))
            {
                return;
            }

            var responder = entry.Responder;
            entry.State = ack.Kind == RuntimeControlAckKind.Refused
                ? LedgerState.Refused
                : LedgerState.Completed;
            entry.TerminalAck = ack;
            entry.TerminalAt = Now();
            entry.Responder = null;

            // The responder may have been swapped to a newer session by a resend;
            // fire whichever is current. A dead session drops it silently — the
            // host recovers via resend or query against the retained ledger entry.
            responder?.Invoke(ack);
        }

        // --- Helpers -----------------------------------------------------------

        private (string handle, InteractionRecorder recorder) CreateRecording()
        {
            var recorderOptions = new InteractionRecorderOptions(
                runtime!.SessionEpoch,
                Options.AppBuild,
                Options.Clock);
            for (var attempt = 0; ; attempt++)
            {
                var handle = GenerateHandle();
                try
                {
                    var recorder = InteractionRecorder.CreateFile(
                        Options.ArtifactRoot,
                        handle + ".jsonl",
                        recorderOptions);
                    return (handle, recorder);
                }
                catch (IOException) when (attempt < 2)
                {
                    // Handle collision: regenerate and retry.
                }
            }
        }

        private long ReadEntryCount(string handle)
        {
            var recording = InteractionRecordingReader.LoadFile(
                Options.ArtifactRoot,
                handle + ".jsonl");
            return recording.Interactions.Count;
        }

        private string GenerateHandle()
        {
            var stamp = Options.Clock.UtcNow.ToString(
                "yyyyMMddHHmmssfff",
                System.Globalization.CultureInfo.InvariantCulture);
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            return "rec-" + stamp + "-" + suffix;
        }

        private void Record(
            RuntimeControlRequest request,
            string fingerprint,
            LedgerState state,
            RuntimeControlAck ack,
            Action<RuntimeControlAck>? responder)
        {
            ledger[request.OperationId] = new LedgerEntry(
                request.Kind,
                fingerprint,
                state,
                ack,
                Now(),
                responder);
        }

        private void EvictExpired()
        {
            var cutoff = Now() - Options.OperationRetention;
            List<string>? expired = null;
            foreach (var pair in ledger)
            {
                if (pair.Value.State != LedgerState.InProgress
                    && pair.Value.TerminalAt < cutoff)
                {
                    (expired ??= new List<string>()).Add(pair.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            foreach (var key in expired)
            {
                ledger.Remove(key);
            }
        }

        private Task NextFrameAsync()
        {
            var waiter = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            frameWaiters.Add(waiter);
            return waiter.Task;
        }

        private DateTimeOffset Now()
        {
            return Options.Clock.UtcNow;
        }

        private InteractionSessionSupervisorOptions Options
        {
            get { return options ??= new InteractionSessionSupervisorOptions(); }
        }

        private static string Fingerprint(RuntimeControlRequest request)
        {
            return request.Kind + "|" + (request.RecordingHandle ?? "") + "|" + (request.Label ?? "");
        }

        private static string MapOutcome(InteractionReplayOutcome outcome)
        {
            switch (outcome)
            {
                case InteractionReplayOutcome.Completed:
                    return ProtocolReplayOutcomes.Completed;
                case InteractionReplayOutcome.Diverged:
                    return ProtocolReplayOutcomes.Diverged;
                default:
                    return ProtocolReplayOutcomes.Stopped;
            }
        }

        private static string? MapDetail(InteractionReplayReport report)
        {
            string? detail = null;
            if (report.Divergence != null)
            {
                detail = report.Divergence.Kind + " at sequence "
                    + report.Divergence.Entry.Sequence.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (report.StopReason != null)
            {
                detail = report.StopReason.Value + " at sequence "
                    + (report.StoppedBefore?.Sequence ?? 0).ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
            }

            return Sanitize(detail);
        }

        private static string? Sanitize(string? detail)
        {
            if (string.IsNullOrEmpty(detail))
            {
                return detail;
            }

            return detail!.Length > ProtocolLimits.MaxErrorMessageChars
                ? detail.Substring(0, ProtocolLimits.MaxErrorMessageChars)
                : detail;
        }

        private enum LedgerState
        {
            InProgress,
            Completed,
            Refused,
        }

        private sealed class LedgerEntry
        {
            public LedgerEntry(
                RuntimeControlKind kind,
                string fingerprint,
                LedgerState state,
                RuntimeControlAck terminalAck,
                DateTimeOffset terminalAt,
                Action<RuntimeControlAck>? responder)
            {
                Kind = kind;
                Fingerprint = fingerprint;
                State = state;
                TerminalAck = terminalAck;
                TerminalAt = terminalAt;
                Responder = responder;
            }

            public RuntimeControlKind Kind { get; }

            public string Fingerprint { get; }

            public LedgerState State { get; set; }

            public RuntimeControlAck TerminalAck { get; set; }

            public DateTimeOffset TerminalAt { get; set; }

            public Action<RuntimeControlAck>? Responder { get; set; }
        }
    }
}
