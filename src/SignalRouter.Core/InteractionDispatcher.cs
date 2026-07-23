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
        private readonly InteractionStateProbeRegistry? probes;
        private readonly InteractionRecorder? recorder;
        private readonly Router router;
        private readonly AsyncLocal<InteractionExecutionScope?> currentScope =
            new AsyncLocal<InteractionExecutionScope?>();
        private readonly object gate = new object();
        private readonly object idempotencyGate = new object();
        private readonly IdempotencyCache idempotencyCache;

        // Split-phase submissions in flight, keyed by their external request ID
        // (ADR 0007). Registration happens inside AssignIdentity's lock so the
        // duplicate check, sequence reservation, FIFO chaining, and cancellation
        // registration are one linearization point; entries leave on terminal
        // completion.
        private readonly Dictionary<string, CancellationTokenSource> activeSubmissions =
            new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private long nextSequence;
        private Task queueTail = Task.CompletedTask;
        private InteractionExecutionScope? activeScope;
        private bool disposed;

        // Replay exclusivity (design §16.1): queueTail alone cannot prove idleness
        // because it is released before StartContinuations runs, and a posted
        // continuation has not yet re-entered the queue. activeDispatches spans a
        // dispatch through its continuation drain, and pendingContinuations covers
        // the posted-but-not-yet-enqueued window, so their sum is zero exactly when
        // no work can still touch probes or the FIFO.
        private int activeDispatches;
        private int pendingContinuations;
        private InteractionReplayLease? replayLease;
        private bool replayDispatchArmed;
        private int replayArmedThreadId;
        private int suppressedReplayContinuations;

        public InteractionDispatcher(
            InteractionCommandCatalog catalog,
            InteractionRegistry registry,
            int idempotencyCacheCapacity = DefaultIdempotencyCacheCapacity)
            : this(catalog, registry, null, idempotencyCacheCapacity)
        {
        }

        public InteractionDispatcher(
            InteractionCommandCatalog catalog,
            InteractionRegistry registry,
            InteractionStateProbeRegistry? probes,
            int idempotencyCacheCapacity = DefaultIdempotencyCacheCapacity)
            : this(catalog, registry, probes, null, idempotencyCacheCapacity)
        {
        }

        public InteractionDispatcher(
            InteractionCommandCatalog catalog,
            InteractionRegistry registry,
            InteractionStateProbeRegistry? probes,
            InteractionRecorder? recorder,
            int idempotencyCacheCapacity = DefaultIdempotencyCacheCapacity)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.probes = probes;
            if (recorder != null
                && !string.Equals(recorder.SessionId, registry.SessionEpoch, StringComparison.Ordinal))
            {
                // Probe hashes encode the registry epoch (ADR 0001); a recording whose
                // header advertises a different session could never replay against a
                // registry reconstructed from that header.
                throw new ArgumentException(
                    "The recorder's session ID must match the registry's session epoch.",
                    nameof(recorder));
            }

            this.recorder = recorder;
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

            // The cache-hit path never reaches AssignIdentity, so it needs its own
            // lease check: a cached result served mid-replay would contradict the
            // exclusive-lease contract even though it runs no side effects. The
            // replayer's own dispatches never carry an idempotency key, so this
            // rejects external callers only.
            lock (gate)
            {
                if (replayLease != null)
                {
                    throw ReplayLeaseRejection();
                }
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
                // A terminal-append failure happens after the side effects ran: the
                // executed result must stay cached (and satisfy concurrent waiters)
                // or a retry with the same key would repeat those side effects.
                var completed = (exception as InteractionRecordingException)?.CompletedResult;
                if (completed != null && IsIdempotentResultCacheable(completed))
                {
                    pending.TrySetResult(completed);
                    throw;
                }

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

        // Split-phase submission (ADR 0007): admission — identity, sequence, and
        // FIFO position — is fixed synchronously under the enqueue lock before
        // this method returns, so a transport can acknowledge acceptance before
        // the terminal result exists. The caller owns the request ID; duplicate
        // live IDs fail fast. Cancellation via TryCancel is a request, not a
        // guarantee — the terminal status on Completion is authoritative.
        public InteractionSubmission Submit<TCommand>(
            TCommand command,
            InteractionSubmissionOptions options,
            CancellationToken cancellationToken = default)
            where TCommand : struct, IInteractionCommand
        {
            InteractionContract.RequireTargetId(command.TargetId, nameof(command));

            // A defaulted options struct bypasses the validating constructor, so
            // the caller-owned identity is re-checked at the API boundary; every
            // path below relies on it being a real identifier.
            InteractionContract.RequireIdentifier(options.RequestId, nameof(options));

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
                return CompletedSubmission(Rejected(
                    AssignIdentity(chainQueue: false, out _, null, options.RequestId),
                    command.TargetId,
                    typeof(TCommand).Name,
                    1,
                    options.Origin,
                    new RejectionInfo(exception.RejectionCode, exception.Message)));
            }

            if (currentScope.Value != null)
            {
                return CompletedSubmission(Rejected(
                    AssignIdentity(chainQueue: false, out _, null, options.RequestId),
                    command.TargetId,
                    commandName,
                    commandVersion,
                    options.Origin,
                    new RejectionInfo(
                        InteractionRejectionCode.ReentrantDispatch,
                        "Submit must not be called from an executing interaction; use InteractionContext.EnqueueContinuation.")));
            }

            var dispatchOptions = new InteractionDispatchOptions(
                options.Origin,
                options.CorrelationId);
            var callerContext = SynchronizationContext.Current;
            RecordingRequestPayload? payload = null;
            if (recorder != null)
            {
                payload = new RecordingRequestPayload(
                    options.Origin,
                    commandName,
                    commandVersion,
                    command.TargetId,
                    InteractionRecordingRedaction.SerializeArguments(entry, command));
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            RequestIdentity request;
            QueueSlot queueSlot;
            try
            {
                request = AssignIdentity(
                    chainQueue: true,
                    out queueSlot,
                    payload,
                    options.RequestId,
                    cancellation);
            }
            catch
            {
                cancellation.Dispose();
                throw;
            }

            var started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = RunAdmittedAsync(
                command,
                dispatchOptions,
                entry,
                commandName,
                commandVersion,
                request,
                queueSlot,
                callerContext,
                started,
                cancellation);
            return new InteractionSubmission(
                InteractionAdmissionKind.Queued,
                request.RequestId,
                request.Sequence,
                started.Task,
                completion);
        }

        private async Task<InteractionResult> RunAdmittedAsync<TCommand>(
            TCommand command,
            InteractionDispatchOptions options,
            InteractionCommandCatalogEntry entry,
            string commandName,
            int commandVersion,
            RequestIdentity request,
            QueueSlot queueSlot,
            SynchronizationContext? callerContext,
            TaskCompletionSource<bool> started,
            CancellationTokenSource cancellation)
            where TCommand : struct, IInteractionCommand
        {
            // Runs synchronously up to the first await inside Submit's frame, so
            // the active-dispatch bracket is in place before Submit returns and a
            // replay lease can never be acquired in the admission-to-run window.
            lock (gate)
            {
                activeDispatches++;
            }

            try
            {
                return await ExecuteAdmittedAsync(
                    command,
                    options,
                    entry,
                    commandName,
                    commandVersion,
                    request,
                    queueSlot,
                    callerContext,
                    started,
                    cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    activeDispatches--;
                    activeSubmissions.Remove(request.RequestId);
                }

                cancellation.Dispose();
            }
        }

        // Requests cancellation of an in-flight submission. A true return means
        // the cancellation request was accepted — not that the outcome will be
        // Cancelled: a stage may reach its terminal state before observing the
        // token, and Completion's status stays authoritative. Unknown and
        // already-terminal request IDs return false, so cancelling twice or
        // after completion is a no-op query, not an error (design §8).
        public bool TryCancel(string requestId)
        {
            InteractionContract.RequireIdentifier(requestId, nameof(requestId));

            CancellationTokenSource? cancellation;
            lock (gate)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(InteractionDispatcher));
                }

                if (!activeSubmissions.TryGetValue(requestId, out cancellation))
                {
                    return false;
                }
            }

            try
            {
                // Outside the gate: cancellation callbacks (stage token
                // registrations) may run synchronously and must not execute
                // under the dispatcher's lock.
                cancellation!.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The submission reached its terminal state between the lookup
                // and the cancel; that is exactly the already-terminal case.
                return false;
            }

            return true;
        }

        private static InteractionSubmission CompletedSubmission(InteractionResult result)
        {
            return new InteractionSubmission(
                InteractionAdmissionKind.Completed,
                result.RequestId,
                result.Sequence,
                Task.FromResult(false),
                Task.FromResult(result));
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

        internal InteractionCommandCatalog Catalog
        {
            get { return catalog; }
        }

        internal InteractionRegistry Registry
        {
            get { return registry; }
        }

        internal InteractionStateProbeRegistry? Probes
        {
            get { return probes; }
        }

        internal InteractionRecorder? Recorder
        {
            get { return recorder; }
        }

        // Grants the replayer exclusive use of this dispatcher (design §16.1).
        // Reentrancy is checked before idleness because a stage-originated call
        // also leaves the queue tail incomplete and would otherwise be reported
        // as merely busy.
        internal InteractionReplayLease AcquireReplayLease()
        {
            if (currentScope.Value != null)
            {
                throw new InteractionReplayException(
                    InteractionReplayError.ReplayReentrancy,
                    "Replay must not be started from an executing interaction.");
            }

            lock (gate)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(InteractionDispatcher));
                }

                if (replayLease != null
                    || activeDispatches > 0
                    || pendingContinuations > 0
                    || !queueTail.IsCompleted)
                {
                    throw new InteractionReplayException(
                        InteractionReplayError.DispatcherBusy,
                        "Replay requires an exclusive idle dispatcher; dispatches or "
                        + "continuations are still in flight, or another replay holds "
                        + "the lease.");
                }

                var lease = new InteractionReplayLease(this);
                replayLease = lease;
                return lease;
            }
        }

        internal void ArmReplayDispatch(InteractionReplayLease lease)
        {
            lock (gate)
            {
                RequireCurrentLease(lease);

                // Admission is bound to the arming thread: the armed dispatch
                // enqueues during the synchronous prefix of DispatchAsync on this
                // same thread, so a concurrent caller racing into the armed window
                // from another thread can never be mistaken for the replay's own
                // dispatch.
                replayDispatchArmed = true;
                replayArmedThreadId = Environment.CurrentManagedThreadId;
            }
        }

        internal void DisarmReplayDispatch(InteractionReplayLease lease)
        {
            lock (gate)
            {
                RequireCurrentLease(lease);
                replayDispatchArmed = false;
            }
        }

        internal int TakeSuppressedReplayContinuations(InteractionReplayLease lease)
        {
            lock (gate)
            {
                RequireCurrentLease(lease);
                var count = suppressedReplayContinuations;
                suppressedReplayContinuations = 0;
                return count;
            }
        }

        internal void ReleaseReplayLease(InteractionReplayLease lease)
        {
            lock (gate)
            {
                if (ReferenceEquals(replayLease, lease))
                {
                    replayLease = null;
                    replayDispatchArmed = false;
                    suppressedReplayContinuations = 0;
                }
            }
        }

        private void RequireCurrentLease(InteractionReplayLease lease)
        {
            if (!ReferenceEquals(replayLease, lease))
            {
                throw new InvalidOperationException(
                    "The replay lease is no longer held by this dispatcher.");
            }
        }

        private static InvalidOperationException ReplayLeaseRejection()
        {
            return new InvalidOperationException(
                "The dispatcher is exclusively leased for replay; dispatch is "
                + "rejected until the replay completes.");
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
            // The active-dispatch count brackets the whole dispatch including its
            // continuation drain, so a replay lease cannot be acquired in the window
            // after the queue tail is released but before continuations are posted.
            lock (gate)
            {
                activeDispatches++;
            }

            try
            {
                return await ExecuteDispatchAsync(
                    command,
                    options,
                    entry,
                    commandName,
                    commandVersion,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    activeDispatches--;
                }
            }
        }

        private async ValueTask<InteractionResult> ExecuteDispatchAsync<TCommand>(
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

            // Step 3 (design §7.1): the request event's redacted argument payload is
            // serialized outside the enqueue lock; the append itself happens inside
            // AssignIdentity so request events land in sequence order (§15.1).
            RecordingRequestPayload? payload = null;
            if (recorder != null)
            {
                payload = new RecordingRequestPayload(
                    options.Origin,
                    commandName,
                    commandVersion,
                    command.TargetId,
                    InteractionRecordingRedaction.SerializeArguments(entry, command));
            }

            var request = AssignIdentity(chainQueue: true, out var queueSlot, payload);
            return await ExecuteAdmittedAsync(
                command,
                options,
                entry,
                commandName,
                commandVersion,
                request,
                queueSlot,
                callerContext,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        // The post-admission half of a dispatch: identity and FIFO position are
        // already fixed. Shared by DispatchAsync and the split-phase Submit path;
        // `started` (submission only) resolves true when execution genuinely
        // begins and false when the request terminates without starting.
        private async ValueTask<InteractionResult> ExecuteAdmittedAsync<TCommand>(
            TCommand command,
            InteractionDispatchOptions options,
            InteractionCommandCatalogEntry entry,
            string commandName,
            int commandVersion,
            RequestIdentity request,
            QueueSlot queueSlot,
            SynchronizationContext? callerContext,
            TaskCompletionSource<bool>? started,
            CancellationToken cancellationToken)
            where TCommand : struct, IInteractionCommand
        {
            InteractionResult result;
            InteractionExecutionScope? scope = null;
            StateProbeReading? beforeReading = null;
            var predecessorObserved = false;
            try
            {
                if (!await TryAwaitPredecessorAsync(queueSlot.Predecessor, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return RecordCompleted(CancelledBeforeStart(
                        request,
                        command.TargetId,
                        commandName,
                        commandVersion,
                        options.Origin));
                }

                predecessorObserved = true;
                if (cancellationToken.IsCancellationRequested)
                {
                    return RecordCompleted(CancelledBeforeStart(
                        request,
                        command.TargetId,
                        commandName,
                        commandVersion,
                        options.Origin));
                }

                // A recorder poisoned by an earlier append failure must not let
                // already-queued work execute stages unrecorded: recording is a
                // guarantee (§15.1), so this dispatch fails before any side effect.
                recorder?.ThrowIfFaulted();

                if (callerContext != null)
                {
                    await SwitchTo(callerContext);
                }

                // Step 5 (design §7.1): capture the before-state observation before any side
                // effect. This runs outside the result-normalizing catch below so that a probe
                // invariant violation (a null or uncanonicalizable snapshot) fails fast per
                // ADR 0001 instead of being reported as an application fault and cached as an
                // idempotent outcome. Validation has no side effects (§13.2), so capturing here
                // — before resolution — yields the same before-state as capturing at publish.
                // A null probe registry keeps the pre-probe behavior (empty observations).
                beforeReading = probes?.Read();

                // Started resolves true only after every pre-start step — the
                // predecessor drain, cancellation and recorder checks, and the
                // before-state capture — has succeeded, so a probe invariant
                // violation still reports "never ran" to submission consumers.
                started?.TrySetResult(true);

                RejectionInfo? rejection = null;
                Exception? fault = null;
                var cancelledDuring = false;
                try
                {
                    rejection = ResolvePipeline<TCommand>(command, entry, out var pipeline);
                    if (rejection == null)
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
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelledDuring = true;
                }
                catch (Exception exception)
                {
                    fault = exception;
                }
                finally
                {
                    activeScope = null;
                    currentScope.Value = null;
                }

                // The publish above resumed with ConfigureAwait(false), so a stage
                // that genuinely yielded leaves this method on a thread-pool thread.
                // The after-state capture below reads probes and target descriptors
                // (main-thread-only state under the §17.2 policy), so marshal back
                // onto the caller's context before touching them.
                if (callerContext != null)
                {
                    await SwitchTo(callerContext);
                }

                // Step 8/9 (design §7.1): build the terminal result outside the normalizing
                // catch. The after-state capture (CaptureAfter) therefore also fails fast on a
                // probe invariant violation rather than being swallowed into a Faulted result.
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
                else if (fault != null)
                {
                    result = Faulted(
                        request,
                        command.TargetId,
                        commandName,
                        commandVersion,
                        options.Origin,
                        fault,
                        scope,
                        CaptureAfter(beforeReading));
                }
                else if (cancelledDuring)
                {
                    result = CancelledDuringExecution(
                        request,
                        command.TargetId,
                        commandName,
                        commandVersion,
                        options.Origin,
                        scope,
                        CaptureAfter(beforeReading));
                }
                else
                {
                    result = Succeeded(
                        request,
                        command.TargetId,
                        commandName,
                        commandVersion,
                        options.Origin,
                        scope,
                        CaptureAfter(beforeReading));
                }

                // Step 10 (design §7.1): append the terminal event inside the outer
                // try so a failed append still releases the queue slot below.
                try
                {
                    RecordCompleted(result);
                }
                catch
                {
                    // The interaction still reached a terminal state: mark the scope
                    // terminal and start its queued continuations so they are not
                    // silently dropped and the retained context cannot accept more.
                    // Each continuation re-enters dispatch and fails fast against the
                    // failed recorder.
                    StartContinuations(scope, callerContext);
                    throw;
                }
            }
            finally
            {
                queueSlot.Release(predecessorObserved);

                // Idempotent when execution already began; every other exit —
                // pre-start cancellation, poisoned recorder, probe invariant
                // violation — resolves Started to "never ran" so a submission
                // consumer can always distinguish the two outcomes.
                started?.TrySetResult(false);
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

        private RequestIdentity AssignIdentity(
            bool chainQueue,
            out QueueSlot queueSlot,
            RecordingRequestPayload? payload = null,
            string? externalRequestId = null,
            CancellationTokenSource? submissionCancellation = null)
        {
            long sequence;
            string requestId;
            Task predecessor;
            TaskCompletionSource<bool>? tail = null;
            lock (gate)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(InteractionDispatcher));
                }

                // While a replay lease is held only the replayer's own armed
                // dispatch — identified by the arming thread — may enter the
                // queue; anything else would interleave with replay probe
                // readings and sequence order (design §16.1).
                if (replayLease != null
                    && !(replayDispatchArmed
                        && replayArmedThreadId == Environment.CurrentManagedThreadId))
                {
                    throw ReplayLeaseRejection();
                }

                // Two results under one live external ID would alias an
                // externally owned identity — whether the second submission
                // queues or completes immediately. Core trusts submitter
                // uniqueness, so a collision is a programming error, not a
                // request to deduplicate (ADR 0007 — deduplication lives in
                // the protocol ledger).
                if (externalRequestId != null
                    && activeSubmissions.ContainsKey(externalRequestId))
                {
                    throw new InvalidOperationException(
                        "A submission with the same request ID is already in flight.");
                }

                sequence = checked(++nextSequence);
                requestId = externalRequestId ?? Guid.NewGuid().ToString("N");

                // The request event is appended under the enqueue lock — the only
                // place the §15.1 sequence-order guarantee holds under concurrent
                // enqueue — and before the queue-tail swap, so a failed append
                // leaves the FIFO chain untouched instead of stranding successors
                // behind a tail that will never complete. Durability before the
                // first stage follows because the line is flushed at enqueue and
                // stages only run after dequeue.
                if (payload != null)
                {
                    recorder!.AppendRequested(
                        sequence,
                        requestId,
                        payload.Origin,
                        payload.CommandName,
                        payload.CommandVersion,
                        payload.TargetId,
                        payload.ArgumentsJson);
                }

                // Registered only after the append can no longer throw, so a
                // failed admission never strands a submission entry that no
                // completion path will ever remove.
                if (submissionCancellation != null)
                {
                    activeSubmissions.Add(requestId, submissionCancellation);
                }

                predecessor = queueTail;
                if (chainQueue)
                {
                    tail = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    queueTail = tail.Task;
                }
            }

            queueSlot = new QueueSlot(predecessor, tail);
            return new RequestIdentity(sequence, requestId);
        }

        private InteractionResult RecordCompleted(InteractionResult result)
        {
            if (recorder == null)
            {
                return result;
            }

            try
            {
                recorder.AppendCompleted(result);
            }
            catch (Exception exception) when (
                !(exception is InteractionInvariantViolationException))
            {
                // The interaction already executed; the result is real but was not
                // persisted. Carrying it on the exception lets the idempotency path
                // keep the cache truthful so a retry does not repeat side effects.
                throw new InteractionRecordingException(
                    InteractionRecordingError.RecorderFailed,
                    "The interaction executed but its terminal recording event could "
                    + "not be appended.",
                    exception,
                    result);
            }

            return result;
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
            if (continuations.Count == 0)
            {
                return;
            }

            lock (gate)
            {
                if (replayLease != null)
                {
                    // The lease guarantees this scope belongs to a replayed dispatch.
                    // Schema v1 recordings carry no parent linkage, so a live
                    // continuation cannot be matched against recorded entries; it is
                    // suppressed and counted, and the replayer stops with
                    // ContinuationRequested instead of double-executing side effects.
                    suppressedReplayContinuations += continuations.Count;
                    return;
                }

                pendingContinuations += continuations.Count;
            }

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

        internal static SwitchToContextAwaitable SwitchTo(SynchronizationContext context)
        {
            return new SwitchToContextAwaitable(context);
        }

        private static InteractionResult Succeeded(
            RequestIdentity request,
            string targetId,
            string commandName,
            int commandVersion,
            InteractionOrigin origin,
            InteractionExecutionScope? scope,
            StateCapture state)
        {
            var tracker = scope?.Context.Tracker;
            var stages = tracker != null && tracker.RecordedAnything
                ? tracker.BuildCompleted()
                : SingleStage(InteractionStageStatus.Completed);
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
                stages,
                state.Before,
                state.After,
                state.Diff);
        }

        private static InteractionResult Faulted(
            RequestIdentity request,
            string targetId,
            string commandName,
            int commandVersion,
            InteractionOrigin origin,
            Exception exception,
            InteractionExecutionScope? scope,
            StateCapture state)
        {
            var tracker = scope?.Context.Tracker;
            StageProgress stages;
            FaultInfo fault;
            if (tracker != null && tracker.HasPending)
            {
                // A stage was in flight when the exception surfaced: it is the terminal faulted
                // stage, preceded by every stage that completed.
                stages = tracker.BuildTerminal(InteractionStageStatus.Faulted);
                fault = CreateFault(
                    exception,
                    tracker.PendingStageId,
                    tracker.PendingStageIndex,
                    tracker.CompletedStageIds());
            }
            else
            {
                // Opaque pipeline or a fault raised outside any stage: fall back to the synthetic
                // single "execute" stage.
                stages = SingleStage(InteractionStageStatus.Faulted);
                fault = CreateFault(exception);
            }

            return new InteractionResult(
                request.Sequence,
                request.RequestId,
                targetId,
                commandName,
                commandVersion,
                origin,
                InteractionStatus.Faulted,
                rejection: null,
                fault,
                stages,
                state.Before,
                state.After,
                state.Diff);
        }

        private static InteractionResult CancelledDuringExecution(
            RequestIdentity request,
            string targetId,
            string commandName,
            int commandVersion,
            InteractionOrigin origin,
            InteractionExecutionScope? scope,
            StateCapture state)
        {
            var tracker = scope?.Context.Tracker;
            StageProgress stages;
            var effectiveState = state;
            if (tracker != null && tracker.HasPending)
            {
                stages = tracker.BuildTerminal(InteractionStageStatus.Cancelled);
            }
            else if (tracker != null && tracker.IsStageDriven)
            {
                // A stage pipeline cancelled before its first stage: no stage ran, so the result
                // carries no stages and stays out of the idempotency cache (no side effects).
                // Cancellation before the first stage must not change state (design §8.1), so it
                // reports no observation regardless of the probe registry.
                stages = StageProgress.Empty;
                effectiveState = StateCapture.Empty;
            }
            else
            {
                // Opaque pipeline that ran and observed cancellation: keep the synthetic single
                // stage so the cancellation is retained as a possibly-side-effecting outcome.
                stages = SingleStage(InteractionStageStatus.Cancelled);
            }

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
                stages,
                effectiveState.Before,
                effectiveState.After,
                effectiveState.Diff);
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

        // Step 8 (design §7.1): capture the after-state observation by re-reading the same
        // probes captured before publish, and derive the before/after/diff triple. A null
        // before reading (no probe registry, or a path with no side effects) yields empty
        // observations, matching pre-probe behavior.
        private static StateCapture CaptureAfter(StateProbeReading? before)
        {
            if (before == null)
            {
                return StateCapture.Empty;
            }

            var after = before.ReadSame();
            return new StateCapture(
                before.ToObservation(),
                after.ToObservation(),
                StateProbeReading.Diff(before, after));
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
            return CreateFault(exception, ExecuteStageId, 0, Array.Empty<string>());
        }

        private static FaultInfo CreateFault(
            Exception exception,
            string failedStageId,
            int failedStageIndex,
            IEnumerable<string> completedStageIds)
        {
            var message = string.IsNullOrEmpty(exception.Message)
                ? "The interaction faulted without an exception message."
                : exception.Message;
            return new FaultInfo(
                exception.GetType().FullName ?? exception.GetType().Name,
                message,
                exception.StackTrace,
                ApplicationCodeFor(exception),
                failedStageId,
                failedStageIndex,
                completedStageIds);
        }

        private static string? ApplicationCodeFor(Exception exception)
        {
            if (exception is InteractionInvariantViolationException)
            {
                return InvariantViolationCode;
            }

            return exception is InteractionFaultException fault
                ? fault.ApplicationCode
                : null;
        }

        private static string FormatRejection(string format, string targetId)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                format,
                targetId);
        }

        private readonly struct StateCapture
        {
            public static readonly StateCapture Empty = new StateCapture(
                StateObservation.Empty,
                StateObservation.Empty,
                StateDiff.Empty);

            public StateCapture(StateObservation before, StateObservation after, StateDiff diff)
            {
                Before = before;
                After = after;
                Diff = diff;
            }

            public StateObservation Before { get; }

            public StateObservation After { get; }

            public StateDiff Diff { get; }
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

        private sealed class RecordingRequestPayload
        {
            public RecordingRequestPayload(
                InteractionOrigin origin,
                string commandName,
                int commandVersion,
                string targetId,
                byte[] argumentsJson)
            {
                Origin = origin;
                CommandName = commandName;
                CommandVersion = commandVersion;
                TargetId = targetId;
                ArgumentsJson = argumentsJson;
            }

            public InteractionOrigin Origin { get; }

            public string CommandName { get; }

            public int CommandVersion { get; }

            public string TargetId { get; }

            public byte[] ArgumentsJson { get; }
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
                // recorder's interaction_completed event, so there is no caller to
                // propagate to here. The pending count is released only after the
                // synchronous part of the dispatch has chained the queue tail, so the
                // idleness predicate never has a gap between "posted" and "enqueued".
                try
                {
                    _ = continuation(dispatcher);
                }
                finally
                {
                    dispatcher.OnContinuationDispatched();
                }
            }
        }

        private void OnContinuationDispatched()
        {
            lock (gate)
            {
                pendingContinuations--;
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

        internal readonly struct SwitchToContextAwaitable
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

    // Exclusive replay ownership of a dispatcher (design §16.1). While held, only
    // dispatches the replayer arms may enter the queue, and continuations drained
    // from replayed stages are counted instead of scheduled. Disposal releases the
    // lease and clears all suppression state, so an aborted replay cannot leave
    // the dispatcher rejecting normal work.
    internal sealed class InteractionReplayLease : IDisposable
    {
        private readonly InteractionDispatcher dispatcher;

        internal InteractionReplayLease(InteractionDispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
        }

        // Marks the immediately following dispatch as replay-owned. Arming is
        // synchronous by design: the dispatch enqueues (and is admission-checked)
        // during the synchronous prefix of DispatchAsync, before Disarm runs.
        public void ArmDispatch()
        {
            dispatcher.ArmReplayDispatch(this);
        }

        public void DisarmDispatch()
        {
            dispatcher.DisarmReplayDispatch(this);
        }

        // Returns and resets the number of continuations suppressed since the last
        // call; the replayer reads it after every dispatch.
        public int TakeSuppressedContinuationCount()
        {
            return dispatcher.TakeSuppressedReplayContinuations(this);
        }

        public void Dispose()
        {
            dispatcher.ReleaseReplayLease(this);
        }
    }
}
