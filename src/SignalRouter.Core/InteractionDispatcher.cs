using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using VitalRouter;

namespace SignalRouter
{
    public sealed class InteractionDispatcher : IInteractionDispatcher, IDisposable
    {
        private const string ExecuteStageId = "execute";
        private const string InvariantViolationCode = "SignalRouter.InvariantViolation";
        private const int DefaultIdempotencyCacheCapacity = 1024;

        private readonly InteractionCommandCatalog catalog;
        private readonly InteractionRegistry registry;
        private readonly Router router;
        private readonly AsyncLocal<InteractionExecutionScope?> currentScope =
            new AsyncLocal<InteractionExecutionScope?>();
        private readonly object gate = new object();
        private readonly object idempotencyGate = new object();
        private readonly IdempotencyCache idempotencyCache;
        private long nextSequence;
        private Task queueTail = Task.CompletedTask;
        private InteractionExecutionScope? activeScope;
        private bool disposed;

        public InteractionDispatcher(
            InteractionCommandCatalog catalog,
            InteractionRegistry registry,
            int idempotencyCacheCapacity = DefaultIdempotencyCacheCapacity)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            if (idempotencyCacheCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(idempotencyCacheCapacity),
                    idempotencyCacheCapacity,
                    "Idempotency cache capacity must be positive.");
            }

            idempotencyCache = new IdempotencyCache(idempotencyCacheCapacity);
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

            InteractionCommandCatalogEntry entry;
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
                var identity = AssignIdentity(chainQueue: false, out _);
                return Rejected(
                    identity,
                    command.TargetId,
                    typeof(TCommand).Name,
                    1,
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

            var idempotencyKey = options.IdempotencyKey;
            if (idempotencyKey == null)
            {
                return await DispatchCoreAsync(
                    command,
                    options,
                    entry,
                    commandName,
                    commandVersion,
                    cancellationToken).ConfigureAwait(false);
            }

            Task<InteractionResult>? existing;
            TaskCompletionSource<InteractionResult> pending;
            lock (idempotencyGate)
            {
                if (idempotencyCache.TryGet(idempotencyKey, out existing))
                {
                    pending = null!;
                }
                else
                {
                    pending = new TaskCompletionSource<InteractionResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    idempotencyCache.Add(idempotencyKey, pending.Task);
                }
            }

            if (existing != null)
            {
                return await existing.ConfigureAwait(false);
            }

            InteractionResult result;
            try
            {
                result = await DispatchCoreAsync(
                    command,
                    options,
                    entry,
                    commandName,
                    commandVersion,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (idempotencyGate)
                {
                    idempotencyCache.Remove(idempotencyKey);
                }

                pending.TrySetException(exception);
                throw;
            }

            // Rejections and pre-start cancellations carry no side effects, so they must
            // not poison the cache; only executed outcomes are retained for deduplication.
            if (!IsIdempotentResultCacheable(result))
            {
                lock (idempotencyGate)
                {
                    idempotencyCache.Remove(idempotencyKey);
                }
            }

            pending.TrySetResult(result);
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

        private async ValueTask<InteractionResult> DispatchCoreAsync<TCommand>(
            TCommand command,
            InteractionDispatchOptions options,
            InteractionCommandCatalogEntry entry,
            string commandName,
            int commandVersion,
            CancellationToken cancellationToken)
            where TCommand : struct, IInteractionCommand
        {
            // Capture the caller's context (the Unity main thread under the main-thread
            // policy) so dequeued resolution and execution marshal back onto it instead
            // of resuming on an arbitrary thread-pool thread.
            var callerContext = SynchronizationContext.Current;
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

                if (callerContext != null)
                {
                    await SwitchTo(callerContext);
                }

                try
                {
                    var rejection = ResolvePipeline<TCommand>(command, entry, out var pipeline);
                    if (rejection != null)
                    {
                        result = Rejected(
                            request,
                            command.TargetId,
                            commandName,
                            commandVersion,
                            options.Origin,
                            rejection);
                    }
                    else
                    {
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
                        currentScope.Value = scope;
                        activeScope = scope;
                        await router.PublishAsync(command, cancellationToken).ConfigureAwait(false);
                        result = Succeeded(
                            request,
                            command.TargetId,
                            commandName,
                            commandVersion,
                            options.Origin);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result = CancelledDuringExecution(
                        request,
                        command.TargetId,
                        commandName,
                        commandVersion,
                        options.Origin);
                }
                catch (Exception exception)
                {
                    result = Faulted(
                        request,
                        command.TargetId,
                        commandName,
                        commandVersion,
                        options.Origin,
                        exception);
                }
                finally
                {
                    activeScope = null;
                    currentScope.Value = null;
                }
            }
            finally
            {
                queueSlot.Release(predecessorObserved);
            }

            StartContinuations(scope, callerContext);
            return result;
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

        private void StartContinuations(
            InteractionExecutionScope? scope,
            SynchronizationContext? callerContext)
        {
            if (scope == null)
            {
                return;
            }

            var continuations = scope.CompleteAndDrain();
            for (var index = 0; index < continuations.Count; index++)
            {
                // Schedule across an asynchronous boundary so a synchronously completing
                // continuation chain unwinds the stack between links instead of recursing
                // through DispatchAsync until it overflows.
                var state = new ContinuationState(this, continuations[index]);
                if (callerContext != null)
                {
                    callerContext.Post(PostContinuation, state);
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(RunContinuation, state);
                }
            }
        }

        private static readonly SendOrPostCallback PostContinuation =
            static state => ((ContinuationState)state!).Invoke();

        private static readonly WaitCallback RunContinuation =
            static state => ((ContinuationState)state!).Invoke();

        private static bool IsIdempotentResultCacheable(InteractionResult result)
        {
            switch (result.Status)
            {
                case InteractionStatus.Succeeded:
                case InteractionStatus.Faulted:
                    return true;
                case InteractionStatus.Cancelled:
                    // Cancellation before execution leaves no side effects (empty stages);
                    // cancellation during execution may have, so it is retained.
                    return result.Stages.Stages.Count > 0;
                default:
                    return false;
            }
        }

        private static SwitchToContextAwaitable SwitchTo(SynchronizationContext context)
        {
            return new SwitchToContextAwaitable(context);
        }

        private static InteractionResult Succeeded(
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
                InteractionStatus.Succeeded,
                rejection: null,
                fault: null,
                SingleStage(InteractionStageStatus.Completed),
                StateObservation.Empty,
                StateObservation.Empty,
                StateDiff.Empty);
        }

        private static InteractionResult Faulted(
            RequestIdentity request,
            string targetId,
            string commandName,
            int commandVersion,
            InteractionOrigin origin,
            Exception exception)
        {
            return new InteractionResult(
                request.Sequence,
                request.RequestId,
                targetId,
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

        private static InteractionResult CancelledDuringExecution(
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
                SingleStage(InteractionStageStatus.Cancelled),
                StateObservation.Empty,
                StateObservation.Empty,
                StateDiff.Empty);
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

        private sealed class ContinuationState
        {
            private readonly InteractionDispatcher dispatcher;
            private readonly Func<IInteractionDispatcher, ValueTask<InteractionResult>> continuation;

            public ContinuationState(
                InteractionDispatcher dispatcher,
                Func<IInteractionDispatcher, ValueTask<InteractionResult>> continuation)
            {
                this.dispatcher = dispatcher;
                this.continuation = continuation;
            }

            public void Invoke()
            {
                // The continuation re-enters the FIFO queue and normalizes its own
                // outcome into a result; its terminal status is observed through the
                // recorder (item 5), so there is no caller to propagate to here.
                _ = continuation(dispatcher);
            }
        }

        private sealed class IdempotencyCache
        {
            private readonly int capacity;
            private readonly Dictionary<string, Task<InteractionResult>> entries;
            private readonly Queue<string> insertionOrder;

            public IdempotencyCache(int capacity)
            {
                this.capacity = capacity;
                entries = new Dictionary<string, Task<InteractionResult>>(StringComparer.Ordinal);
                insertionOrder = new Queue<string>();
            }

            public bool TryGet(string key, out Task<InteractionResult>? task)
            {
                if (entries.TryGetValue(key, out var found))
                {
                    task = found;
                    return true;
                }

                task = null;
                return false;
            }

            public void Add(string key, Task<InteractionResult> task)
            {
                entries[key] = task;
                insertionOrder.Enqueue(key);
                while (entries.Count > capacity && insertionOrder.Count > 0)
                {
                    var oldest = insertionOrder.Dequeue();
                    entries.Remove(oldest);
                }
            }

            public void Remove(string key)
            {
                entries.Remove(key);
            }
        }

        private readonly struct SwitchToContextAwaitable
        {
            private readonly SynchronizationContext context;

            public SwitchToContextAwaitable(SynchronizationContext context)
            {
                this.context = context;
            }

            public Awaiter GetAwaiter()
            {
                return new Awaiter(context);
            }

            public readonly struct Awaiter : ICriticalNotifyCompletion
            {
                private static readonly SendOrPostCallback Invoke =
                    static state => ((Action)state!)();

                private readonly SynchronizationContext context;

                public Awaiter(SynchronizationContext context)
                {
                    this.context = context;
                }

                public bool IsCompleted
                {
                    get { return ReferenceEquals(SynchronizationContext.Current, context); }
                }

                public void OnCompleted(Action continuation)
                {
                    context.Post(Invoke, continuation);
                }

                public void UnsafeOnCompleted(Action continuation)
                {
                    context.Post(Invoke, continuation);
                }

                public void GetResult()
                {
                }
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
