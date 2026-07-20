using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class StateObservationDispatcherTests
{
    [Test]
    public async Task StateMutatingInteractionYieldsDifferingBeforeAndAfterHashesAndAMatchingDiff()
    {
        using var harness = new ProbeHarness(withProbes: true);

        // Mutating the label (without notifying the registry) changes only the semantic-ui
        // snapshot; the interaction-runtime revision is untouched, so its hash is stable.
        var target = harness.Register("menu.start", t => t.Label = "after");

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());

        var semanticBefore = Hash(result.Before, SemanticUiStateProbe.ProbeId);
        var semanticAfter = Hash(result.After, SemanticUiStateProbe.ProbeId);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(
                result.Before.Probes.Select(probe => probe.ProbeId).ToArray(),
                Is.EqualTo(new[] { InteractionRuntimeStateProbe.ProbeId, SemanticUiStateProbe.ProbeId }));
            Assert.That(semanticBefore, Is.Not.EqualTo(semanticAfter));
            Assert.That(
                Hash(result.Before, InteractionRuntimeStateProbe.ProbeId),
                Is.EqualTo(Hash(result.After, InteractionRuntimeStateProbe.ProbeId)));
            Assert.That(result.Diff.Probes, Has.Count.EqualTo(1));
            Assert.That(result.Diff.Probes[0].ProbeId, Is.EqualTo(SemanticUiStateProbe.ProbeId));
            Assert.That(result.Diff.Probes[0].BeforeHash, Is.EqualTo(semanticBefore));
            Assert.That(result.Diff.Probes[0].AfterHash, Is.EqualTo(semanticAfter));
            // The semantic-ui probe now explains the hash change as a property-level change.
            Assert.That(result.Diff.Probes[0].Changes, Has.Count.EqualTo(1));
            Assert.That(
                result.Diff.Probes[0].Changes[0].Path,
                Is.EqualTo("targets[menu.start].label"));
            Assert.That(
                result.Diff.Probes[0].Changes[0].Before,
                Is.EqualTo(InteractionValue.FromString("before")));
            Assert.That(
                result.Diff.Probes[0].Changes[0].After,
                Is.EqualTo(InteractionValue.FromString("after")));
        });
    }

    [Test]
    public async Task NonMutatingInteractionYieldsEqualObservationsAndAnEmptyDiff()
    {
        using var harness = new ProbeHarness(withProbes: true);
        harness.Register("menu.start");

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(result.Before, Is.EqualTo(result.After));
            Assert.That(result.Before.Probes, Has.Count.EqualTo(2));
            Assert.That(result.Diff.Probes, Is.Empty);
        });
    }

    [Test]
    public async Task FaultedInteractionCapturesBeforeAndAfterObservations()
    {
        using var harness = new ProbeHarness(withProbes: true);
        var boom = new InvalidOperationException("stage failed");
        harness.Register("menu.start", t =>
        {
            t.Label = "after";
            throw boom;
        });

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Faulted));
            Assert.That(result.Before.Probes, Has.Count.EqualTo(2));
            Assert.That(result.After.Probes, Has.Count.EqualTo(2));
            Assert.That(result.Diff.Probes, Has.Count.EqualTo(1));
            Assert.That(result.Diff.Probes[0].ProbeId, Is.EqualTo(SemanticUiStateProbe.ProbeId));
        });
    }

    [Test]
    public async Task RejectedInteractionYieldsEmptyObservations()
    {
        using var harness = new ProbeHarness(withProbes: true);

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("missing"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Rejected));
            Assert.That(result.Before.Probes, Is.Empty);
            Assert.That(result.After.Probes, Is.Empty);
            Assert.That(result.Diff.Probes, Is.Empty);
        });
    }

    [Test]
    public async Task ProbeInvariantViolationDuringCaptureFailsFastInsteadOfFaulting()
    {
        using var harness = new ProbeHarness(
            withProbes: true,
            extraProbe: new NullSnapshotProbe());
        harness.Register("menu.start");

        // A probe that yields no snapshot is dispatcher infrastructure failing, not an
        // application-stage fault: it must escape rather than normalize into a Faulted result
        // that the idempotency cache would retain (ADR 0001).
        InteractionInvariantViolationException? caught = null;
        try
        {
            await harness.Dispatcher.DispatchAsync(new ClickCommand("menu.start"), Options());
        }
        catch (InteractionInvariantViolationException exception)
        {
            caught = exception;
        }

        Assert.That(caught, Is.Not.Null);
    }

    [Test]
    public async Task DispatcherWithoutProbeRegistryKeepsEmptyObservations()
    {
        using var harness = new ProbeHarness(withProbes: false);
        harness.Register("menu.start");

        var result = await harness.Dispatcher.DispatchAsync(
            new ClickCommand("menu.start"),
            Options());

        NUnitCompat.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Succeeded));
            Assert.That(result.Before.Probes, Is.Empty);
            Assert.That(result.After.Probes, Is.Empty);
            Assert.That(result.Diff.Probes, Is.Empty);
        });
    }

    private static string Hash(StateObservation observation, string probeId)
    {
        return observation.Probes.Single(probe => probe.ProbeId == probeId).Hash;
    }

    private static InteractionDispatchOptions Options(
        InteractionOrigin origin = InteractionOrigin.Test)
    {
        return new InteractionDispatchOptions(origin);
    }

    private sealed class ProbeHarness : IDisposable
    {
        private readonly InteractionRegistry registry;
        private readonly List<IInteractionTargetRegistration> registrations = new();

        public ProbeHarness(bool withProbes, IInteractionStateProbe? extraProbe = null)
        {
            var catalog = InteractionCommandCatalog.CreateMvp();
            registry = new InteractionRegistry(catalog, "session-1");
            InteractionStateProbeRegistry? probes = null;
            if (withProbes)
            {
                probes = new InteractionStateProbeRegistry();
                probes.Register(new SemanticUiStateProbe(registry));
                probes.Register(new InteractionRuntimeStateProbe(registry));
                if (extraProbe != null)
                {
                    probes.Register(extraProbe);
                }
            }

            Dispatcher = new InteractionDispatcher(catalog, registry, probes);
        }

        public InteractionDispatcher Dispatcher { get; }

        public MutableTarget Register(string targetId, Action<MutableTarget>? stage = null)
        {
            var target = new MutableTarget(targetId, stage);
            registrations.Add(registry.Register(target, true));
            return target;
        }

        public void Dispose()
        {
            Dispatcher.Dispose();
        }
    }

    private sealed class MutableTarget : IInteractionTarget
    {
        private readonly DelegatingPipeline pipeline;

        public MutableTarget(string id, Action<MutableTarget>? stage)
        {
            Id = id;
            pipeline = new DelegatingPipeline(this, stage);
        }

        public string Id { get; }

        public string Label { get; set; } = "before";

        public InteractionDescriptor Describe()
        {
            return new InteractionDescriptor(
                Id,
                null,
                "button",
                Label,
                null,
                true,
                true,
                new[]
                {
                    new AvailableInteraction("click", 1, ClickCommandSchema.Instance.Arguments),
                });
        }

        public bool TryGetPipeline<TCommand>(
            out IInteractionPipeline<TCommand>? resolved)
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

    // A misbehaving probe that returns no snapshot, standing in for infrastructure failure
    // (a null or uncanonicalizable capture) during before-state observation.
    private sealed class NullSnapshotProbe : IInteractionStateProbe
    {
        public string Id => "broken";

        public int Version => 1;

        public StateProbeSnapshot Capture() => null!;
    }

    // A single-stage pipeline that runs the target's configured stage action inside execution,
    // so a test can mutate observable state (and optionally throw) between the before and after
    // state captures.
    private sealed class DelegatingPipeline : IInteractionPipeline<ClickCommand>
    {
        private readonly MutableTarget target;
        private readonly Action<MutableTarget>? stage;

        public DelegatingPipeline(MutableTarget target, Action<MutableTarget>? stage)
        {
            this.target = target;
            this.stage = stage;
        }

        public InteractionValidation Validate(in ClickCommand command)
        {
            return InteractionValidation.Valid;
        }

        public ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            stage?.Invoke(target);
            return default;
        }
    }
}
