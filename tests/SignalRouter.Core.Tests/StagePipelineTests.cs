using System.Collections.Concurrent;
using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class StagePipelineTests
{
    [Test]
    public void ConstructionRejectsAnEmptyStageSet()
    {
        NUnitCompat.Throws<ArgumentException>(
            () => new StagePipeline<ClickCommand>(Array.Empty<IInteractionStage<ClickCommand>>()));
    }

    [Test]
    public void ConstructionRejectsDuplicateStageIds()
    {
        var log = new ConcurrentQueue<string>();
        NUnitCompat.Throws<ArgumentException>(() => new StagePipeline<ClickCommand>(new[]
        {
            new Stage("click.apply", 10, log),
            new Stage("click.apply", 20, log),
        }));
    }

    [Test]
    public void ConstructionRejectsDuplicateStageOrders()
    {
        var log = new ConcurrentQueue<string>();
        NUnitCompat.Throws<ArgumentException>(() => new StagePipeline<ClickCommand>(new[]
        {
            new Stage("click.apply", 10, log),
            new Stage("click.sound", 10, log),
        }));
    }

    [Test]
    public async Task StagesExecuteInAscendingOrderAndReportCompletedProgress()
    {
        using var harness = new StageHarness();
        var log = new ConcurrentQueue<string>();

        // Supplied out of order to prove the pipeline sorts by Order.
        harness.Register("menu.start", new StagePipeline<ClickCommand>(new[]
        {
            new Stage("click.sound", 30, log),
            new Stage("click.apply", 10, log),
            new Stage("click.transition", 20, log),
        }));

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(log.ToArray(), Is.EqualTo(new[] { "click.apply", "click.transition", "click.sound" }));
            Assert.That(result.Stages.Stages, Has.Count.EqualTo(3));
            Assert.That(
                result.Stages.Stages.Select(stage => stage.Id).ToArray(),
                Is.EqualTo(new[] { "click.apply", "click.transition", "click.sound" }));
            Assert.That(
                result.Stages.Stages.Select(stage => stage.Index).ToArray(),
                Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(
                result.Stages.Stages.All(stage => stage.Status == InteractionStageStatus.Completed),
                Is.True);
        });
    }

    [Test]
    public async Task ValidatorRejectionSurfacesWithoutRunningStages()
    {
        using var harness = new StageHarness();
        var log = new ConcurrentQueue<string>();

        harness.Register("menu.start", new StagePipeline<ClickCommand>(
            new[] { new Stage("click.apply", 10, log) },
            _ => InteractionValidation.Reject(
                InteractionRejectionCode.InvalidArguments,
                "not allowed")));

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Rejected));
            Assert.That(result.Rejection!.Code, Is.EqualTo(InteractionRejectionCode.InvalidArguments));
            Assert.That(result.Stages.Stages, Is.Empty);
            Assert.That(log.ToArray(), Is.Empty);
        });
    }

    [Test]
    public async Task FaultingStageBecomesTheTerminalFaultedStageAndStopsLaterStages()
    {
        using var harness = new StageHarness();
        var log = new ConcurrentQueue<string>();
        var boom = new InvalidOperationException("stage failed");

        harness.Register("menu.start", new StagePipeline<ClickCommand>(new[]
        {
            new Stage("click.apply", 10, log),
            new Stage("click.transition", 20, log, _ => throw boom),
            new Stage("click.sound", 30, log),
        }));

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Faulted));
            Assert.That(log.ToArray(), Is.EqualTo(new[] { "click.apply", "click.transition" }));
            Assert.That(result.Stages.Stages, Has.Count.EqualTo(2));
            Assert.That(result.Stages.Stages[0].Status, Is.EqualTo(InteractionStageStatus.Completed));
            Assert.That(result.Stages.Stages[1].Id, Is.EqualTo("click.transition"));
            Assert.That(result.Stages.Stages[1].Status, Is.EqualTo(InteractionStageStatus.Faulted));
            Assert.That(result.Fault!.FailedStageId, Is.EqualTo("click.transition"));
            Assert.That(result.Fault!.FailedStageIndex, Is.EqualTo(1));
            Assert.That(result.Fault!.CompletedStageIds, Is.EqualTo(new[] { "click.apply" }));
            Assert.That(result.Fault!.Message, Is.EqualTo("stage failed"));
        });
    }

    [Test]
    public async Task CancellationDuringAStageBecomesTheTerminalCancelledStage()
    {
        using var harness = new StageHarness();
        var log = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();

        harness.Register("menu.start", new StagePipeline<ClickCommand>(new[]
        {
            new Stage("click.apply", 10, log),
            new Stage("click.transition", 20, log, token =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            }),
            new Stage("click.sound", 30, log),
        }));

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options(),
            cts.Token);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Cancelled));
            Assert.That(log.ToArray(), Is.EqualTo(new[] { "click.apply", "click.transition" }));
            Assert.That(result.Stages.Stages, Has.Count.EqualTo(2));
            Assert.That(result.Stages.Stages[0].Status, Is.EqualTo(InteractionStageStatus.Completed));
            Assert.That(result.Stages.Stages[1].Id, Is.EqualTo("click.transition"));
            Assert.That(result.Stages.Stages[1].Status, Is.EqualTo(InteractionStageStatus.Cancelled));
        });
    }

    [Test]
    public void CancellationBeforeTheFirstStageRecordsNoStageAndStaysUncacheable()
    {
        // A cancellation observed before the first stage runs (design §12) must leave the progress
        // empty so the dispatcher reports no side effects and the idempotency cache does not retain
        // it. See IsIdempotentResultCacheable, which keys cacheability on a non-empty stage list.
        var context = new InteractionContext(
            1,
            "req-1",
            new InteractionDispatchOptions(InteractionOrigin.Test));
        var log = new ConcurrentQueue<string>();
        var pipeline = new StagePipeline<ClickCommand>(new[]
        {
            new Stage("click.apply", 10, log),
            new Stage("click.transition", 20, log),
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            (Func<Task>)(() =>
                pipeline.ExecuteAsync(new ClickCommand("menu.start"), context, cts.Token).AsTask()));

        NUnitCompat.Multiple(() =>
        {
            Assert.That(log.ToArray(), Is.Empty);
            Assert.That(context.Tracker.IsStageDriven, Is.True);
            Assert.That(context.Tracker.HasPending, Is.False);
            Assert.That(context.Tracker.RecordedAnything, Is.False);
        });
    }

    [Test]
    public async Task StagesResumeOnTheCallerSynchronizationContext()
    {
        // Under the main-thread policy (design §17.2), a stage that completes asynchronously must
        // resume on the caller context so later stages keep running on the Unity main thread.
        using var harness = new StageHarness();
        using var syncContext = new SingleThreadSynchronizationContext();
        var secondStageThread = 0;

        harness.Register("menu.start", new StagePipeline<ClickCommand>(new IInteractionStage<ClickCommand>[]
        {
            new AsyncStage("click.apply", 10, async () => await Task.Yield()),
            new AsyncStage("click.transition", 20, () =>
            {
                secondStageThread = Environment.CurrentManagedThreadId;
                return default;
            }),
        }));

        await syncContext.Run(async () =>
        {
            await harness.Dispatcher
                .DispatchAsync(new ClickCommand("menu.start"), Options())
                .AsTask();
        });

        Assert.That(secondStageThread, Is.EqualTo(syncContext.ThreadId));
    }

    private static InteractionDispatchOptions Options(
        InteractionOrigin origin = InteractionOrigin.Test)
    {
        return new InteractionDispatchOptions(origin);
    }

    private sealed class Stage : IInteractionStage<ClickCommand>
    {
        private readonly ConcurrentQueue<string> log;
        private readonly Action<CancellationToken>? action;

        public Stage(
            string id,
            int order,
            ConcurrentQueue<string> log,
            Action<CancellationToken>? action = null)
        {
            Id = id;
            Order = order;
            this.log = log;
            this.action = action;
        }

        public string Id { get; }

        public int Order { get; }

        public ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            log.Enqueue(Id);
            action?.Invoke(cancellationToken);
            return default;
        }
    }

    private sealed class AsyncStage : IInteractionStage<ClickCommand>
    {
        private readonly Func<ValueTask> body;

        public AsyncStage(string id, int order, Func<ValueTask> body)
        {
            Id = id;
            Order = order;
            this.body = body;
        }

        public string Id { get; }

        public int Order { get; }

        public async ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            await body();
        }
    }

    private sealed class StageTarget : IInteractionTarget
    {
        private readonly IInteractionPipeline<ClickCommand> pipeline;

        public StageTarget(string id, IInteractionPipeline<ClickCommand> pipeline)
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
                    new AvailableInteraction("click", 1, ClickCommandSchema.Instance.Arguments),
                });
        }

        public bool TryGetPipeline<TCommand>(
            out IInteractionPipeline<TCommand>? resolved)
            where TCommand : struct, IInteractionCommand
        {
            if (typeof(TCommand) == typeof(ClickCommand))
            {
                resolved = (IInteractionPipeline<TCommand>)pipeline;
                return true;
            }

            resolved = null;
            return false;
        }
    }

    private sealed class StageHarness : IDisposable
    {
        private readonly InteractionRegistry registry;
        private readonly List<IInteractionTargetRegistration> registrations = new();

        public StageHarness()
        {
            var catalog = InteractionCommandCatalog.CreateMvp();
            registry = new InteractionRegistry(catalog, "session-1");
            Dispatcher = new InteractionDispatcher(catalog, registry);
        }

        public InteractionDispatcher Dispatcher { get; }

        public void Register(string targetId, StagePipeline<ClickCommand> pipeline)
        {
            var target = new StageTarget(targetId, pipeline);
            registrations.Add(registry.Register(target, true));
        }

        public void Dispose()
        {
            Dispatcher.Dispose();
        }
    }
}
