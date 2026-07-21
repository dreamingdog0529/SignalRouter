using NUnit.Framework;

namespace SignalRouter.Tests;

// Adapter registration against a hand-built registry (design §21.2): stable-ID
// uniqueness, snapshot membership, revision arithmetic, and descriptor/pipeline
// validation all run exactly as they will under the runtime.
public sealed class InteractionTargetRegistrationTests
{
    private EditModeUi ui;
    private InteractionRegistry registry;

    [SetUp]
    public void SetUp()
    {
        ui = new EditModeUi();
        registry = new InteractionRegistry(
            InteractionCommandCatalog.CreateMvp(),
            "editmode-session");
    }

    [TearDown]
    public void TearDown()
    {
        ui.Dispose();
    }

    [Test]
    public void ConfiguredAdaptersRegisterAndAppearInTheSnapshot()
    {
        var button = ui.CreateButton("menu.start", "Start");
        button.ConfigurePipeline(new[] { new InteractionButtonAdapterTests.CountingStage() });
        var input = ui.CreateTextInput("profile.name", "Name");
        input.ConfigurePipeline();

        using var buttonRegistration = registry.Register(button, true);
        using var inputRegistration = registry.Register(input, true);

        var snapshot = registry.GetSnapshot(InteractionRegistryView.All);
        Assert.That(snapshot.Targets.Count, Is.EqualTo(2));
        Assert.That(snapshot.Targets[0].Id, Is.EqualTo("menu.start"));
        Assert.That(snapshot.Targets[1].Id, Is.EqualTo("profile.name"));
        Assert.That(registry.Revision, Is.EqualTo(2));
    }

    [Test]
    public void DuplicateStableIdsAreRejected()
    {
        var first = ui.CreateButton("menu.start");
        first.ConfigurePipeline(new[] { new InteractionButtonAdapterTests.CountingStage() });
        var second = ui.CreateButton("menu.start");
        second.ConfigurePipeline(new[] { new InteractionButtonAdapterTests.CountingStage() });

        using var registration = registry.Register(first, true);
        var revisionBefore = registry.Revision;

        Assert.That(
            () => registry.Register(second, true),
            Throws.InvalidOperationException);
        Assert.That(registry.Revision, Is.EqualTo(revisionBefore));
    }

    [Test]
    public void DisposingTheRegistrationRemovesTheTargetAndBumpsTheRevision()
    {
        var button = ui.CreateButton("menu.start");
        button.ConfigurePipeline(new[] { new InteractionButtonAdapterTests.CountingStage() });

        var registration = registry.Register(button, true);
        Assert.That(registry.Revision, Is.EqualTo(1));

        registration.Dispose();

        Assert.That(registry.GetSnapshot(InteractionRegistryView.All).Targets, Is.Empty);
        Assert.That(registry.Revision, Is.EqualTo(2));
    }

    [Test]
    public void UnconfiguredAdaptersAreRejectedAtRegistration()
    {
        var button = ui.CreateButton("menu.start");

        Assert.That(
            () => registry.Register(button, true),
            Throws.InvalidOperationException);
    }

    [Test]
    public void EmptyTargetIdsAreRejectedAtRegistration()
    {
        var button = ui.CreateButton(string.Empty);
        button.ConfigurePipeline(new[] { new InteractionButtonAdapterTests.CountingStage() });

        Assert.That(
            () => registry.Register(button, true),
            Throws.ArgumentException);
    }
}
