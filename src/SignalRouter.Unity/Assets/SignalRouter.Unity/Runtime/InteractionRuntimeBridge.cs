#nullable enable

using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SignalRouter.Protocol;
using SignalRouter.Protocol.Transport;
using UnityEngine;

namespace SignalRouter.Unity
{
    // The runtime side of the loopback WebSocket bridge (design §5, §18.1): the
    // runtime connects as the client so it can reconnect after a domain reload
    // without restarting the MCP host. The bridge owns the reconnect loop and
    // one request ledger per runtime session epoch — the ledger surviving
    // reconnects is the whole recovery story (ADR 0007). Message parsing runs
    // on background threads inside the session; everything that touches the
    // registry, dispatcher, or ledger is marshalled through the runtime's
    // main-thread pump (§17.2). Release builds default this component off in
    // item 9 (§19); until then the flag only shapes the surface.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(InteractionRuntime))]
    [AddComponentMenu("SignalRouter/Interaction Runtime Bridge")]
    public sealed class InteractionRuntimeBridge : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Loopback WebSocket endpoint of the MCP host (ws://127.0.0.1:<port>/).")]
        private string endpointUrl = "ws://127.0.0.1:8017/";

        [SerializeField]
        [Tooltip("Connect automatically when the component is enabled.")]
        private bool connectOnEnable = true;

        private readonly System.Random jitter = new();
        private readonly System.Collections.Generic.List<Waiter> waiters = new();
        private InteractionRuntime? runtime;
        private InteractionSessionSupervisor? supervisor;
        private ProtocolRequestLedger? ledger;
        private SemanticUiStateProbe? agentViewProbe;
        private ProtocolPeerOptions? peerOptions;
        private CancellationTokenSource? loop;

        public bool IsRunning => loop != null && !loop.IsCancellationRequested;

        public string EndpointUrl
        {
            get => endpointUrl;
            set
            {
                if (IsRunning)
                {
                    throw new InvalidOperationException(
                        "The endpoint cannot change while the bridge is running.");
                }

                endpointUrl = ValidateEndpoint(value).ToString();
            }
        }

        public bool ConnectOnEnable
        {
            get => connectOnEnable;
            set => connectOnEnable = value;
        }

        // The ledger backing this runtime session's wire requests; exposed so
        // tests and future session tooling can observe recovery state.
        public ProtocolRequestLedger? Ledger => ledger;

        public void StartBridge()
        {
            var owner = runtime != null ? runtime : GetComponent<InteractionRuntime>();
            runtime = owner;
            owner.RequireMainThread();

            // Resolved on the main thread and cached: CreateSessionOptions runs off
            // the main thread (after the connect await), where GetComponent is
            // illegal. An absent supervisor leaves control operations refused.
            supervisor ??= GetComponent<InteractionSessionSupervisor>();
            if (IsRunning)
            {
                throw new InvalidOperationException("The bridge is already running.");
            }

            var endpoint = ValidateEndpoint(endpointUrl);

            // One ledger per runtime session: reconnects reuse it — that is
            // what makes results recoverable — while a recreated runtime (new
            // epoch) starts a fresh one (§13.3).
            if (ledger == null
                || !string.Equals(ledger.SessionEpoch, owner.SessionEpoch, StringComparison.Ordinal))
            {
                ledger = new ProtocolRequestLedger(
                    owner.SessionEpoch,
                    InteractionSystemClock.Instance);
            }

            agentViewProbe ??= new SemanticUiStateProbe(
                owner.Registry,
                InteractionRegistryView.Agent);
            peerOptions ??= new ProtocolPeerOptions(
                "SignalRouter.Unity " + Application.unityVersion,
                Array.Empty<string>(),
                ProtocolLimits.DefaultMaxReceiveMessageBytes);

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                owner.LifetimeToken);
            loop = cancellation;

            // The linked source is disposed only after the loop has fully
            // exited: disposing at StopBridge would race the loop's own use of
            // the token, while never disposing would leak a registration on
            // the runtime's lifetime token per start/stop cycle.
            _ = RunConnectionLoopAsync(endpoint, cancellation.Token)
                .ContinueWith(
                    _ => cancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        public void StopBridge()
        {
            var cancellation = loop;
            loop = null;
            cancellation?.Cancel();
        }

        private void OnEnable()
        {
            if (connectOnEnable && !IsRunning)
            {
                StartBridge();
            }
        }

        private void OnDisable()
        {
            StopBridge();
        }

        // Services pending wait_for conditions once per frame on the main
        // thread (§17.2): the smallest polling scheme that never touches
        // registry state off-thread and needs no timers.
        private void Update()
        {
            if (waiters.Count == 0)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            for (var index = waiters.Count - 1; index >= 0; index--)
            {
                var waiter = waiters[index];
                if (EvaluateCondition(waiter.Request))
                {
                    waiters.RemoveAt(index);
                    waiter.Complete(true, ElapsedMs(waiter, now));
                }
                else if (now >= waiter.Deadline)
                {
                    // A timeout is a normal answer, not an error (ADR 0007).
                    waiters.RemoveAt(index);
                    waiter.Complete(false, ElapsedMs(waiter, now));
                }
            }
        }

        private void BeginWait(WaitForMessage request, Action<bool, long> complete)
        {
            // The fast path answers within the same pump slot; everything else
            // is frame-polled until the deadline.
            if (EvaluateCondition(request))
            {
                complete(true, 0L);
                return;
            }

            var now = Time.realtimeSinceStartup;
            waiters.Add(new Waiter(
                request,
                complete,
                now,
                now + (request.TimeoutMs / 1000f)));
        }

        private bool EvaluateCondition(WaitForMessage request)
        {
            switch (request.Condition)
            {
                case ProtocolWaitConditions.Idle:
                    return runtime!.InFlightDispatches == 0;
                case ProtocolWaitConditions.TargetPresent:
                    // Waits see the agent view only (design §19): a hidden
                    // target must not be probeable into disclosure.
                    return runtime!.Registry.IsAgentVisible(request.TargetId!);
                default:
                    return !runtime!.Registry.IsAgentVisible(request.TargetId!);
            }
        }

        private static long ElapsedMs(Waiter waiter, float now)
        {
            return (long)((now - waiter.StartedAt) * 1000f);
        }

        private readonly struct Waiter
        {
            public Waiter(
                WaitForMessage request,
                Action<bool, long> complete,
                float startedAt,
                float deadline)
            {
                Request = request;
                Complete = complete;
                StartedAt = startedAt;
                Deadline = deadline;
            }

            public WaitForMessage Request { get; }

            public Action<bool, long> Complete { get; }

            public float StartedAt { get; }

            public float Deadline { get; }
        }

        private async Task RunConnectionLoopAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            var policy = ProtocolReconnectPolicy.CreateDefault(NextRandomSample);
            var attempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                WebSocketChannel channel;
                try
                {
                    channel = await WebSocketChannel.ConnectAsync(endpoint, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    if (!await TryDelayAsync(policy, attempt++, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                using (channel)
                {
                    var session = new RuntimeBridgeSession(
                        channel,
                        CreateSessionOptions(),
                        cancellationToken);

                    // RunAsync completes normally for every way a connection
                    // ends, including session-local teardown after a failed
                    // send — only this loop's own token decides whether the
                    // bridge stops reconnecting. The catch is defensive: a
                    // session bug must surface as a retry, not as a silently
                    // dead bridge.
                    try
                    {
                        await session.RunAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }

                    // A completed handshake proves the endpoint is healthy;
                    // the next disconnect retries fast again.
                    if (session.Session != null)
                    {
                        attempt = 0;
                    }
                }

                if (!await TryDelayAsync(policy, attempt++, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }

        private RuntimeBridgeSessionOptions CreateSessionOptions()
        {
            var owner = supervisor;
            return new RuntimeBridgeSessionOptions(
                ledger!,
                peerOptions!,
                runtime!.Post,
                SubmitFromWire,
                requestId => runtime!.Dispatcher.TryCancel(requestId),
                CaptureAgentSnapshot,
                BeginWait,
                null,
                null,
                owner != null ? owner.BeginControlOperation : null,
                owner != null ? owner.QueryControlOperation : null);
        }

        // Runs on the main thread (the session posts). Raw arguments cross the
        // thread boundary as a string and are parsed and disposed here, never
        // as a JsonElement whose document lives elsewhere.
        private InteractionSubmission SubmitFromWire(ExecuteInteractionMessage message)
        {
            // A control transition closes admission; the session turns this throw
            // into a runtime_busy answer and abandons the ledger reservation, so the
            // host can honestly resend once the transition completes.
            if (supervisor != null && !supervisor.IsAdmitting)
            {
                throw new InvalidOperationException(
                    "The runtime is transitioning and cannot admit new interactions.");
            }

            var options = new InteractionSubmissionOptions(
                message.RequestId!,
                InteractionOrigin.Agent,
                message.CorrelationId);

            // Agent visibility is enforced before dispatch (design §19): a
            // hidden command or target answers exactly like an absent one, so
            // the wire can neither execute nor disclose what agents were not
            // shown.
            if (!runtime!.Catalog.TryGet(
                    message.CommandName,
                    message.CommandVersion,
                    out var catalogEntry)
                || !catalogEntry!.AgentVisible)
            {
                return runtime.Dispatcher.SubmitRejection(
                    options,
                    message.TargetId,
                    message.CommandName,
                    message.CommandVersion,
                    new RejectionInfo(
                        InteractionRejectionCode.CommandNotRegistered,
                        "Command '" + message.CommandName + "@" + message.CommandVersion
                        + "' is not registered."));
            }

            if (!runtime.Registry.IsAgentVisible(message.TargetId))
            {
                return runtime.Dispatcher.SubmitRejection(
                    options,
                    message.TargetId,
                    message.CommandName,
                    message.CommandVersion,
                    new RejectionInfo(
                        InteractionRejectionCode.TargetNotFound,
                        "Target '" + message.TargetId + "' is not registered."));
            }

            DecodedInteractionCommand decoded;
            try
            {
                using var document = JsonDocument.Parse(message.ArgumentsJson);
                decoded = runtime!.Catalog.Decode(
                    message.CommandName,
                    message.CommandVersion,
                    message.TargetId,
                    document.RootElement);
            }
            catch (InteractionCommandException exception)
            {
                // Lenient transport shell, strict command core (§6.1): the
                // catalog's verdict becomes a terminal Rejected result under
                // the caller-owned identity, not a transport error.
                return runtime!.Dispatcher.SubmitRejection(
                    options,
                    message.TargetId,
                    message.CommandName,
                    message.CommandVersion,
                    new RejectionInfo(exception.RejectionCode, exception.Message));
            }

            var submission = decoded.Submit(
                runtime!.Dispatcher,
                options,
                runtime.LifetimeToken);
            if (submission.Kind == InteractionAdmissionKind.Queued)
            {
                // Wire dispatches count as in-flight runtime work so shutdown
                // keeps deferring disposal until they drain, exactly like
                // adapter dispatches.
                runtime.TrackDispatch(
                    new ValueTask<InteractionResult>(submission.Completion),
                    _ => { });
            }

            return submission;
        }

        private RegistrySnapshotDocument CaptureAgentSnapshot()
        {
            var snapshot = agentViewProbe!.Capture();
            return new RegistrySnapshotDocument(
                agentViewProbe.Version,
                Encoding.UTF8.GetString(snapshot.Utf8Json.Span));
        }

        private async Task<bool> TryDelayAsync(
            ProtocolReconnectPolicy policy,
            int attempt,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(policy.NextDelay(attempt), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private double NextRandomSample()
        {
            lock (jitter)
            {
                return jitter.NextDouble();
            }
        }

        private static Uri ValidateEndpoint(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
                || (!string.Equals(endpoint.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(endpoint.Scheme, "wss", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    "The endpoint must be an absolute ws:// or wss:// URI.",
                    nameof(value));
            }

            return endpoint;
        }
    }
}
