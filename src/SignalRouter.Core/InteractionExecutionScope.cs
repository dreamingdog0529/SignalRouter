using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRouter
{
    internal sealed class InteractionExecutionScope
    {
        private readonly object gate = new object();
        private List<Func<IInteractionDispatcher, ValueTask<InteractionResult>>>? continuations;
        private bool terminal;

        internal InteractionExecutionScope(
            InteractionContext context,
            Type commandType,
            IInteractionCommand command,
            Func<CancellationToken, ValueTask> executeAsync)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            CommandType = commandType ?? throw new ArgumentNullException(nameof(commandType));
            Command = command ?? throw new ArgumentNullException(nameof(command));
            ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        }

        public InteractionContext Context { get; }

        public Type CommandType { get; }

        public IInteractionCommand Command { get; }

        public Func<CancellationToken, ValueTask> ExecuteAsync { get; }

        public void AddContinuation(
            Func<IInteractionDispatcher, ValueTask<InteractionResult>> continuation)
        {
            if (continuation == null)
            {
                throw new ArgumentNullException(nameof(continuation));
            }

            lock (gate)
            {
                if (terminal)
                {
                    throw new InvalidOperationException(
                        "Continuations cannot be enqueued after the interaction reached a terminal state.");
                }

                if (continuations == null)
                {
                    continuations =
                        new List<Func<IInteractionDispatcher, ValueTask<InteractionResult>>>();
                }

                continuations.Add(continuation);
            }
        }

        public IReadOnlyList<Func<IInteractionDispatcher, ValueTask<InteractionResult>>> CompleteAndDrain()
        {
            lock (gate)
            {
                terminal = true;
                IReadOnlyList<Func<IInteractionDispatcher, ValueTask<InteractionResult>>> drained =
                    continuations
                    ?? (IReadOnlyList<Func<IInteractionDispatcher, ValueTask<InteractionResult>>>)
                        Array.Empty<Func<IInteractionDispatcher, ValueTask<InteractionResult>>>();
                continuations = null;
                return drained;
            }
        }
    }
}
