using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class InteractionStateProbeRegistryTests
{
    [Test]
    public void RegistrationRejectsNull()
    {
        var registry = new InteractionStateProbeRegistry();

        NUnitCompat.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    [Test]
    public void RegistrationRejectsANonPositiveVersion()
    {
        var registry = new InteractionStateProbeRegistry();

        NUnitCompat.Throws<ArgumentOutOfRangeException>(
            () => registry.Register(new FakeProbe("p", 0, "{}")));
    }

    [Test]
    public void RegistrationRejectsDuplicateIds()
    {
        var registry = new InteractionStateProbeRegistry();
        registry.Register(new FakeProbe("semantic-ui", 1, "{}"));

        NUnitCompat.Throws<InvalidOperationException>(
            () => registry.Register(new FakeProbe("semantic-ui", 1, "{}")));
    }

    [Test]
    public void ObservationCoversEveryProbeSortedByOrdinalId()
    {
        var registry = new InteractionStateProbeRegistry();
        // Registered out of order to prove the observation is ordinal-sorted.
        registry.Register(new FakeProbe("semantic-ui", 1, "{\"a\":1}"));
        registry.Register(new FakeProbe("interaction-runtime", 1, "{\"b\":2}"));

        var observation = registry.Read().ToObservation();

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                observation.Probes.Select(probe => probe.ProbeId).ToArray(),
                Is.EqualTo(new[] { "interaction-runtime", "semantic-ui" }));
            Assert.That(observation.Probes.All(probe => probe.Hash.Length == 64), Is.True);
        });
    }

    [Test]
    public void IdenticalSnapshotsProduceEqualObservationsAndAnEmptyDiff()
    {
        var registry = new InteractionStateProbeRegistry();
        registry.Register(new FakeProbe("semantic-ui", 1, "{\"a\":1}"));

        var before = registry.Read();
        var after = before.ReadSame();

        NUnitCompat.Multiple(() =>
        {
            Assert.That(before.ToObservation(), Is.EqualTo(after.ToObservation()));
            Assert.That(StateProbeReading.Diff(before, after).Probes, Is.Empty);
        });
    }

    [Test]
    public void OnlyChangedProbesAppearInTheDiff()
    {
        var registry = new InteractionStateProbeRegistry();
        var changing = new FakeProbe("semantic-ui", 1, "{\"a\":1}");
        var stable = new FakeProbe("interaction-runtime", 1, "{\"b\":2}");
        registry.Register(changing);
        registry.Register(stable);

        var before = registry.Read();
        changing.Payload = "{\"a\":2}";
        var after = before.ReadSame();
        var diff = StateProbeReading.Diff(before, after);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(diff.Probes, Has.Count.EqualTo(1));
            Assert.That(diff.Probes[0].ProbeId, Is.EqualTo("semantic-ui"));
            Assert.That(
                diff.Probes[0].BeforeHash,
                Is.EqualTo(before.ToObservation().Probes
                    .Single(probe => probe.ProbeId == "semantic-ui").Hash));
            Assert.That(
                diff.Probes[0].AfterHash,
                Is.EqualTo(after.ToObservation().Probes
                    .Single(probe => probe.ProbeId == "semantic-ui").Hash));
            Assert.That(diff.Probes[0].Changes, Is.Empty);
        });
    }

    [Test]
    public void AChangedDiffProviderProbeCarriesItsPropertyChanges()
    {
        var registry = new InteractionStateProbeRegistry();
        var probe = new DiffProbe(
            "semantic-ui",
            "{\"a\":1}",
            () => new[]
            {
                new StatePropertyChange(
                    "targets[x].label",
                    InteractionValue.FromString("before"),
                    InteractionValue.FromString("after")),
            });
        registry.Register(probe);

        var before = registry.Read();
        probe.Payload = "{\"a\":2}";
        var after = before.ReadSame();
        var diff = StateProbeReading.Diff(before, after);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(diff.Probes[0].Changes, Has.Count.EqualTo(1));
            Assert.That(diff.Probes[0].Changes[0].Path, Is.EqualTo("targets[x].label"));
        });
    }

    [Test]
    public void ADiffProviderThatEmitsAnInvalidChangeFailsFastAsAnInvariantViolation()
    {
        var registry = new InteractionStateProbeRegistry();
        // A change whose before equals its after violates StatePropertyChange's contract; a
        // provider that produces one is dispatcher infrastructure misbehaving, not an
        // application fault, so it must escape as an invariant violation (ADR 0001 rule 5).
        var probe = new DiffProbe(
            "semantic-ui",
            "{\"a\":1}",
            () => new[]
            {
                new StatePropertyChange("targets[x].label", InteractionValue.Null, InteractionValue.Null),
            });
        registry.Register(probe);

        var before = registry.Read();
        probe.Payload = "{\"a\":2}";
        var after = before.ReadSame();

        NUnitCompat.Throws<InteractionInvariantViolationException>(
            () => StateProbeReading.Diff(before, after));
    }

    [Test]
    public void ANullSnapshotFailsFastAsAnInvariantViolation()
    {
        var registry = new InteractionStateProbeRegistry();
        registry.Register(new FakeProbe("semantic-ui", 1, null));

        NUnitCompat.Throws<InteractionInvariantViolationException>(() => registry.Read());
    }

    [Test]
    public void AnUncanonicalizableSnapshotFailsFastAsAnInvariantViolation()
    {
        var registry = new InteractionStateProbeRegistry();
        registry.Register(new FakeProbe("semantic-ui", 1, "{\"n\":1.5}"));

        NUnitCompat.Throws<InteractionInvariantViolationException>(() => registry.Read());
    }

    private sealed class FakeProbe : IInteractionStateProbe
    {
        public FakeProbe(string id, int version, string? payload)
        {
            Id = id;
            Version = version;
            Payload = payload;
        }

        public string Id { get; }

        public int Version { get; }

        public string? Payload { get; set; }

        public StateProbeSnapshot Capture()
        {
            return Payload == null ? null! : StateProbeSnapshot.FromJson(Payload);
        }
    }

    // A probe that also provides property-level changes, so a test can drive
    // StateProbeReading.Diff's provider path with a controlled change set (or a bad one).
    private sealed class DiffProbe : IInteractionStateProbe, IStatePropertyDiffProvider
    {
        private readonly Func<IReadOnlyList<StatePropertyChange>> diff;

        public DiffProbe(string id, string payload, Func<IReadOnlyList<StatePropertyChange>> diff)
        {
            Id = id;
            Payload = payload;
            this.diff = diff;
        }

        public string Id { get; }

        public int Version => 1;

        public string Payload { get; set; }

        public StateProbeSnapshot Capture()
        {
            return StateProbeSnapshot.FromJson(Payload);
        }

        public IReadOnlyList<StatePropertyChange> DiffProperties(
            StateProbeSnapshot before,
            StateProbeSnapshot after)
        {
            return diff();
        }
    }
}
