using System.Collections.Concurrent;
using NUnit.Framework;

namespace SignalRouter.Core.Tests;

// Regression coverage for caller-context preservation (design §17.2): a stage
// that genuinely yields resumes the dispatcher on a thread-pool thread via
// ConfigureAwait(false), so without an explicit switch the after-state capture
// — and, during replay, every entry after the first — would touch probes and
// target descriptors off the caller's context (the Unity main thread under the
// main-thread policy).
public sealed class CallerContextPreservationTests
{
    [Test]
    public async Task AfterStateCaptureRunsOnTheCallerContextWhenAStageYields()
    {
        using var runtime = new ContextRuntime();
        using var context = new SingleThreadSynchronizationContext();
        runtime.RegisterYieldingClick("menu.start");

        InteractionResult? result = null;
        await context.Run(async () =>
        {
            result = await runtime.Dispatcher.DispatchAsync(
                new ClickCommand("menu.start"),
                new InteractionDispatchOptions(InteractionOrigin.Test));
        });

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result!.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(runtime.StageThreads, Is.EqualTo(new[] { context.ThreadId }));
            Assert.That(runtime.Probe.CaptureThreads, Is.Not.Empty);
            Assert.That(
                runtime.Probe.CaptureThreads,
                Is.All.EqualTo(context.ThreadId));
        });
    }

    [Test]
    public async Task ReplayEntriesAfterAYieldingDispatchStayOnTheCallerContext()
    {
        InteractionRecording recording;
        using (var recordSide = new ContextRuntime(record: true))
        {
            recordSide.RegisterYieldingClick("menu.start");
            await recordSide.Dispatcher.DispatchAsync(
                new ClickCommand("menu.start"),
                new InteractionDispatchOptions(InteractionOrigin.Test));
            await recordSide.Dispatcher.DispatchAsync(
                new ClickCommand("menu.start"),
                new InteractionDispatchOptions(InteractionOrigin.Test));
            using var stream = new MemoryStream(recordSide.Sink!.ToArray());
            recording = InteractionRecordingReader.Load(stream);
        }

        using var replaySide = new ContextRuntime();
        using var context = new SingleThreadSynchronizationContext();
        replaySide.RegisterYieldingClick("menu.start");

        InteractionReplayReport? report = null;
        await context.Run(async () =>
        {
            report = await InteractionReplayer.ReplayAsync(
                recording,
                replaySide.Dispatcher);
        });

        NUnitCompat.Multiple(() =>
        {
            Assert.That(report!.Outcome, Is.EqualTo(InteractionReplayOutcome.Completed));
            Assert.That(report.VerifiedInteractions, Is.EqualTo(2));
            Assert.That(replaySide.StageThreads, Has.Count.EqualTo(2));
            Assert.That(replaySide.StageThreads, Is.All.EqualTo(context.ThreadId));
            Assert.That(replaySide.Probe.CaptureThreads, Is.Not.Empty);
            Assert.That(
                replaySide.Probe.CaptureThreads,
                Is.All.EqualTo(context.ThreadId));
        });
    }

    // A minimal probe-observed runtime whose single click stage records the
    // thread it starts on and then yields off the ambient context, reproducing
    // a Unity stage that awaits real asynchronous work.
    private sealed class ContextRuntime : IDisposable
    {
        private readonly List<IInteractionTargetRegistration> registrations = new();

        public ContextRuntime(bool record = false)
        {
            Catalog = InteractionCommandCatalog.CreateMvp();
            Registry = new InteractionRegistry(Catalog, "session-1");
            Probe = new ThreadRecordingProbe();
            var probes = new InteractionStateProbeRegistry();
            probes.Register(new SemanticUiStateProbe(Registry));
            probes.Register(new InteractionRuntimeStateProbe(Registry));
            probes.Register(Probe);
            if (record)
            {
                Sink = new MemoryStream();
                Recorder = new InteractionRecorder(
                    Sink,
                    new InteractionRecorderOptions("session-1", "build-1"),
                    leaveOpen: true);
            }

            Dispatcher = new InteractionDispatcher(Catalog, Registry, probes, Recorder);
        }

        public InteractionCommandCatalog Catalog { get; }

        public InteractionRegistry Registry { get; }

        public ThreadRecordingProbe Probe { get; }

        public MemoryStream? Sink { get; }

        public InteractionRecorder? Recorder { get; }

        public InteractionDispatcher Dispatcher { get; }

        public ConcurrentQueue<int> StageThreads { get; } = new();

        public void RegisterYieldingClick(string targetId)
        {
            var pipeline = new StagePipeline<ClickCommand>(
                new[] { new YieldingStage(this) });
            registrations.Add(
                Registry.Register(new ClickTarget(targetId, pipeline), true));
        }

        public void Dispose()
        {
            Dispatcher.Dispose();
            Recorder?.Dispose();
        }
    }

    // Captures are deterministic (a constant snapshot) so record and replay
    // hashes line up; the observed thread IDs are collected on the side.
    private sealed class ThreadRecordingProbe : IInteractionStateProbe
    {
        public ConcurrentQueue<int> CaptureThreads { get; } = new();

        public string Id => "thread-probe";

        public int Version => 1;

        public StateProbeSnapshot Capture()
        {
            CaptureThreads.Enqueue(Environment.CurrentManagedThreadId);
            return StateProbeSnapshot.FromJson("{\"value\":0}");
        }
    }

    private sealed class YieldingStage : IInteractionStage<ClickCommand>
    {
        private readonly ContextRuntime owner;

        public YieldingStage(ContextRuntime owner)
        {
            this.owner = owner;
        }

        public string Id => "click.yield";

        public int Order => 0;

        public async ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            owner.StageThreads.Enqueue(Environment.CurrentManagedThreadId);

            // The yield must be the stage's last act: ConfigureAwait(false)
            // hands the dispatcher's continuation to the thread pool, which is
            // exactly the resumption these tests guard against.
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ClickTarget : IInteractionTarget
    {
        private readonly StagePipeline<ClickCommand> pipeline;

        public ClickTarget(string id, StagePipeline<ClickCommand> pipeline)
        {
            Id = id;
            this.pipeline = pipeline;
        }

        public string Id { get; }

        public InteractionDescriptor Describe()
        {
            return new InteractionDescriptor(
                Id,
                null,
                "button",
                "Label",
                null,
                true,
                true,
                new[]
                {
                    new AvailableInteraction(
                        "click",
                        1,
                        ClickCommandSchema.Instance.Arguments),
                });
        }

        public bool TryGetPipeline<TCommand>(
            out IInteractionPipeline<TCommand>? resolved)
            where TCommand : struct, IInteractionCommand
        {
            if (typeof(TCommand) == typeof(ClickCommand))
            {
                resolved = (IInteractionPipeline<TCommand>)(object)pipeline;
                return true;
            }

            resolved = null;
            return false;
        }
    }
}
