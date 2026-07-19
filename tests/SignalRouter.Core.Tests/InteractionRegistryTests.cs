using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class InteractionRegistryTests
{
    [Test]
    public void RegistrationResolutionNotificationAndLifetimeAdvanceRevision()
    {
        var registry = Registry();
        var target = Target("menu.start", Click());

        var registration = registry.Register(target, true);
        registry.NotifyDescriptorChanged("menu.start");
        var resolved = registry.TryResolve("menu.start", out var actual);
        registration.Dispose();
        registration.Dispose();

        NUnitCompat.Multiple(() =>
        {
            Assert.That(resolved, Is.True);
            Assert.That(actual, Is.SameAs(target));
            Assert.That(registry.Revision, Is.EqualTo(3));
            Assert.That(registry.TryResolve("menu.start", out _), Is.False);
            Assert.That(registration.TargetId, Is.EqualTo("menu.start"));
        });
    }

    [Test]
    public void DuplicateRegistrationPreservesExistingTargetAndRevision()
    {
        var registry = Registry();
        var existing = Target("menu.start", Click());
        var duplicate = Target("menu.start", Click());
        using var registration = registry.Register(existing, true);

        NUnitCompat.ThatThrows(
            () => registry.Register(duplicate, true),
            Throws.TypeOf<InvalidOperationException>());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(registry.Revision, Is.EqualTo(1));
            Assert.That(registry.TryResolve("menu.start", out var resolved), Is.True);
            Assert.That(resolved, Is.SameAs(existing));
            Assert.That(duplicate.DescribeCalls, Is.Zero);
        });
    }

    [Test]
    public void IdsAreOrdinalAndSnapshotsAreSorted()
    {
        var registry = Registry();
        using var lower = registry.Register(Target("alpha", Click()), true);
        using var upper = registry.Register(Target("Alpha", Click()), true);
        using var last = registry.Register(Target("zeta", Click()), true);

        var snapshot = registry.GetSnapshot(InteractionRegistryView.All);

        Assert.That(
            snapshot.Targets.Select(target => target.Id),
            Is.EqualTo(new[] { "Alpha", "alpha", "zeta" }));
    }

    [Test]
    public void RegistrationRejectsDescriptorCatalogSchemaAndPipelineMismatches()
    {
        var registry = Registry();
        var wrongId = Target(
            "menu.start",
            Click(),
            descriptorId: "menu.other");
        var unknownCommand = Target(
            "menu.start",
            new AvailableInteraction(
                "unknown",
                1,
                InteractionArgumentSchema.Empty));
        var wrongSchema = Target(
            "menu.start",
            new AvailableInteraction(
                "click",
                1,
                new InteractionArgumentSchema(
                    new[]
                    {
                        new InteractionArgumentDefinition(
                            "value",
                            InteractionArgumentType.String,
                            true,
                            false),
                    })));
        var missingPipeline = Target(
            "menu.start",
            Click(),
            supportsClick: false);

        NUnitCompat.Multiple(() =>
        {
            NUnitCompat.ThatThrows(
                () => registry.Register(wrongId, true),
                Throws.TypeOf<InvalidOperationException>());
            NUnitCompat.ThatThrows(
                () => registry.Register(unknownCommand, true),
                Throws.TypeOf<InvalidOperationException>());
            NUnitCompat.ThatThrows(
                () => registry.Register(wrongSchema, true),
                Throws.TypeOf<InvalidOperationException>());
            NUnitCompat.ThatThrows(
                () => registry.Register(missingPipeline, true),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(registry.Revision, Is.Zero);
        });
    }

    [Test]
    public void AgentSnapshotRequiresVisibleTargetAndVisibleCatalogCommand()
    {
        var catalog = new InteractionCommandCatalogBuilder()
            .Register("click", 1, ClickCommandSchema.Instance, true)
            .Register("set_value", 1, SetValueCommandSchema.Instance, false)
            .Build();
        var registry = new InteractionRegistry(catalog, "session-1");
        var visible = Target(
            "profile.name",
            Click(),
            SetValue(),
            supportsSetValue: true);
        var hidden = Target("menu.secret", Click());
        using var visibleRegistration = registry.Register(visible, true);
        using var hiddenRegistration = registry.Register(hidden, false);

        var all = registry.GetSnapshot(InteractionRegistryView.All);
        var agent = registry.GetSnapshot(InteractionRegistryView.Agent);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(all.Targets, Has.Count.EqualTo(2));
            Assert.That(agent.Targets, Has.Count.EqualTo(1));
            Assert.That(agent.Targets[0].Id, Is.EqualTo("profile.name"));
            Assert.That(
                agent.Targets[0].AvailableInteractions.Select(value => value.WireName),
                Is.EqualTo(new[] { "click" }));
        });
    }

    [Test]
    public void DescriptorNotificationValidatesBeforeAdvancingRevision()
    {
        var registry = Registry();
        var target = Target("menu.start", Click());
        using var registration = registry.Register(target, true);
        target.Descriptor = Descriptor("different", Click());

        NUnitCompat.ThatThrows(
            () => registry.NotifyDescriptorChanged("menu.start"),
            Throws.TypeOf<InvalidOperationException>());

        Assert.That(registry.Revision, Is.EqualTo(1));
    }

    [Test]
    public void DescriptorCollectionsAreDefensivelyCopied()
    {
        var interactions = new[] { Click() };
        var descriptor = Descriptor("menu.start", interactions);
        interactions[0] = SetValue();

        Assert.That(
            descriptor.AvailableInteractions[0].WireName,
            Is.EqualTo("click"));
    }

    [Test]
    public void NewSessionFactoryCreatesStableDistinctEpochs()
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        var first = InteractionRegistry.CreateNewSession(catalog);
        var second = InteractionRegistry.CreateNewSession(catalog);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(first.SessionEpoch, Is.Not.Empty);
            Assert.That(second.SessionEpoch, Is.Not.EqualTo(first.SessionEpoch));
            Assert.That(first.GetSnapshot(InteractionRegistryView.All).SessionEpoch,
                Is.EqualTo(first.SessionEpoch));
        });
    }

    [Test]
    public void TargetSpecificSchemaMayUpgradeSensitiveMetadata()
    {
        var sensitiveSetValue = new AvailableInteraction(
            "set_value",
            1,
            new InteractionArgumentSchema(
                new[]
                {
                    new InteractionArgumentDefinition(
                        "value",
                        InteractionArgumentType.String,
                        true,
                        true),
                }));
        var registry = Registry();

        using var registration = registry.Register(
            Target(
                "password",
                sensitiveSetValue,
                supportsClick: false,
                supportsSetValue: true),
            true);

        Assert.That(registry.Revision, Is.EqualTo(1));
    }

    private static InteractionRegistry Registry()
    {
        return new InteractionRegistry(
            InteractionCommandCatalog.CreateMvp(),
            "session-1");
    }

    private static AvailableInteraction Click()
    {
        return new AvailableInteraction(
            "click",
            1,
            ClickCommandSchema.Instance.Arguments);
    }

    private static AvailableInteraction SetValue()
    {
        return new AvailableInteraction(
            "set_value",
            1,
            SetValueCommandSchema.Instance.Arguments);
    }

    private static TestTarget Target(
        string id,
        AvailableInteraction interaction,
        bool supportsClick = true,
        bool supportsSetValue = false,
        string? descriptorId = null)
    {
        return Target(
            id,
            new[] { interaction },
            supportsClick,
            supportsSetValue,
            descriptorId);
    }

    private static TestTarget Target(
        string id,
        AvailableInteraction first,
        AvailableInteraction second,
        bool supportsClick = true,
        bool supportsSetValue = false,
        string? descriptorId = null)
    {
        return Target(
            id,
            new[] { first, second },
            supportsClick,
            supportsSetValue,
            descriptorId);
    }

    private static TestTarget Target(
        string id,
        IEnumerable<AvailableInteraction> interactions,
        bool supportsClick = true,
        bool supportsSetValue = false,
        string? descriptorId = null)
    {
        return new TestTarget(
            id,
            Descriptor(descriptorId ?? id, interactions),
            supportsClick,
            supportsSetValue);
    }

    private static InteractionDescriptor Descriptor(
        string id,
        params AvailableInteraction[] interactions)
    {
        return Descriptor(id, (IEnumerable<AvailableInteraction>)interactions);
    }

    private static InteractionDescriptor Descriptor(
        string id,
        IEnumerable<AvailableInteraction> interactions)
    {
        return new InteractionDescriptor(
            id,
            null,
            "button",
            "Start",
            null,
            true,
            true,
            interactions);
    }

    private sealed class TestTarget : IInteractionTarget
    {
        private readonly bool supportsClick;
        private readonly bool supportsSetValue;

        public TestTarget(
            string id,
            InteractionDescriptor descriptor,
            bool supportsClick,
            bool supportsSetValue)
        {
            Id = id;
            Descriptor = descriptor;
            this.supportsClick = supportsClick;
            this.supportsSetValue = supportsSetValue;
        }

        public string Id { get; }

        public InteractionDescriptor Descriptor { get; set; }

        public int DescribeCalls { get; private set; }

        public InteractionDescriptor Describe()
        {
            DescribeCalls++;
            return Descriptor;
        }

        public bool TryGetPipeline<TCommand>(
            out IInteractionPipeline<TCommand>? pipeline)
            where TCommand : struct, IInteractionCommand
        {
            if (supportsClick && typeof(TCommand) == typeof(ClickCommand))
            {
                pipeline = (IInteractionPipeline<TCommand>)(object)new TestPipeline<ClickCommand>();
                return true;
            }

            if (supportsSetValue && typeof(TCommand) == typeof(SetValueCommand))
            {
                pipeline =
                    (IInteractionPipeline<TCommand>)(object)new TestPipeline<SetValueCommand>();
                return true;
            }

            pipeline = null;
            return false;
        }
    }

    private sealed class TestPipeline<TCommand> : IInteractionPipeline<TCommand>
        where TCommand : struct, IInteractionCommand
    {
        public InteractionValidation Validate(in TCommand command)
        {
            return InteractionValidation.Valid;
        }

        public ValueTask ExecuteAsync(
            TCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}
