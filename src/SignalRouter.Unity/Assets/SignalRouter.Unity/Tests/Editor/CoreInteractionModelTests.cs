using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VitalRouter;

namespace SignalRouter.Tests;

public sealed class CoreInteractionModelTests
{
    [Test]
    public void MvpCoreRunsWithUnityDependencyReferences()
    {
        Assert.That(typeof(ICommand).IsAssignableFrom(typeof(ClickCommand)), Is.True);

        var catalog = InteractionCommandCatalog.CreateMvp();
        using var arguments = JsonDocument.Parse("{\"value\":\"Wanwan\"}");
        var command = catalog.Decode(
            "set_value",
            1,
            "profile.name",
            arguments.RootElement);
        Assert.That(
            command.GetCommand<SetValueCommand>(),
            Is.EqualTo(new SetValueCommand("profile.name", "Wanwan")));

        var result = new InteractionResult(
            1,
            "request-1",
            "menu.start",
            "click",
            1,
            InteractionOrigin.Test,
            InteractionStatus.Rejected,
            new RejectionInfo(
                InteractionRejectionCode.Disabled,
                "Disabled for Unity test."),
            null,
            StageProgress.Empty,
            StateObservation.Empty,
            StateObservation.Empty,
            StateDiff.Empty);
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Rejected));

        var registry = new InteractionRegistry(catalog, "unity-session");
        using var registration = registry.Register(new ClickTarget(), true);
        var snapshot = registry.GetSnapshot(InteractionRegistryView.Agent);
        Assert.That(snapshot.Targets.Count, Is.EqualTo(1));
        Assert.That(snapshot.Targets[0].Id, Is.EqualTo("menu.start"));
    }

    private sealed class ClickTarget : IInteractionTarget
    {
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
            out IInteractionPipeline<TCommand> pipeline)
            where TCommand : struct, IInteractionCommand
        {
            if (typeof(TCommand) == typeof(ClickCommand))
            {
                pipeline =
                    (IInteractionPipeline<TCommand>)(object)new ClickPipeline();
                return true;
            }

            pipeline = null;
            return false;
        }
    }

    private sealed class ClickPipeline : IInteractionPipeline<ClickCommand>
    {
        public InteractionValidation Validate(in ClickCommand command)
        {
            return InteractionValidation.Valid;
        }

        public ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            return default;
        }
    }
}
