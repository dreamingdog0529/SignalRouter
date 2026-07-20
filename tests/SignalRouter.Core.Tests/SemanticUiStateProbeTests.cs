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
        // so presence is expressed per field (ADR 0003). The target's availableInteractions are
        // likewise enumerated per field (ADR 0004): its set_value@1 interaction contributes its
        // key fields and argument fields. The pre-existing target is unchanged.
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
                    "targets[menu.options].availableInteractions[set_value@1].wireName",
                    "targets[menu.options].availableInteractions[set_value@1].version",
                    "targets[menu.options].availableInteractions[set_value@1].arguments[value].type",
                    "targets[menu.options].availableInteractions[set_value@1].arguments[value].required",
                    "targets[menu.options].availableInteractions[set_value@1].arguments[value].sensitive",
                }));
            Assert.That(
                changes.Select(change => change.Kind),
                Has.All.EqualTo(StatePropertyChangeKind.Added));
            Assert.That(changes.Select(change => change.Before), Has.All.Null);
            var role = Single(changes, "targets[menu.options].role");
            Assert.That(role.After, Is.EqualTo(InteractionValue.FromString("button")));
            var parentId = Single(changes, "targets[menu.options].parentId");
            Assert.That(parentId.After, Is.EqualTo(InteractionValue.Null));
            var wireName = Single(
                changes,
                "targets[menu.options].availableInteractions[set_value@1].wireName");
            Assert.That(wireName.After, Is.EqualTo(InteractionValue.FromString("set_value")));
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
                    "targets[menu.options].availableInteractions[set_value@1].wireName",
                    "targets[menu.options].availableInteractions[set_value@1].version",
                    "targets[menu.options].availableInteractions[set_value@1].arguments[value].type",
                    "targets[menu.options].availableInteractions[set_value@1].arguments[value].required",
                    "targets[menu.options].availableInteractions[set_value@1].arguments[value].sensitive",
                }));
            Assert.That(
                changes.Select(change => change.Kind),
                Has.All.EqualTo(StatePropertyChangeKind.Removed));
            Assert.That(changes.Select(change => change.After), Has.All.Null);
            var label = Single(changes, "targets[menu.options].label");
            Assert.That(label.Before, Is.EqualTo(InteractionValue.FromString("Options")));
            var version = Single(
                changes,
                "targets[menu.options].availableInteractions[set_value@1].version");
            Assert.That(version.Before, Is.EqualTo(InteractionValue.FromNumber(1m)));
        });
    }

    [Test]
    public void AnArgumentSensitivityChangeIsEnumerated()
    {
        var target = new FakeTarget("menu.start");
        var catalog = InteractionCommandCatalog.CreateMvp();
        var registry = new InteractionRegistry(catalog, "session-1");
        registry.Register(target, true);
        var probe = new SemanticUiStateProbe(registry);

        var before = probe.Capture();
        // Marking the argument more sensitive changes availableInteractions (and the hash) but
        // touches no scalar descriptor field. It is now enumerated as a nested argument-field
        // change (ADR 0004): the sole reachable-through-a-single-catalog nested change.
        target.ArgumentSensitive = true;
        var after = probe.Capture();

        var change = Single(
            probe.DiffProperties(before, after),
            "targets[menu.start].availableInteractions[set_value@1].arguments[value].sensitive");
        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                StateCanonicalizer.ComputeHash(probe.Version, before),
                Is.Not.EqualTo(StateCanonicalizer.ComputeHash(probe.Version, after)));
            Assert.That(probe.DiffProperties(before, after), Has.Count.EqualTo(1));
            Assert.That(change.Kind, Is.EqualTo(StatePropertyChangeKind.Modified));
            Assert.That(change.Before, Is.EqualTo(InteractionValue.FromBoolean(false)));
            Assert.That(change.After, Is.EqualTo(InteractionValue.FromBoolean(true)));
        });
    }

    [Test]
    public void AnAddedInteractionIsEnumeratedAsPerFieldAddedChanges()
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        var registry = new InteractionRegistry(catalog, "session-1");
        var target = new TogglingTarget("menu.start");
        registry.Register(target, true);
        var probe = new SemanticUiStateProbe(registry);

        var before = probe.Capture();
        // click@1 has an empty argument schema, so its presence is visible only through the
        // interaction key fields (wireName, version) — the reason those are emitted (ADR 0004).
        target.ExposeClick = true;
        var after = probe.Capture();
        var changes = probe.DiffProperties(before, after);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                changes.Select(change => change.Path),
                Is.EquivalentTo(new[]
                {
                    "targets[menu.start].availableInteractions[click@1].wireName",
                    "targets[menu.start].availableInteractions[click@1].version",
                }));
            Assert.That(
                changes.Select(change => change.Kind),
                Has.All.EqualTo(StatePropertyChangeKind.Added));
            Assert.That(changes.Select(change => change.Before), Has.All.Null);
            var wireName = Single(
                changes,
                "targets[menu.start].availableInteractions[click@1].wireName");
            Assert.That(wireName.After, Is.EqualTo(InteractionValue.FromString("click")));
            var version = Single(
                changes,
                "targets[menu.start].availableInteractions[click@1].version");
            Assert.That(version.After, Is.EqualTo(InteractionValue.FromNumber(1m)));
        });
    }

    [Test]
    public void ARemovedInteractionIsEnumeratedAsPerFieldRemovedChanges()
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        var registry = new InteractionRegistry(catalog, "session-1");
        var target = new TogglingTarget("menu.start") { ExposeClick = true };
        registry.Register(target, true);
        var probe = new SemanticUiStateProbe(registry);

        var before = probe.Capture();
        target.ExposeClick = false;
        var after = probe.Capture();
        var changes = probe.DiffProperties(before, after);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                changes.Select(change => change.Path),
                Is.EquivalentTo(new[]
                {
                    "targets[menu.start].availableInteractions[click@1].wireName",
                    "targets[menu.start].availableInteractions[click@1].version",
                }));
            Assert.That(
                changes.Select(change => change.Kind),
                Has.All.EqualTo(StatePropertyChangeKind.Removed));
            Assert.That(changes.Select(change => change.After), Has.All.Null);
        });
    }

    [Test]
    public void AReorderedArgumentIsEnumeratedAsOrdinalChanges()
    {
        // Reordering same-membership arguments is not reachable through a single-catalog
        // registry (a descriptor schema must be order-compatible with the catalog), so this
        // exercises DiffProperties directly on hand-crafted canonical snapshots.
        var before = Snapshot(Interaction("do", Argument("a", 0), Argument("b", 0)));
        var after = Snapshot(Interaction("do", Argument("b", 0), Argument("a", 0)));

        var changes = DiffSnapshots(before, after);

        var argA = Single(changes, "targets[menu.start].availableInteractions[do@1].arguments[a].ordinal");
        var argB = Single(changes, "targets[menu.start].availableInteractions[do@1].arguments[b].ordinal");
        NUnitCompat.Multiple(() =>
        {
            Assert.That(changes, Has.Count.EqualTo(2));
            Assert.That(changes.Select(change => change.Kind), Has.All.EqualTo(StatePropertyChangeKind.Modified));
            Assert.That(argA.Before, Is.EqualTo(InteractionValue.FromNumber(0m)));
            Assert.That(argA.After, Is.EqualTo(InteractionValue.FromNumber(1m)));
            Assert.That(argB.Before, Is.EqualTo(InteractionValue.FromNumber(1m)));
            Assert.That(argB.After, Is.EqualTo(InteractionValue.FromNumber(0m)));
        });
    }

    [Test]
    public void AnArgumentTypeChangeIsEnumerated()
    {
        // Enum type is carried as its underlying int: String(0) -> Number(2).
        var before = Snapshot(Interaction("do", Argument("value", 0)));
        var after = Snapshot(Interaction("do", Argument("value", 2)));

        var changes = DiffSnapshots(before, after);

        var change = Single(changes, "targets[menu.start].availableInteractions[do@1].arguments[value].type");
        NUnitCompat.Multiple(() =>
        {
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(change.Kind, Is.EqualTo(StatePropertyChangeKind.Modified));
            Assert.That(change.Before, Is.EqualTo(InteractionValue.FromNumber(0m)));
            Assert.That(change.After, Is.EqualTo(InteractionValue.FromNumber(2m)));
        });
    }

    [Test]
    public void AnArgumentRequiredChangeIsEnumerated()
    {
        var before = Snapshot(Interaction("do", Argument("value", 0, required: true)));
        var after = Snapshot(Interaction("do", Argument("value", 0, required: false)));

        var change = Single(
            DiffSnapshots(before, after),
            "targets[menu.start].availableInteractions[do@1].arguments[value].required");
        NUnitCompat.Multiple(() =>
        {
            Assert.That(change.Before, Is.EqualTo(InteractionValue.FromBoolean(true)));
            Assert.That(change.After, Is.EqualTo(InteractionValue.FromBoolean(false)));
        });
    }

    [Test]
    public void AnAddedArgumentIsEnumeratedPerFieldWithoutOrdinalNoise()
    {
        // Membership changed (b is added), so no ordinal changes are emitted for any argument in
        // this interaction — only the added argument's own fields (ADR 0004, Option C).
        var before = Snapshot(Interaction("do", Argument("a", 0)));
        var after = Snapshot(Interaction("do", Argument("a", 0), Argument("b", 1)));

        var changes = DiffSnapshots(before, after);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                changes.Select(change => change.Path),
                Is.EquivalentTo(new[]
                {
                    "targets[menu.start].availableInteractions[do@1].arguments[b].type",
                    "targets[menu.start].availableInteractions[do@1].arguments[b].required",
                    "targets[menu.start].availableInteractions[do@1].arguments[b].sensitive",
                }));
            Assert.That(
                changes.Select(change => change.Kind),
                Has.All.EqualTo(StatePropertyChangeKind.Added));
        });
    }

    [Test]
    public void ADifferentInteractionVersionIsTreatedAsRemoveAndAdd()
    {
        // (wireName, version) is the match key, so do@1 and do@2 never match: do@1 is removed
        // and do@2 is added, rather than a modification.
        var before = Snapshot(Interaction("do", 1, Argument("value", 0)));
        var after = Snapshot(Interaction("do", 2, Argument("value", 0)));

        var changes = DiffSnapshots(before, after);

        NUnitCompat.Multiple(() =>
        {
            var removed = changes
                .Where(change => change.Kind == StatePropertyChangeKind.Removed)
                .Select(change => change.Path);
            var added = changes
                .Where(change => change.Kind == StatePropertyChangeKind.Added)
                .Select(change => change.Path);
            Assert.That(
                removed,
                Has.Member("targets[menu.start].availableInteractions[do@1].wireName"));
            Assert.That(
                added,
                Has.Member("targets[menu.start].availableInteractions[do@2].wireName"));
            Assert.That(
                changes.Where(change => change.Kind == StatePropertyChangeKind.Modified),
                Is.Empty);
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

    // Diffs two hand-crafted snapshots directly through the probe. The registry is only a
    // constructor dependency here — the snapshots are supplied, not captured — so cases that a
    // single-catalog registry cannot produce (argument add/remove, type/required/reorder) can
    // still be exercised (ADR 0004).
    private static IReadOnlyList<StatePropertyChange> DiffSnapshots(
        StateProbeSnapshot before,
        StateProbeSnapshot after)
    {
        return new SemanticUiStateProbe(
            new InteractionRegistry(InteractionCommandCatalog.CreateMvp(), "session-1"))
            .DiffProperties(before, after);
    }

    // Builds a canonical semantic-ui snapshot for a single target "menu.start" whose scalar
    // fields are fixed (so only interaction/argument differences surface) exposing the given
    // availableInteractions.
    private static StateProbeSnapshot Snapshot(params string[] interactions)
    {
        var json = "{\"sessionEpoch\":\"session-1\",\"revision\":1,\"targets\":[{"
            + "\"id\":\"menu.start\",\"parentId\":null,\"role\":\"textbox\",\"label\":\"Label\","
            + "\"visible\":true,\"enabled\":true,\"value\":null,"
            + "\"availableInteractions\":[" + string.Join(",", interactions) + "]}]}";
        return StateProbeSnapshot.FromJson(json);
    }

    private static string Interaction(string wireName, params string[] arguments)
    {
        return Interaction(wireName, 1, arguments);
    }

    private static string Interaction(string wireName, int version, params string[] arguments)
    {
        return "{\"wireName\":\"" + wireName + "\",\"version\":" + version
            + ",\"arguments\":[" + string.Join(",", arguments) + "]}";
    }

    private static string Argument(
        string name,
        int type,
        bool required = false,
        bool sensitive = false)
    {
        return "{\"name\":\"" + name + "\",\"type\":" + type
            + ",\"required\":" + (required ? "true" : "false")
            + ",\"sensitive\":" + (sensitive ? "true" : "false") + "}";
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

    // A target that always exposes set_value@1 and can additionally expose click@1 (an empty-
    // argument interaction) on demand, so a test can add or remove a whole interaction between
    // two captures without touching any other field.
    private sealed class TogglingTarget : IInteractionTarget
    {
        public TogglingTarget(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public bool ExposeClick { get; set; }

        public InteractionDescriptor Describe()
        {
            var interactions = new List<AvailableInteraction>
            {
                new AvailableInteraction(
                    "set_value",
                    1,
                    new InteractionArgumentSchema(
                        new[]
                        {
                            new InteractionArgumentDefinition(
                                "value",
                                InteractionArgumentType.String,
                                true,
                                false),
                        })),
            };
            if (ExposeClick)
            {
                interactions.Add(
                    new AvailableInteraction("click", 1, InteractionArgumentSchema.Empty));
            }

            return new InteractionDescriptor(
                Id,
                null,
                "textbox",
                "Label",
                InteractionValue.FromString("v"),
                true,
                true,
                interactions);
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

            if (typeof(TCommand) == typeof(ClickCommand))
            {
                resolved = (IInteractionPipeline<TCommand>)(object)ClickPipeline.Instance;
                return true;
            }

            resolved = null;
            return false;
        }
    }

    private sealed class ClickPipeline : IInteractionPipeline<ClickCommand>
    {
        public static readonly ClickPipeline Instance = new ClickPipeline();

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
