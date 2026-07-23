using System.Collections.Concurrent;
using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class InteractionSubmissionTests
{
    [Test]
    public async Task SubmitFixesIdentityAtAdmissionAndCompletesWithTheSameIdentity()
    {
        using var runtime = new SubmissionRuntime();
        runtime.RegisterClick("menu.start");

        var submission = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent, "corr-1"));

        Assert.That(submission.Kind, Is.EqualTo(InteractionAdmissionKind.Queued));
        Assert.That(submission.RequestId, Is.EqualTo("req-1"));
        Assert.That(submission.Sequence, Is.EqualTo(1));

        var result = await submission.Completion;
        Assert.That(result.RequestId, Is.EqualTo("req-1"));
        Assert.That(result.Sequence, Is.EqualTo(submission.Sequence));
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(result.Origin, Is.EqualTo(InteractionOrigin.Agent));
        Assert.That(await submission.Started, Is.True);
    }

    [Test]
    public async Task ConcurrentSubmissionsExecuteInAdmissionOrder()
    {
        using var runtime = new SubmissionRuntime();
        runtime.RegisterClick("menu.start");

        var first = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));
        var second = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-2", InteractionOrigin.Agent));

        Assert.That(first.Sequence, Is.LessThan(second.Sequence));
        await Task.WhenAll(first.Completion, second.Completion);
        Assert.That(
            runtime.ExecutedRequestIds,
            Is.EqualTo(new[] { "req-1", "req-2" }));
    }

    [Test]
    public async Task StartedResolvesOnlyWhenThePredecessorDrains()
    {
        using var runtime = new SubmissionRuntime();
        runtime.RegisterBlockingClick("menu.start");

        var blocked = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));
        await blocked.Started;
        var waiting = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-2", InteractionOrigin.Agent));

        Assert.That(waiting.Started.IsCompleted, Is.False);
        runtime.ReleaseBlockedStage();
        Assert.That(await waiting.Started, Is.True);
        runtime.ReleaseBlockedStage();
        await Task.WhenAll(blocked.Completion, waiting.Completion);
    }

    [Test]
    public async Task UnregisteredCommandsCompleteImmediatelyWithoutQueueAdmission()
    {
        using var runtime = new SubmissionRuntime(registerClickCommand: false);

        var submission = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));

        Assert.That(submission.Kind, Is.EqualTo(InteractionAdmissionKind.Completed));
        Assert.That(submission.RequestId, Is.EqualTo("req-1"));
        Assert.That(submission.Completion.IsCompleted, Is.True);
        Assert.That(await submission.Started, Is.False);
        var result = await submission.Completion;
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Rejected));
        Assert.That(
            result.Rejection!.Code,
            Is.EqualTo(InteractionRejectionCode.CommandNotRegistered));
        Assert.That(result.RequestId, Is.EqualTo("req-1"));
    }

    [Test]
    public async Task TargetRejectionsCarryTheExternalIdentityThroughTheQueue()
    {
        using var runtime = new SubmissionRuntime();

        var submission = runtime.Dispatcher.Submit(
            new ClickCommand("missing.target"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));

        Assert.That(submission.Kind, Is.EqualTo(InteractionAdmissionKind.Queued));
        var result = await submission.Completion;
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Rejected));
        Assert.That(result.Rejection!.Code, Is.EqualTo(InteractionRejectionCode.TargetNotFound));
        Assert.That(result.RequestId, Is.EqualTo("req-1"));
        Assert.That(await submission.Started, Is.True);
    }

    [Test]
    public async Task PreStartCancellationResolvesStartedAsNeverRan()
    {
        using var runtime = new SubmissionRuntime();
        runtime.RegisterBlockingClick("menu.start");
        using var cancellation = new CancellationTokenSource();

        var blocked = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));
        await blocked.Started;
        var cancelled = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-2", InteractionOrigin.Agent),
            cancellation.Token);
        cancellation.Cancel();

        var result = await cancelled.Completion;
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Cancelled));
        Assert.That(result.Stages.Stages, Is.Empty);
        Assert.That(await cancelled.Started, Is.False);

        runtime.ReleaseBlockedStage();
        await blocked.Completion;
    }

    [Test]
    public async Task ConcurrentDuplicateRequestIdsFailFast()
    {
        using var runtime = new SubmissionRuntime();
        runtime.RegisterBlockingClick("menu.start");

        var pending = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));

        NUnitCompat.Throws<InvalidOperationException>(() => runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent)));

        runtime.ReleaseBlockedStage();
        await pending.Completion;

        // Terminal completion releases the identity for reuse.
        var reused = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));
        runtime.ReleaseBlockedStage();
        await reused.Completion;
    }

    [Test]
    public async Task SubmitFromAnExecutingInteractionIsRejected()
    {
        using var runtime = new SubmissionRuntime();
        InteractionSubmission? nested = null;
        runtime.RegisterClick("menu.start", () =>
        {
            nested = runtime.Dispatcher.Submit(
                new ClickCommand("menu.start"),
                new InteractionSubmissionOptions("req-nested", InteractionOrigin.Agent));
        });

        var outer = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));

        await outer.Completion;
        Assert.That(nested, Is.Not.Null);
        Assert.That(nested!.Kind, Is.EqualTo(InteractionAdmissionKind.Completed));
        var nestedResult = await nested.Completion;
        Assert.That(
            nestedResult.Rejection!.Code,
            Is.EqualTo(InteractionRejectionCode.ReentrantDispatch));
    }

    [Test]
    public void SubmitUnderAReplayLeaseThrows()
    {
        using var runtime = new SubmissionRuntime();
        runtime.RegisterClick("menu.start");
        using (runtime.Dispatcher.AcquireReplayLease())
        {
            NUnitCompat.Throws<InvalidOperationException>(() => runtime.Dispatcher.Submit(
                new ClickCommand("menu.start"),
                new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent)));
        }
    }

    [Test]
    public void SubmitOnADisposedDispatcherThrows()
    {
        var runtime = new SubmissionRuntime();
        runtime.Dispose();

        NUnitCompat.Throws<ObjectDisposedException>(() => runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent)));
    }

    [Test]
    public void SubmissionOptionsValidateTheirIdentity()
    {
        NUnitCompat.Throws<ArgumentNullException>(
            () => _ = new InteractionSubmissionOptions(null!, InteractionOrigin.Agent));
        NUnitCompat.Throws<ArgumentException>(
            () => _ = new InteractionSubmissionOptions(" padded ", InteractionOrigin.Agent));
        NUnitCompat.Throws<ArgumentException>(
            () => _ = new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent, ""));
    }

    [Test]
    public async Task CancellingBeforeStartProducesACancelledResultWithoutStages()
    {
        using var runtime = new SubmissionRuntime();
        runtime.RegisterBlockingClick("menu.start");

        var blocked = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));
        await blocked.Started;
        var waiting = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-2", InteractionOrigin.Agent));

        Assert.That(runtime.Dispatcher.TryCancel("req-2"), Is.True);
        var result = await waiting.Completion;
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Cancelled));
        Assert.That(result.Stages.Stages, Is.Empty);
        Assert.That(await waiting.Started, Is.False);
        Assert.That(runtime.ExecutedRequestIds, Does.Not.Contain("req-2"));

        runtime.ReleaseBlockedStage();
        await blocked.Completion;
    }

    [Test]
    public async Task CancellingMidExecutionIsObservedByTheStagePipeline()
    {
        using var runtime = new SubmissionRuntime();
        runtime.RegisterBlockingClick("menu.start");

        var submission = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));
        Assert.That(await submission.Started, Is.True);

        Assert.That(runtime.Dispatcher.TryCancel("req-1"), Is.True);
        var result = await submission.Completion;
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Cancelled));
        Assert.That(result.Stages.Stages, Has.Count.EqualTo(1));
        Assert.That(
            result.Stages.Stages[0].Status,
            Is.EqualTo(InteractionStageStatus.Cancelled));
    }

    [Test]
    public async Task CancelReturnsFalseForUnknownAndTerminalRequests()
    {
        using var runtime = new SubmissionRuntime();
        runtime.RegisterClick("menu.start");

        Assert.That(runtime.Dispatcher.TryCancel("req-unknown"), Is.False);

        var submission = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-1", InteractionOrigin.Agent));
        await submission.Completion;

        Assert.That(runtime.Dispatcher.TryCancel("req-1"), Is.False);
    }

    [Test]
    public void CancelOnADisposedDispatcherThrows()
    {
        var runtime = new SubmissionRuntime();
        runtime.Dispose();

        NUnitCompat.Throws<ObjectDisposedException>(
            () => _ = runtime.Dispatcher.TryCancel("req-1"));
    }

    [Test]
    public async Task DecodedCommandsSubmitThroughTheSameSplitPhasePath()
    {
        using var runtime = new SubmissionRuntime();
        runtime.RegisterClick("menu.start");
        using var arguments = System.Text.Json.JsonDocument.Parse("{}");
        var decoded = runtime.Catalog.Decode("click", 1, "menu.start", arguments.RootElement);

        var submission = decoded.Submit(
            runtime.Dispatcher,
            new InteractionSubmissionOptions("req-wire", InteractionOrigin.Agent));

        Assert.That(submission.Kind, Is.EqualTo(InteractionAdmissionKind.Queued));
        var result = await submission.Completion;
        Assert.That(result.RequestId, Is.EqualTo("req-wire"));
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(result.CommandName, Is.EqualTo("click"));
        Assert.That(runtime.ExecutedRequestIds, Is.EqualTo(new[] { "req-wire" }));

        // The decode-then-submit path and the typed path produce the same
        // terminal shape (MVP criterion 1 groundwork).
        var typed = await runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-typed", InteractionOrigin.Agent)).Completion;
        Assert.That(typed.Status, Is.EqualTo(result.Status));
        Assert.That(typed.CommandName, Is.EqualTo(result.CommandName));
        Assert.That(typed.CommandVersion, Is.EqualTo(result.CommandVersion));
    }

    [Test]
    public async Task SubmissionsRecordTheExternalRequestId()
    {
        using var runtime = new SubmissionRuntime(record: true);
        runtime.RegisterClick("menu.start");

        var submission = runtime.Dispatcher.Submit(
            new ClickCommand("menu.start"),
            new InteractionSubmissionOptions("req-recorded", InteractionOrigin.Agent));
        await submission.Completion;
        runtime.Recorder!.Dispose();

        runtime.Sink!.Position = 0;
        var recording = InteractionRecordingReader.Load(runtime.Sink);
        Assert.That(recording.Interactions, Has.Count.EqualTo(1));
        Assert.That(recording.Interactions[0].RequestId, Is.EqualTo("req-recorded"));
        Assert.That(recording.Interactions[0].Origin, Is.EqualTo(InteractionOrigin.Agent));
    }

    // A minimal dispatcher fixture with an optionally blocking click pipeline so
    // tests can hold the FIFO open and observe admission-versus-start timing.
    private sealed class SubmissionRuntime : IDisposable
    {
        private readonly List<IInteractionTargetRegistration> registrations = new();
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> blockedStages = new();

        public SubmissionRuntime(bool record = false, bool registerClickCommand = true)
        {
            Catalog = registerClickCommand
                ? InteractionCommandCatalog.CreateMvp()
                : new InteractionCommandCatalogBuilder().Build();
            Registry = new InteractionRegistry(Catalog, "session-1");
            if (record)
            {
                Sink = new MemoryStream();
                Recorder = new InteractionRecorder(
                    Sink,
                    new InteractionRecorderOptions("session-1", "build-1"),
                    leaveOpen: true);
            }

            Dispatcher = new InteractionDispatcher(Catalog, Registry, null, Recorder);
        }

        public InteractionCommandCatalog Catalog { get; }

        public InteractionRegistry Registry { get; }

        public MemoryStream? Sink { get; }

        public InteractionRecorder? Recorder { get; }

        public InteractionDispatcher Dispatcher { get; }

        public ConcurrentQueue<string> ExecutedRequests { get; } = new();

        public string[] ExecutedRequestIds
        {
            get { return ExecutedRequests.ToArray(); }
        }

        public void RegisterClick(string targetId, Action? onExecute = null)
        {
            var pipeline = new StagePipeline<ClickCommand>(
                new[] { new RecordingStage(this, onExecute) });
            registrations.Add(Registry.Register(new ClickTarget(targetId, pipeline), true));
        }

        public void RegisterBlockingClick(string targetId)
        {
            var pipeline = new StagePipeline<ClickCommand>(
                new[] { new BlockingStage(this) });
            registrations.Add(Registry.Register(new ClickTarget(targetId, pipeline), true));
        }

        public void ReleaseBlockedStage()
        {
            // Started resolves before the stage body runs, so the gate may not be
            // enqueued yet when a test asks to release it; wait briefly for it.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            TaskCompletionSource<bool>? gate;
            while (!blockedStages.TryDequeue(out gate))
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("No blocked stage appeared within the timeout.");
                }

                Thread.Sleep(5);
            }

            gate!.TrySetResult(true);
        }

        public void Dispose()
        {
            while (blockedStages.TryDequeue(out var gate))
            {
                gate.TrySetResult(true);
            }

            foreach (var registration in registrations)
            {
                registration.Dispose();
            }

            Dispatcher.Dispose();
            Recorder?.Dispose();
        }

        private sealed class RecordingStage : IInteractionStage<ClickCommand>
        {
            private readonly SubmissionRuntime owner;
            private readonly Action? onExecute;

            public RecordingStage(SubmissionRuntime owner, Action? onExecute)
            {
                this.owner = owner;
                this.onExecute = onExecute;
            }

            public string Id => "click.record";

            public int Order => 0;

            public ValueTask ExecuteAsync(
                ClickCommand command,
                InteractionContext context,
                CancellationToken cancellationToken)
            {
                owner.ExecutedRequests.Enqueue(context.RequestId);
                onExecute?.Invoke();
                return default;
            }
        }

        private sealed class BlockingStage : IInteractionStage<ClickCommand>
        {
            private readonly SubmissionRuntime owner;

            public BlockingStage(SubmissionRuntime owner)
            {
                this.owner = owner;
            }

            public string Id => "click.block";

            public int Order => 0;

            public async ValueTask ExecuteAsync(
                ClickCommand command,
                InteractionContext context,
                CancellationToken cancellationToken)
            {
                var gate = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                owner.blockedStages.Enqueue(gate);
                owner.ExecutedRequests.Enqueue(context.RequestId);
                using (cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                    gate))
                {
                    await gate.Task.ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
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
}
