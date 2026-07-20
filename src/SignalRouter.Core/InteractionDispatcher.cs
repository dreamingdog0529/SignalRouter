using System;
using System.Threading;
using System.Threading.Tasks;
using VitalRouter;

namespace SignalRouter
{
    public sealed class InteractionDispatcher : IInteractionDispatcher, IDisposable
    {
        private const string ExecuteStageId = "execute";
        private const string InvariantViolationCode = "SignalRouter.InvariantViolation";

        private readonly InteractionCommandCatalog catalog;
        private readonly InteractionRegistry registry;
        private readonly Router router;
        private readonly AsyncLocal<InteractionExecutionScope?> currentScope =
            new AsyncLocal<InteractionExecutionScope?>();
        private readonly object gate = new object();
        private long nextSequence;
        private Task queueTail = Task.CompletedTask;
        private InteractionExecutionScope? activeScope;
        private bool disposed;

        public InteractionDispatcher(
            InteractionCommandCatalog catalog,
            InteractionRegistry registry)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            router = new Router();
            router.AddFilter(CommandOrdering.Sequential);
            router.Subscribe(new RouterSubscriber(this));
        }

        public async ValueTask<InteractionResult> DispatchAsync<TCommand>(
            TCommand command,
            InteractionDispatchOptions options,
            CancellationToken cancellationToken = default)
            where TCommand : struct, IInteractionCommand
        {
            InteractionContract.RequireTargetId(command.TargetId, nameof(command));

            InteractionCommandCatalogEntry? entry;
            string commandName;
            int commandVersion;
            try
            {
                entry = catalog.Get<TCommand>();
                commandName = entry.WireName;
                commandVersion = entry.Version;
            }
            catch (InteractionCommandException exception)
            {
                entry = null;
                commandName = typeof(TCommand).Name;
                commandVersion = 1;
                var identity = AssignIdentity(chainQueue: false, out _);
                return Rejected(
                    identity,
                    command.TargetId,
                    commandName,
                    commandVersion,
                    options.Origin,
                    new RejectionInfo(exception.RejectionCode, exception.Message));
            }

            if (currentScope.Value != null)
            {
                var identity = AssignIdentity(chainQueue: false, out _);
                return Rejected(
                    identity,
                    command.TargetId,
                    commandName,
                    commandVersion,
                    options.Origin,
                    new RejectionInfo(
                        InteractionRejectionCode.ReentrantDispatch,
                        "DispatchAsync must not be called from an executing interaction; use InteractionContext.EnqueueContinuation."));
            }

            var request = AssignIdentity(chainQueue: true, out var queueSlot);
            InteractionResult result;
            InteractionExecutionScope? scope = null;
            var predecessorObserved = false;
            try
            {
                if (!await TryAwaitPredecessorAsync(queueSlot.Predecessor, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return CancelledBeforeStart(
                        request,
                        command.TargetId,
                        commandName,
                        commandVersion,
                        options.Origin);
                }

                predecessorObserved = true;
                if (cancellationToken.IsCancellationRequested)
                {
                    return CancelledBeforeStart(
                        request,
                        command.TargetId,
                        commandName,
                        commandVersion,
                        options.Origin);
                }

                var rejection = ResolvePipeline<TCommand>(
                    command,
                    entry!,
                    out var pipeline);
                if (rejection != null)
                {
                    return Rejected(
                        request,
                        command.TargetId,
                        commandName,
                        commandVersion,
                        options.Origin,
                        rejection);
                }

                var context = new InteractionContext(
                    request.Sequence,
                    request.RequestId,
                    options);
                var resolvedPipeline = pipeline!;
                scope = new InteractionExecutionScope(
                    context,
                    typeof(TCommand),
                    command,
                    token => resolvedPipeline.ExecuteAsync(command, context, token));
                context.AttachScope(scope);
                result = await PublishAsync(
                    scope,
                    command,
                    request,
                    commandName,
                    commandVersion,
                    options.Origin,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                queueSlot.Release(predecessorObserved);
            }

            StartContinuations(scope);
            return result;
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

            router.Dispose();
        }

        private async ValueTask<InteractionResult> PublishAsync<TCommand>(
            InteractionExecutionScope scope,
            TCommand command,
            RequestIdentity request,
            string commandName,
            int commandVersion,
            InteractionOrigin origin,
            CancellationToken cancellationToken)
            where TCommand : struct, IInteractionCommand
        {
            currentScope.Value = scope;
            activeScope = scope;
            try
            {
                await router.PublishAsync(command, cancellationToken).ConfigureAwait(false);
                return new InteractionResult(
                    request.Sequence,
                    request.RequestId,
                    command.TargetId,
                    commandName,
                    commandVersion,
                    origin,
                    InteractionStatus.Succeeded,
                    rejection: null,
                    fault: null,
                    SingleStage(InteractionStageStatus.Completed),
                    StateObservation.Empty,
                    StateObservation.Empty,
                    StateDiff.Empty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new InteractionResult(
                    request.Sequence,
                    request.RequestId,
                    command.TargetId,
                    commandName,
                    commandVersion,
                    origin,
                    InteractionStatus.Cancelled,
                    rejection: null,
                    fault: null,
                    SingleStage(InteractionStageStatus.Cancelled),
                    StateObservation.Empty,
                    StateObservation.Empty,
                    StateDiff.Empty);
            }
            catch (Exception exception)
            {
                return new InteractionResult(
                    request.Sequence,
                    request.RequestId,
                    command.TargetId,
                    commandName,
                    commandVersion,
                    origin,
                    InteractionStatus.Faulted,
                    rejection: null,
                    CreateFault(exception),
                    SingleStage(InteractionStageStatus.Faulted),
                    StateObservation.Empty,
                    StateObservation.Empty,
                    StateDiff.Empty);
            }
            finally
            {
                activeScope = null;
                currentScope.Value = null;
            }
        }

        private RejectionInfo? ResolvePipeline<TCommand>(
            TCommand command,
            InteractionCommandCatalogEntry entry,
            out IInteractionPipeline<TCommand>? pipeline)
            where TCommand : struct, IInteractionCommand
        {
            pipeline = null;
            if (!registry.TryResolve(command.TargetId, out var target))
            {
                return new RejectionInfo(
                    InteractionRejectionCode.TargetNotFound,
                    FormatRejection("Target '{0}' is not registered.", command.TargetId));
            }

            var descriptor = target!.Describe();
            if (!descriptor.Visible)
            {
                return new RejectionInfo(
                    InteractionRejectionCode.NotVisible,
                    FormatRejection("Target '{0}' is not visible.", command.TargetId));
            }

            if (!descriptor.Enabled)
            {
                return new RejectionInfo(
                    InteractionRejectionCode.Disabled,
                    FormatRejection("Target '{0}' is disabled.", command.TargetId));
            }

            var available = false;
            foreach (var interaction in descriptor.AvailableInteractions)
            {
                if (interaction.Version == entry.Version
                    && string.Equals(
                        interaction.WireName,
                        entry.WireName,
                        StringComparison.Ordinal))
                {
                    available = true;
                    break;
                }
            }

            if (!available
                || !target.TryGetPipeline(out pipeline)
                || pipeline == null)
            {
                pipeline = null;
                return new RejectionInfo(
                    InteractionRejectionCode.OperationNotAvailable,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Target '{0}' does not support '{1}@{2}'.",
                        command.TargetId,
                        entry.WireName,
                        entry.Version));
            }

            var validation = pipeline.Validate(in command);
            if (!validation.IsValid)
            {
                pipeline = null;
                return validation.Rejection;
            }

            return null;
        }

        private RequestIdentity AssignIdentity(bool chainQueue, out QueueSlot queueSlot)
        {
            long sequence;
            Task predecessor;
            TaskCompletionSource<bool>? tail = null;
            lock (gate)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(InteractionDispatcher));
                }

                sequence = checked(++nextSequence);
                predecessor = queueTail;
                if (chainQueue)
                {
                    tail = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    queueTail = tail.Task;
                }
            }

            queueSlot = new QueueSlot(predecessor, tail);
            return new RequestIdentity(sequence, Guid.NewGuid().ToString("N"));
        }

        private static async ValueTask<bool> TryAwaitPredecessorAsync(
            Task predecessor,
            CancellationToken cancellationToken)
        {
            if (predecessor.IsCompleted)
            {
                return true;
            }

            if (!cancellationToken.CanBeCanceled)
            {
                await predecessor.ConfigureAwait(false);
                return true;
            }

            var cancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                cancelled))
            {
                var completed = await Task.WhenAny(predecessor, cancelled.Task)
                    .ConfigureAwait(false);
                if (!ReferenceEquals(completed, predecessor))
                {
                    return false;
                }
            }

            await predecessor.ConfigureAwait(false);
            return true;
        }

        private void StartContinuations(InteractionExecutionScope? scope)
        {
            if (scope == null)
            {
                return;
            }

            var continuations = scope.CompleteAndDrain();
            for (var index = 0; index < continuations.Count; index++)
            {
                _ = continuations[index](this);
            }
        }

        private static InteractionResult Rejected(
            RequestIdentity request,
            string targetId,
            string commandName,
            int commandVersion,
            InteractionOrigin origin,
            RejectionInfo rejection)
        {
            return new InteractionResult(
                request.Sequence,
                request.RequestId,
                targetId,
                commandName,
                commandVersion,
                origin,
                InteractionStatus.Rejected,
                rejection,
                fault: null,
                StageProgress.Empty,
                StateObservation.Empty,
                StateObservation.Empty,
                StateDiff.Empty);
        }

        private static InteractionResult CancelledBeforeStart(
            RequestIdentity request,
            string targetId,
            string commandName,
            int commandVersion,
            InteractionOrigin origin)
        {
            return new InteractionResult(
                request.Sequence,
                request.RequestId,
                targetId,
                commandName,
                commandVersion,
                origin,
                InteractionStatus.Cancelled,
                rejection: null,
                fault: null,
                StageProgress.Empty,
                StateObservation.Empty,
                StateObservation.Empty,
                StateDiff.Empty);
        }

        private static StageProgress SingleStage(InteractionStageStatus status)
        {
            return new StageProgress(
                new[]
                {
                    new InteractionStageProgress(ExecuteStageId, 0, status),
                });
        }

        private static FaultInfo CreateFault(Exception exception)
        {
            var message = string.IsNullOrEmpty(exception.Message)
                ? "The interaction faulted without an exception message."
                : exception.Message;
            return new FaultInfo(
                exception.GetType().FullName ?? exception.GetType().Name,
                message,
                exception.StackTrace,
                exception is InteractionInvariantViolationException
                    ? InvariantViolationCode
                    : null,
                ExecuteStageId,
                0,
                Array.Empty<string>());
        }

        private static string FormatRejection(string format, string targetId)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                format,
                targetId);
        }

        private readonly struct RequestIdentity
        {
            public RequestIdentity(long sequence, string requestId)
            {
                Sequence = sequence;
                RequestId = requestId;
            }

            public long Sequence { get; }

            public string RequestId { get; }
        }

        private readonly struct QueueSlot
        {
            public QueueSlot(Task predecessor, TaskCompletionSource<bool>? tail)
            {
                Predecessor = predecessor;
                Tail = tail;
            }

            public Task Predecessor { get; }

            public TaskCompletionSource<bool>? Tail { get; }

            public void Release(bool predecessorObserved)
            {
                var tail = Tail;
                if (tail == null)
                {
                    return;
                }

                if (predecessorObserved)
                {
                    tail.TrySetResult(true);
                    return;
                }

                // The request left the queue before its predecessor finished
                // (cancelled while waiting); successors must still wait for the
                // predecessor to preserve single-flight FIFO execution.
                _ = Predecessor.ContinueWith(
                    static (_, state) =>
                        ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                    tail,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        private sealed class RouterSubscriber : IAsyncCommandSubscriber
        {
            private readonly InteractionDispatcher owner;

            public RouterSubscriber(InteractionDispatcher owner)
            {
                this.owner = owner;
            }

            public ValueTask ReceiveAsync<T>(T command, PublishContext context)
                where T : ICommand
            {
                var scope = owner.activeScope;
                if (scope == null)
                {
                    throw new InteractionInvariantViolationException(
                        "The router received a command without an active execution scope.");
                }

                if (scope.CommandType != typeof(T) || !scope.Command.Equals(command))
                {
                    throw new InteractionInvariantViolationException(
                        "The router received a command that does not match the active execution scope.");
                }

                return scope.ExecuteAsync(context.CancellationToken);
            }
        }
    }

    internal sealed class InteractionInvariantViolationException : InvalidOperationException
    {
        public InteractionInvariantViolationException(string message)
            : base(message)
        {
        }
    }
}
