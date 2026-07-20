using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class SemanticUiStateProbeTests
{
    [Test]
    public void ArgumentSensitivityChangesTheSnapshotHash()
    {
        // Two states with identical targets and command identities that differ only in the
        // sensitivity of an available interaction's argument must hash differently, so state
        // observation cannot miss a change in what agents may send or record.
        var relaxed = SnapshotHash(sensitive: false);
        var sensitive = SnapshotHash(sensitive: true);

        Assert.That(relaxed, Is.Not.EqualTo(sensitive));
    }

    private static string SnapshotHash(bool sensitive)
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        var registry = new InteractionRegistry(catalog, "session-1");
        registry.Register(new SetValueTarget("field", sensitive), true);
        var probe = new SemanticUiStateProbe(registry);
        return StateCanonicalizer.ComputeHash(probe.Version, probe.Capture());
    }

    private sealed class SetValueTarget : IInteractionTarget
    {
        private readonly bool sensitive;

        public SetValueTarget(string id, bool sensitive)
        {
            Id = id;
            this.sensitive = sensitive;
        }

        public string Id { get; }

        public InteractionDescriptor Describe()
        {
            // The catalog exposes set_value's "value" as non-sensitive; a descriptor may mark
            // it more sensitive (design §13.3), which is the only field varied here.
            var arguments = new InteractionArgumentSchema(
                new[]
                {
                    new InteractionArgumentDefinition(
                        "value",
                        InteractionArgumentType.String,
                        true,
                        sensitive),
                });
            return new InteractionDescriptor(
                Id,
                null,
                "textbox",
                "Label",
                InteractionValue.FromString("v"),
                true,
                true,
                new[] { new AvailableInteraction("set_value", 1, arguments) });
        }

        public bool TryGetPipeline<TCommand>(
            out IInteractionPipeline<TCommand>? resolved)
            where TCommand : struct, IInteractionCommand
        {
            if (typeof(TCommand) == typeof(SetValueCommand))
            {
                resolved = (IInteractionPipeline<TCommand>)(object)NoopPipeline.Instance;
                return true;
            }

            resolved = null;
            return false;
        }
    }

    private sealed class NoopPipeline : IInteractionPipeline<SetValueCommand>
    {
        public static readonly NoopPipeline Instance = new NoopPipeline();

        public InteractionValidation Validate(in SetValueCommand command)
        {
            return InteractionValidation.Valid;
        }

        public ValueTask ExecuteAsync(
            SetValueCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            return default;
        }
    }
}
