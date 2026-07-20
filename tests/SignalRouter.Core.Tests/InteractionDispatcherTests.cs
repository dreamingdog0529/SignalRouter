using System.Collections.Concurrent;
using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class InteractionDispatcherTests
{
    [Test]
    public async Task SucceededResultCarriesCatalogMetadataAndSingleStage()
    {
        using var harness = new Harness();
        harness.Register("menu.start");

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options(InteractionOrigin.Human));

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(result.Sequence, Is.EqualTo(1));
            Assert.That(result.RequestId, Is.Not.Empty);
            Assert.That(result.CommandName, Is.EqualTo("click"));
            Assert.That(result.CommandVersion, Is.EqualTo(1));
            Assert.That(result.Origin, Is.EqualTo(InteractionOrigin.Human));
            Assert.That(result.Stages.Stages, Has.Count.EqualTo(1));
            Assert.That(result.Stages.Stages[0].Id, Is.EqualTo("execute"));
            Assert.That(
                result.Stages.Stages[0].Status,
                Is.EqualTo(InteractionStageStatus.Completed));
            Assert.That(harness.Log, Is.EqualTo(new[] { "menu.start" }));
        });
    }

    [Test]
    public async Task ConcurrentDispatchesExecuteInFifoSequenceOrder()
    {
        using var harness = new Harness();
        for (var index = 0; index < 20; index++)
        {
            harness.Register("target." + index);
        }

        var tasks = new List<Task<InteractionResult>>();
        for (var index = 0; index < 20; index++)
        {
            var command = new ClickCommand("target." + index);
            tasks.Add(Task.Run(async () =>
                await harness.Dispatcher.DispatchAsync(command, Options()).AsTask()));
        }

        var results = await Task.WhenAll(tasks);
        var sequences = results.Select(result => result.Sequence).OrderBy(value => value).ToArray();
        var executionOrder = harness.Log.ToArray();
        var sequenceForTarget = results.ToDictionary(
            result => result.TargetId,
            result => result.Sequence);
        var executionSequences = executionOrder.Select(id => sequenceForTarget[id]).ToArray();

        NUnitCompat.Multiple(() =>
        {
            Assert.That(sequences, Is.EqualTo(Enumerable.Range(1, 20).Select(v => (long)v)));
            Assert.That(
                executionSequences,
                Is.Ordered,
                "Execution order must follow the assigned FIFO sequence.");
            Assert.That(executionOrder, Has.Length.EqualTo(20));
        });
    }

    [Test]
    public async Task SequenceIsAssignedAtEnqueueWhileAPredecessorIsRunning()
    {
        using var harness = new Harness();
        var blocker = harness.Register("menu.first", gate: true);
        harness.Register("menu.second");

        var first = harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.first"),
            Options()).AsTask();
        await blocker.Started.Task;

        var second = harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.second"),
            Options()).AsTask();

        blocker.Release();
        var firstResult = await first;
        var secondResult = await second;

        NUnitCompat.Multiple(() =>
        {
            Assert.That(firstResult.Sequence, Is.EqualTo(1));
            Assert.That(secondResult.Sequence, Is.EqualTo(2));
            Assert.That(harness.Log, Is.EqualTo(new[] { "menu.first", "menu.second" }));
        });
    }

    [Test]
    public async Task RejectionsProduceNoSideEffects()
    {
        using var harness = new Harness();
        harness.Register("visible");
        harness.Register("hidden", visible: false);
        harness.Register("disabled", enabled: false);
        harness.Register("invalid", validation:
            InteractionValidation.Reject(
                InteractionRejectionCode.InvalidArguments,
                "rejected by pipeline"));

        var unknownTarget = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("missing"),
            Options());
        var notVisible = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("hidden"),
            Options());
        var disabled = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("disabled"),
            Options());
        var invalid = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("invalid"),
            Options());
        var operationUnavailable = await harness.Dispatcher.DispatchAsync(
            new SetValueCommand("visible", "x"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            AssertRejected(unknownTarget, InteractionRejectionCode.TargetNotFound);
            AssertRejected(notVisible, InteractionRejectionCode.NotVisible);
            AssertRejected(disabled, InteractionRejectionCode.Disabled);
            AssertRejected(invalid, InteractionRejectionCode.InvalidArguments);
            AssertRejected(operationUnavailable, InteractionRejectionCode.OperationNotAvailable);
            Assert.That(harness.Log, Is.Empty);
        });
    }

    [Test]
    public async Task UnregisteredCommandIsRejectedWithoutThrowing()
    {
        var clickOnly = new InteractionCommandCatalogBuilder()
            .Register("click", 1, ClickCommandSchema.Instance, true)
            .Build();
        using var harness = new Harness(clickOnly);
        harness.Register("menu.start");

        var result = await harness.Dispatcher.DispatchAsync(
            new SetValueCommand("menu.start", "value"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Rejected));
            Assert.That(
                result.Rejection!.Code,
                Is.EqualTo(InteractionRejectionCode.CommandNotRegistered));
            Assert.That(result.CommandName, Is.EqualTo(nameof(SetValueCommand)));
            Assert.That(harness.Log, Is.Empty);
        });
    }

    [Test]
    public async Task CancellationBeforeStartYieldsCancelledWithNoSideEffects()
    {
        using var harness = new Harness();
        harness.Register("menu.start");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options(),
            cts.Token);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Cancelled));
            Assert.That(result.Stages.Stages, Is.Empty);
            Assert.That(harness.Log, Is.Empty);
        });
    }

    [Test]
    public async Task CancellationWhileQueuedStillReleasesSuccessors()
    {
        using var harness = new Harness();
        var blocker = harness.Register("menu.first", gate: true);
        harness.Register("menu.second");
        using var cts = new CancellationTokenSource();

        var first = harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.first"),
            Options()).AsTask();
        await blocker.Started.Task;

        var queued = harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.second"),
            Options(),
            cts.Token).AsTask();
        cts.Cancel();
        var queuedResult = await queued;

        blocker.Release();
        var firstResult = await first;

        var third = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.second"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(queuedResult.Status, Is.EqualTo(InteractionStatus.Cancelled));
            Assert.That(firstResult.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(third.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(harness.Log, Is.EqualTo(new[] { "menu.first", "menu.second" }));
        });
    }

    [Test]
    public async Task CancellationDuringExecutionYieldsTerminalCancelledStage()
    {
        using var harness = new Harness();
        using var cts = new CancellationTokenSource();
        harness.Register("menu.start", observeCancellation: cts);

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options(),
            cts.Token);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Cancelled));
            Assert.That(result.Stages.Stages, Has.Count.EqualTo(1));
            Assert.That(
                result.Stages.Stages[0].Status,
                Is.EqualTo(InteractionStageStatus.Cancelled));
        });
    }

    [Test]
    public async Task PipelineExceptionIsNormalizedToFaultedResult()
    {
        using var harness = new Harness();
        harness.Register("menu.start", fault: new InvalidOperationException("boom"));

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Faulted));
            Assert.That(
                result.Fault!.ExceptionType,
                Is.EqualTo(typeof(InvalidOperationException).FullName));
            Assert.That(result.Fault.Message, Is.EqualTo("boom"));
            Assert.That(result.Fault.FailedStageId, Is.EqualTo("execute"));
            Assert.That(
                result.Stages.Stages[0].Status,
                Is.EqualTo(InteractionStageStatus.Faulted));
        });
    }

    [Test]
    public async Task ReentrantDispatchIsRejectedWithoutDeadlock()
    {
        using var harness = new Harness();
        InteractionResult? inner = null;
        harness.Register("menu.start", onExecute: async (context, _) =>
        {
            inner = await harness.Dispatcher.DispatchAsync(
                new ClickCommand("menu.start"),
                Options());
        });

        var outer = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(outer.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(inner, Is.Not.Null);
            Assert.That(inner!.Status, Is.EqualTo(InteractionStatus.Rejected));
            Assert.That(
                inner.Rejection!.Code,
                Is.EqualTo(InteractionRejectionCode.ReentrantDispatch));
        });
    }

    [Test]
    public async Task ContinuationRunsAfterTerminalStateWithFreshIdentity()
    {
        using var harness = new Harness();
        var continuationDone = new TaskCompletionSource<InteractionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Register("menu.follow");
        harness.Register("menu.start", onExecute: (context, _) =>
        {
            context.EnqueueContinuation(
                new ClickCommand("menu.follow"),
                Options());
            return default;
        });
        harness.OnExecuted = (id, result) =>
        {
            if (id == "menu.follow")
            {
                continuationDone.TrySetResult(result);
            }
        };

        var outer = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());
        var continuation = await continuationDone.Task;

        NUnitCompat.Multiple(() =>
        {
            Assert.That(outer.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(outer.Sequence, Is.EqualTo(1));
            Assert.That(continuation.Sequence, Is.GreaterThan(outer.Sequence));
            Assert.That(continuation.RequestId, Is.Not.EqualTo(outer.RequestId));
            Assert.That(harness.Log, Is.EqualTo(new[] { "menu.start", "menu.follow" }));
        });
    }

    [Test]
    public void ContinuationAfterTerminalStateThrows()
    {
        using var harness = new Harness();
        InteractionContext? captured = null;
        harness.Register("menu.start", onExecute: (context, _) =>
        {
            captured = context;
            return default;
        });

        _ = harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options()).AsTask().GetAwaiter().GetResult();

        NUnitCompat.ThatThrows(
            () => captured!.EnqueueContinuation(new ClickCommand("menu.start"), Options()),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task DisposeStopsFurtherDispatch()
    {
        var harness = new Harness();
        harness.Register("menu.start");
        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());
        harness.Dispose();
        harness.Dispose();

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Succeeded));
            NUnitCompat.ThatThrows(
                () => harness.Dispatcher.DispatchAsync(
                    new ClickCommand("menu.start"),
                    Options()).AsTask().GetAwaiter().GetResult(),
                Throws.TypeOf<ObjectDisposedException>());
        });
    }

    private static InteractionDispatchOptions Options(
        InteractionOrigin origin = InteractionOrigin.Test)
    {
        return new InteractionDispatchOptions(origin);
    }

    private static void AssertRejected(
        InteractionResult result,
        InteractionRejectionCode code)
    {
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Rejected));
        Assert.That(result.Rejection!.Code, Is.EqualTo(code));
        Assert.That(result.Stages.Stages, Is.Empty);
        Assert.That(result.Before, Is.EqualTo(result.After));
    }

    private sealed class Harness : IDisposable
    {
        private readonly InteractionRegistry registry;
        private readonly List<IInteractionTargetRegistration> registrations = new();
        private readonly ConcurrentQueue<string> log = new();

        public Harness(InteractionCommandCatalog? catalog = null)
        {
            var resolved = catalog ?? InteractionCommandCatalog.CreateMvp();
            registry = new InteractionRegistry(resolved, "session-1");
            Dispatcher = new InteractionDispatcher(resolved, registry);
        }

        public InteractionDispatcher Dispatcher { get; }

        public IReadOnlyList<string> Log
        {
            get { return log.ToArray(); }
        }

        public Action<string, InteractionResult>? OnExecuted { get; set; }

        public Blocker Register(
            string targetId,
            bool visible = true,
            bool enabled = true,
            bool gate = false,
            InteractionValidation? validation = null,
            Exception? fault = null,
            CancellationTokenSource? observeCancellation = null,
            Func<InteractionContext, CancellationToken, ValueTask>? onExecute = null)
        {
            var blocker = new Blocker(gate);
            var pipeline = new HarnessPipeline(
                targetId,
                log,
                blocker,
                validation ?? InteractionValidation.Valid,
                fault,
                observeCancellation,
                onExecute,
                result => OnExecuted?.Invoke(targetId, result));
            var target = new HarnessTarget(targetId, visible, enabled, pipeline);
            registrations.Add(registry.Register(target, true));
            return blocker;
        }

        public void Dispose()
        {
            Dispatcher.Dispose();
        }
    }

    private sealed class Blocker
    {
        private readonly TaskCompletionSource<bool>? gate;

        public Blocker(bool gated)
        {
            if (gated)
            {
                gate = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public TaskCompletionSource<bool> Started { get; } =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Wait
        {
            get { return gate?.Task ?? Task.CompletedTask; }
        }

        public void Release()
        {
            gate?.TrySetResult(true);
        }
    }

    private sealed class HarnessTarget : IInteractionTarget
    {
        private readonly bool visible;
        private readonly bool enabled;
        private readonly object pipeline;

        public HarnessTarget(
            string id,
            bool visible,
            bool enabled,
            object pipeline)
        {
            Id = id;
            this.visible = visible;
            this.enabled = enabled;
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
                visible,
                enabled,
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

    private sealed class HarnessPipeline : IInteractionPipeline<ClickCommand>
    {
        private readonly string targetId;
        private readonly ConcurrentQueue<string> log;
        private readonly Blocker blocker;
        private readonly InteractionValidation validation;
        private readonly Exception? fault;
        private readonly CancellationTokenSource? observeCancellation;
        private readonly Func<InteractionContext, CancellationToken, ValueTask>? onExecute;
        private readonly Action<InteractionResult>? report;

        public HarnessPipeline(
            string targetId,
            ConcurrentQueue<string> log,
            Blocker blocker,
            InteractionValidation validation,
            Exception? fault,
            CancellationTokenSource? observeCancellation,
            Func<InteractionContext, CancellationToken, ValueTask>? onExecute,
            Action<InteractionResult>? report)
        {
            this.targetId = targetId;
            this.log = log;
            this.blocker = blocker;
            this.validation = validation;
            this.fault = fault;
            this.observeCancellation = observeCancellation;
            this.onExecute = onExecute;
            this.report = report;
        }

        public InteractionValidation Validate(in ClickCommand command)
        {
            return validation;
        }

        public async ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            blocker.Started.TrySetResult(true);
            await blocker.Wait;

            if (observeCancellation != null)
            {
                observeCancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (fault != null)
            {
                log.Enqueue(targetId);
                throw fault;
            }

            log.Enqueue(targetId);
            if (onExecute != null)
            {
                await onExecute(context, cancellationToken);
            }

            report?.Invoke(SucceededMarker(context, command));
        }

        private static InteractionResult SucceededMarker(
            InteractionContext context,
            ClickCommand command)
        {
            return new InteractionResult(
                context.Sequence,
                context.RequestId,
                command.TargetId,
                "click",
                1,
                context.Options.Origin,
                InteractionStatus.Succeeded,
                null,
                null,
                new StageProgress(new[]
                {
                    new InteractionStageProgress("execute", 0, InteractionStageStatus.Completed),
                }),
                StateObservation.Empty,
                StateObservation.Empty,
                StateDiff.Empty);
        }
    }
}
