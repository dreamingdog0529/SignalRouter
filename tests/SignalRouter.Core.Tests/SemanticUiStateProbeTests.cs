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

    [Test]
    public void ScalarFieldChangesOnAMatchedTargetAreEnumerated()
    {
        var changes = Diff(
            initialValue: InteractionValue.FromString("v1"),
            mutate: target =>
            {
                target.Label = "after";
                target.Enabled = false;
                target.Value = InteractionValue.FromString("v2");
            });

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                changes.Select(change => change.Path),
                Is.EquivalentTo(new[]
                {
                    "targets[menu.start].label",
                    "targets[menu.start].enabled",
                    "targets[menu.start].value",
                }));
            var label = Single(changes, "targets[menu.start].label");
            Assert.That(label.Before, Is.EqualTo(InteractionValue.FromString("before")));
            Assert.That(label.After, Is.EqualTo(InteractionValue.FromString("after")));
            var enabled = Single(changes, "targets[menu.start].enabled");
            Assert.That(enabled.Before, Is.EqualTo(InteractionValue.FromBoolean(true)));
            Assert.That(enabled.After, Is.EqualTo(InteractionValue.FromBoolean(false)));
            var value = Single(changes, "targets[menu.start].value");
            Assert.That(value.Before, Is.EqualTo(InteractionValue.FromString("v1")));
            Assert.That(value.After, Is.EqualTo(InteractionValue.FromString("v2")));
        });
    }

    [Test]
    public void ReparentingIsEnumeratedAsAParentIdChange()
    {
        var changes = Diff(mutate: target => target.ParentId = "menu");

        var change = Single(changes, "targets[menu.start].parentId");
        NUnitCompat.Multiple(() =>
        {
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(change.Before, Is.EqualTo(InteractionValue.Null));
            Assert.That(change.After, Is.EqualTo(InteractionValue.FromString("menu")));
        });
    }

    [Test]
    public void ANumericValueChangeIsEnumerated()
    {
        var changes = Diff(
            initialValue: InteractionValue.FromNumber(1m),
            mutate: target => target.Value = InteractionValue.FromNumber(2m));

        var change = Single(changes, "targets[menu.start].value");
        NUnitCompat.Multiple(() =>
        {
            Assert.That(change.Before, Is.EqualTo(InteractionValue.FromNumber(1m)));
            Assert.That(change.After, Is.EqualTo(InteractionValue.FromNumber(2m)));
        });
    }

    [Test]
    public void ANumericValueThatNormalizesEquallyYieldsNoChange()
    {
        // 1.0 and 1.00 canonicalize to the same normalized string, so neither the hash nor
        // the property diff sees a change.
        var changes = Diff(
            initialValue: InteractionValue.FromNumber(1.0m),
            mutate: target => target.Value = InteractionValue.FromNumber(1.00m));

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void AnAddedTargetIsEnumeratedAsPerFieldAddedChanges()
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        var registry = new InteractionRegistry(catalog, "session-1");
        registry.Register(new FakeTarget("menu.start"), true);
        var probe = new SemanticUiStateProbe(registry);

        var before = probe.Capture();
        registry.Register(new FakeTarget("menu.options") { Role = "button", Label = "Options" }, true);
        var after = probe.Capture();
        var changes = probe.DiffProperties(before, after);

        // Every scalar field of the added target is enumerated as an Added change (before absent),
        // so presence is expressed per field (ADR 0003). The pre-existing target is unchanged.
        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                changes.Select(change => change.Path),
                Is.EquivalentTo(new[]
                {
                    "targets[menu.options].role",
                    "targets[menu.options].label",
                    "targets[menu.options].parentId",
                    "targets[menu.options].visible",
                    "targets[menu.options].enabled",
                    "targets[menu.options].value",
                }));
            Assert.That(
                changes.Select(change => change.Kind),
                Has.All.EqualTo(StatePropertyChangeKind.Added));
            Assert.That(changes.Select(change => change.Before), Has.All.Null);
            var role = Single(changes, "targets[menu.options].role");
            Assert.That(role.After, Is.EqualTo(InteractionValue.FromString("button")));
            var parentId = Single(changes, "targets[menu.options].parentId");
            Assert.That(parentId.After, Is.EqualTo(InteractionValue.Null));
        });
    }

    [Test]
    public void ARemovedTargetIsEnumeratedAsPerFieldRemovedChanges()
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        var registry = new InteractionRegistry(catalog, "session-1");
        registry.Register(new FakeTarget("menu.start"), true);
        var options = registry.Register(new FakeTarget("menu.options") { Label = "Options" }, true);
        var probe = new SemanticUiStateProbe(registry);

        var before = probe.Capture();
        options.Dispose();
        var after = probe.Capture();
        var changes = probe.DiffProperties(before, after);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                changes.Select(change => change.Path),
                Is.EquivalentTo(new[]
                {
                    "targets[menu.options].role",
                    "targets[menu.options].label",
                    "targets[menu.options].parentId",
                    "targets[menu.options].visible",
                    "targets[menu.options].enabled",
                    "targets[menu.options].value",
                }));
            Assert.That(
                changes.Select(change => change.Kind),
                Has.All.EqualTo(StatePropertyChangeKind.Removed));
            Assert.That(changes.Select(change => change.After), Has.All.Null);
            var label = Single(changes, "targets[menu.options].label");
            Assert.That(label.Before, Is.EqualTo(InteractionValue.FromString("Options")));
        });
    }

    [Test]
    public void AnAvailableInteractionChangeIsNotEnumerated()
    {
        var target = new FakeTarget("menu.start");
        var catalog = InteractionCommandCatalog.CreateMvp();
        var registry = new InteractionRegistry(catalog, "session-1");
        registry.Register(target, true);
        var probe = new SemanticUiStateProbe(registry);

        var before = probe.Capture();
        // Marking the argument more sensitive changes availableInteractions (and the hash) but
        // touches no scalar descriptor field, so no property change is enumerated (nested
        // interaction changes are deferred, ADR 0002).
        target.ArgumentSensitive = true;
        var after = probe.Capture();

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                StateCanonicalizer.ComputeHash(probe.Version, before),
                Is.Not.EqualTo(StateCanonicalizer.ComputeHash(probe.Version, after)));
            Assert.That(probe.DiffProperties(before, after), Is.Empty);
        });
    }

    private static IReadOnlyList<StatePropertyChange> Diff(
        Action<FakeTarget> mutate,
        InteractionValue? initialValue = null)
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        var registry = new InteractionRegistry(catalog, "session-1");
        var target = new FakeTarget("menu.start") { Value = initialValue };
        registry.Register(target, true);
        var probe = new SemanticUiStateProbe(registry);

        var before = probe.Capture();
        mutate(target);
        var after = probe.Capture();

        return probe.DiffProperties(before, after);
    }

    private static StatePropertyChange Single(
        IReadOnlyList<StatePropertyChange> changes,
        string path)
    {
        return changes.Single(change => change.Path == path);
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

    // A target whose observable descriptor fields are all mutable, so a test can capture, flip
    // one field (without notifying the registry — GetSnapshot re-reads Describe live), and
    // capture again to exercise the property diff over a single field delta.
    private sealed class FakeTarget : IInteractionTarget
    {
        public FakeTarget(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public string? ParentId { get; set; }

        public string Role { get; set; } = "textbox";

        public string Label { get; set; } = "before";

        public bool Visible { get; set; } = true;

        public bool Enabled { get; set; } = true;

        public InteractionValue? Value { get; set; }

        // A descriptor may mark a catalog field more sensitive (design §13.3); toggling this
        // changes availableInteractions without touching any scalar descriptor field.
        public bool ArgumentSensitive { get; set; }

        public InteractionDescriptor Describe()
        {
            var arguments = new InteractionArgumentSchema(
                new[]
                {
                    new InteractionArgumentDefinition(
                        "value",
                        InteractionArgumentType.String,
                        true,
                        ArgumentSensitive),
                });
            return new InteractionDescriptor(
                Id,
                ParentId,
                Role,
                Label,
                Value,
                Visible,
                Enabled,
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
}
