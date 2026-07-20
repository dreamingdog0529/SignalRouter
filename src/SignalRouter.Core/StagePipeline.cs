using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRouter
{
    // A pipeline that executes an ordered set of explicitly registered stages (design §10). It is
    // the sanctioned way to obtain real per-stage progress: it reports each stage to the tracker on
    // the interaction context so the dispatcher can build accurate StageProgress and FaultInfo.
    // Opaque pipelines that implement IInteractionPipeline directly remain valid and are represented
    // by the dispatcher's single synthetic "execute" stage.
    public sealed class StagePipeline<TCommand> : IInteractionPipeline<TCommand>
        where TCommand : struct, IInteractionCommand
    {
        private readonly IInteractionStage<TCommand>[] stages;
        private readonly Func<TCommand, InteractionValidation>? validator;

        public StagePipeline(
            IEnumerable<IInteractionStage<TCommand>> stages,
            Func<TCommand, InteractionValidation>? validator = null)
        {
            if (stages == null)
            {
                throw new ArgumentNullException(nameof(stages));
            }

            var ordered = new List<IInteractionStage<TCommand>>();
            foreach (var stage in stages)
            {
                if (stage == null)
                {
                    throw new ArgumentException(
                        "A stage pipeline must not contain null stages.",
                        nameof(stages));
                }

                InteractionContract.RequireIdentifier(stage.Id, nameof(stages));
                ordered.Add(stage);
            }

            if (ordered.Count == 0)
            {
                throw new ArgumentException(
                    "A stage pipeline requires at least one stage.",
                    nameof(stages));
            }

            // Startup validation (design §10.1): duplicate IDs or orders must fail immediately.
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var orders = new HashSet<int>();
            foreach (var stage in ordered)
            {
                if (!ids.Add(stage.Id))
                {
                    throw new ArgumentException(
                        "Stage IDs must be unique within a pipeline.",
                        nameof(stages));
                }

                if (!orders.Add(stage.Order))
                {
                    throw new ArgumentException(
                        "Stage orders must be unique within a pipeline.",
                        nameof(stages));
                }
            }

            ordered.Sort((left, right) => left.Order.CompareTo(right.Order));
            this.stages = ordered.ToArray();
            this.validator = validator;
        }

        public InteractionValidation Validate(in TCommand command)
        {
            if (validator == null)
            {
                return InteractionValidation.Valid;
            }

            var validation = validator(command);
            if (validation == null)
            {
                throw new InvalidOperationException(
                    "A stage pipeline validator must not return null.");
            }

            return validation;
        }

        public async ValueTask ExecuteAsync(
            TCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            context.MarkStageDriven();

            // Stages run in ascending order; the first exception or observed cancellation stops
            // every later stage (design §10.1). The pending stage becomes the terminal faulted or
            // cancelled stage.
            for (var index = 0; index < stages.Length; index++)
            {
                var stage = stages[index];

                // Before the first stage, an observed cancellation is "cancelled before the first
                // stage" (design §12): record nothing so the interaction carries no stages and is
                // treated as a no-side-effect cancellation. From the second stage on, defer the
                // check until after BeginStage so a between-stage cancellation is attributed to the
                // stage about to run, which becomes the terminal cancelled stage.
                if (index == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                context.BeginStage(stage.Id, index);

                if (index != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // Application stages run on the caller's context (the Unity main thread under the
                // main-thread policy, design §17.2); do not opt out of it between stages with
                // ConfigureAwait(false).
                await stage.ExecuteAsync(command, context, cancellationToken);
                context.CompleteStage();
            }
        }
    }
}
