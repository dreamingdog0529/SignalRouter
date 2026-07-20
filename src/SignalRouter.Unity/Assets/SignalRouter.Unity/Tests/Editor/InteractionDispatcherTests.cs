using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace SignalRouter.Tests;

public sealed class InteractionDispatcherTests
{
    [Test]
    public async Task DispatcherPublishesThroughVitalRouterAndSucceeds()
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        var registry = new InteractionRegistry(catalog, "unity-session");
        using var dispatcher = new InteractionDispatcher(catalog, registry);
        var pipeline = new ClickPipeline();
        using var registration = registry.Register(new ClickTarget(pipeline), true);

        var result = await dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            new InteractionDispatchOptions(InteractionOrigin.Test));

        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(pipeline.Executed, Is.True);
        Assert.That(result.Stages.Stages.Count, Is.EqualTo(1));
    }

    private sealed class ClickTarget : IInteractionTarget
    {
        private readonly ClickPipeline pipeline;

        public ClickTarget(ClickPipeline pipeline)
        {
            this.pipeline = pipeline;
        }

        public string Id => "menu.start";

        public InteractionDescriptor Describe()
        {
            return new InteractionDescriptor(
                Id,
                null,
                "button",
                "Start",
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
            out IInteractionPipeline<TCommand> resolved)
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

    private sealed class ClickPipeline : IInteractionPipeline<ClickCommand>
    {
        public bool Executed { get; private set; }

        public InteractionValidation Validate(in ClickCommand command)
        {
            return InteractionValidation.Valid;
        }

        public ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            Executed = true;
            return default;
        }
    }
}
