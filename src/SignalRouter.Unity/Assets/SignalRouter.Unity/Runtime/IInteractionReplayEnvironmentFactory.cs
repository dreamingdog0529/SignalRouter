#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRouter.Unity
{
    // Builds an isolated runtime for verifying a recording (item 8d). Replay is an
    // opt-in capability: an in-process runtime isolates only its own dispatcher,
    // registry, and probes — it cannot isolate static fields, singletons,
    // ScriptableObjects, PlayerPrefs, files, or the network. So the application
    // must supply an environment whose stages, probes, schemas, and resources are
    // ALL replay-only instances with no global or external side effects; otherwise
    // replaying would mutate live application state. The supervisor pauses live
    // interaction for the duration and runs the recording against
    // Environment.Runtime.Dispatcher.
    public interface IInteractionReplayEnvironmentFactory
    {
        // Constructs the environment at the application's initial interactable
        // state, seeded with the recording's session epoch so the built-in probe
        // hashes line up. The returned runtime must be initialized and its targets
        // registered (await any Start/asset readiness) before the task completes.
        // It must carry no transport bridge and take no human input.
        ValueTask<IInteractionReplayEnvironment> CreateAsync(
            InteractionRecording recording,
            CancellationToken cancellationToken);
    }

    // A disposable isolated replay runtime. Dispose tears the environment down; the
    // supervisor waits one frame after disposal so Unity's deferred destruction
    // completes before the operation is reported terminal.
    public interface IInteractionReplayEnvironment : IDisposable
    {
        InteractionRuntime Runtime { get; }
    }
}
