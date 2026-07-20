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

            // Stages run in ascending order; the first exception or observed cancellation stops
            // every later stage (design §10.1). The pending stage becomes the terminal faulted or
            // cancelled stage. Cancellation is checked after BeginStage so a between-stage
            // cancellation is attributed to the stage about to run rather than left unassigned.
            for (var index = 0; index < stages.Length; index++)
            {
                var stage = stages[index];
                context.BeginStage(stage.Id, index);
                cancellationToken.ThrowIfCancellationRequested();
                await stage.ExecuteAsync(command, context, cancellationToken).ConfigureAwait(false);
                context.CompleteStage();
            }
        }
    }
}
