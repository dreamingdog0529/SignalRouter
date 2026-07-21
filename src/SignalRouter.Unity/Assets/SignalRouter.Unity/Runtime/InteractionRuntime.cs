#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SignalRouter.Unity;

// Owns the catalog, semantic registry, probe registry, and dispatcher for one
// Unity runtime session (design §17). Tying the registry to this component's
// lifetime makes the session-epoch-per-recreation guarantee structural for
// live sessions (§13.3); replay reconstruction passes the recording's session
// ID explicitly through InteractionRuntimeOptions instead. Update drains the
// thread-safe handoff queue that later transports feed (§17.2).
[DisallowMultipleComponent]
[AddComponentMenu("SignalRouter/Interaction Runtime")]
public sealed class InteractionRuntime : MonoBehaviour
{
    private readonly ConcurrentQueue<Action> mainThreadQueue = new();
    private InteractionCommandCatalog? catalog;
    private InteractionRegistry? registry;
    private InteractionStateProbeRegistry? probes;
    private InteractionDispatcher? dispatcher;
    private CancellationTokenSource? lifetime;
    private TaskCompletionSource<bool>? idleCompletion;
    private int mainThreadId = -1;
    private int suppressionDepth;
    private int inFlightDispatches;
    private volatile bool shutdownRequested;
    private bool disposed;

    public bool IsInitialized => dispatcher != null;

    public InteractionCommandCatalog Catalog =>
        catalog ?? throw NotInitialized();

    public InteractionRegistry Registry =>
        registry ?? throw NotInitialized();

    public InteractionStateProbeRegistry Probes =>
        probes ?? throw NotInitialized();

    // The concrete dispatcher type is exposed on purpose: the strict replayer
    // requires it, and hiding it behind IInteractionDispatcher would force
    // replay tooling to down-cast.
    public InteractionDispatcher Dispatcher =>
        dispatcher ?? throw NotInitialized();

    public string SessionEpoch => Registry.SessionEpoch;

    // Cancelled when Shutdown begins; adapters pass this token to their
    // dispatches so in-flight work stops cooperatively during teardown. After
    // shutdown the token is refused outright: the backing source may already
    // be disposed, and new dispatches must fail with the shutdown message,
    // not an ObjectDisposedException from the source.
    public CancellationToken LifetimeToken
    {
        get
        {
            if (lifetime == null)
            {
                throw NotInitialized();
            }

            if (shutdownRequested)
            {
                throw new InvalidOperationException(
                    "The interaction runtime is shut down and no longer accepts dispatches.");
            }

            return lifetime.Token;
        }
    }

    // True while any InteractionScope suppression is open: adapters treat UI
    // notifications as agent/replay echoes rather than human input.
    public bool IsSuppressing => suppressionDepth > 0;

    public bool IsMainThread =>
        mainThreadId != -1 && Environment.CurrentManagedThreadId == mainThreadId;

    // Number of adapter dispatches whose results have not been observed yet.
    public int InFlightDispatches => inFlightDispatches;

    public void Initialize(InteractionRuntimeOptions? options = null)
    {
        if (dispatcher != null)
        {
            throw new InvalidOperationException(
                "The interaction runtime is already initialized.");
        }

        if (shutdownRequested)
        {
            throw new InvalidOperationException(
                "The interaction runtime has been shut down and cannot be reinitialized.");
        }

        CaptureMainThread();
        var resolvedCatalog = options?.Catalog ?? InteractionCommandCatalog.CreateMvp();

        // A fresh GUID epoch per initialization is what makes a recreated
        // runtime a new session (§13.3). Only replay reconstruction supplies
        // the recorded session ID explicitly.
        var epoch = options?.SessionEpoch ?? Guid.NewGuid().ToString("N");
        var resolvedRegistry = new InteractionRegistry(resolvedCatalog, epoch);
        var resolvedProbes = new InteractionStateProbeRegistry();
        resolvedProbes.Register(new SemanticUiStateProbe(resolvedRegistry));
        resolvedProbes.Register(new InteractionRuntimeStateProbe(resolvedRegistry));
        if (options != null)
        {
            foreach (var probe in options.AdditionalProbes)
            {
                resolvedProbes.Register(probe);
            }
        }

        var resolvedDispatcher = new InteractionDispatcher(
            resolvedCatalog,
            resolvedRegistry,
            resolvedProbes,
            options?.Recorder);

        catalog = resolvedCatalog;
        registry = resolvedRegistry;
        probes = resolvedProbes;
        lifetime = new CancellationTokenSource();
        dispatcher = resolvedDispatcher;
    }

    public void RequireMainThread()
    {
        if (mainThreadId == -1)
        {
            throw new InvalidOperationException(
                "The interaction runtime has not captured the main thread yet; "
                + "initialize it or let Awake run first.");
        }

        if (Environment.CurrentManagedThreadId != mainThreadId)
        {
            throw new InvalidOperationException(
                "This operation must run on the Unity main thread (design §17.2).");
        }
    }

    // Thread-safe handoff into the main-thread pump (§17.2). Posted work runs
    // in order during the next Update; a throwing action propagates out of
    // Update and surfaces as an error.
    public void Post(Action work)
    {
        if (work == null)
        {
            throw new ArgumentNullException(nameof(work));
        }

        if (shutdownRequested)
        {
            throw new InvalidOperationException(
                "The interaction runtime is shut down and no longer accepts posted work.");
        }

        mainThreadQueue.Enqueue(work);
    }

    // Completes when every tracked dispatch has been observed. Test harnesses
    // await this before tearing scenes down so no dispatch outlives its UI.
    public Task WhenIdleAsync()
    {
        RequireMainThread();
        if (inFlightDispatches == 0)
        {
            return Task.CompletedTask;
        }

        idleCompletion ??= new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return idleCompletion.Task;
    }

    // Rejects new work and cancels the lifetime token. The dispatcher is
    // disposed once the last in-flight dispatch has been observed — disposing
    // it mid-dispatch would race the cancelled work still unwinding through
    // it. Idempotent; OnDestroy calls it, and fixtures that initialize an
    // inactive runtime (whose OnDestroy never runs) must call it directly.
    public void Shutdown()
    {
        if (shutdownRequested)
        {
            return;
        }

        shutdownRequested = true;
        if (dispatcher == null)
        {
            return;
        }

        RequireMainThread();
        lifetime!.Cancel();
        if (inFlightDispatches == 0)
        {
            DisposeCore();
        }
    }

    internal void BeginSuppression()
    {
        RequireMainThread();
        suppressionDepth++;
    }

    internal void EndSuppression()
    {
        RequireMainThread();
        if (suppressionDepth == 0)
        {
            throw new InvalidOperationException(
                "Suppression ended more often than it began.");
        }

        suppressionDepth--;
    }

    // Tracks an adapter dispatch: counts it as in flight, observes its result
    // exactly once, and raises the adapter callback. Structured non-success
    // (Rejected/Faulted) flows to the callback per the result model; only
    // infrastructure exceptions escape, onto the Unity synchronization
    // context, where they surface as errors.
    internal void TrackDispatch(
        ValueTask<InteractionResult> dispatch,
        Action<InteractionResult> onCompleted)
    {
        RequireMainThread();
        if (shutdownRequested)
        {
            throw new InvalidOperationException(
                "The interaction runtime is shut down and no longer accepts dispatches.");
        }

        inFlightDispatches++;
        ObserveDispatch(dispatch, onCompleted);
    }

    // async void is deliberate: it is the one mechanism that rethrows an
    // infrastructure failure onto the Unity synchronization context instead
    // of leaving it on a discarded task.
    private async void ObserveDispatch(
        ValueTask<InteractionResult> dispatch,
        Action<InteractionResult> onCompleted)
    {
        try
        {
            var result = await dispatch;
            onCompleted(result);
        }
        finally
        {
            inFlightDispatches--;
            if (inFlightDispatches == 0)
            {
                var completion = idleCompletion;
                idleCompletion = null;
                completion?.TrySetResult(true);
                if (shutdownRequested && !disposed)
                {
                    DisposeCore();
                }
            }
        }
    }

    private void DisposeCore()
    {
        disposed = true;
        dispatcher!.Dispose();
        lifetime!.Dispose();
    }

    private void Awake()
    {
        CaptureMainThread();
        if (dispatcher == null)
        {
            Initialize();
        }
    }

    private void Update()
    {
        while (mainThreadQueue.TryDequeue(out var work))
        {
            work();
        }
    }

    private void OnDestroy()
    {
        Shutdown();
    }

    private void CaptureMainThread()
    {
        if (mainThreadId == -1)
        {
            mainThreadId = Environment.CurrentManagedThreadId;
        }
    }

    private static InvalidOperationException NotInitialized()
    {
        return new InvalidOperationException(
            "The interaction runtime is not initialized; call Initialize or let Awake run.");
    }
}
