using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SignalRouter.Unity;
using UnityEngine.UI;

namespace SignalRouter.Tests;

public sealed class InteractionButtonAdapterTests
{
    private EditModeUi ui;

    [SetUp]
    public void SetUp()
    {
        ui = new EditModeUi();
    }

    [TearDown]
    public void TearDown()
    {
        ui.Dispose();
    }

    [Test]
    public void DescribeMapsUnityStateOntoTheButtonDescriptor()
    {
        var adapter = ui.CreateButton("menu.start", "Start");

        var inactive = adapter.Describe();
        Assert.That(inactive.Id, Is.EqualTo("menu.start"));
        Assert.That(inactive.Role, Is.EqualTo("button"));
        Assert.That(inactive.Label, Is.EqualTo("Start"));
        Assert.That(inactive.Value, Is.Null);
        Assert.That(inactive.Visible, Is.False);
        Assert.That(inactive.Enabled, Is.True);
        Assert.That(inactive.AvailableInteractions.Count, Is.EqualTo(1));
        Assert.That(inactive.AvailableInteractions[0].WireName, Is.EqualTo("click"));
        Assert.That(inactive.AvailableInteractions[0].Version, Is.EqualTo(1));

        adapter.gameObject.SetActive(true);
        adapter.GetComponent<Button>().interactable = false;

        var active = adapter.Describe();
        Assert.That(active.Visible, Is.True);
        Assert.That(active.Enabled, Is.False);
    }

    [Test]
    public void PipelineResolvesOnlyForClickCommandsAndOnlyAfterConfiguration()
    {
        var adapter = ui.CreateButton("menu.start");

        Assert.That(adapter.TryGetPipeline<ClickCommand>(out var missing), Is.False);
        Assert.That(missing, Is.Null);

        adapter.ConfigurePipeline(new[] { new CountingStage() });

        Assert.That(adapter.TryGetPipeline<ClickCommand>(out var click), Is.True);
        Assert.That(click, Is.Not.Null);
        Assert.That(adapter.TryGetPipeline<SetValueCommand>(out var setValue), Is.False);
        Assert.That(setValue, Is.Null);
    }

    [Test]
    public void ConfiguringThePipelineTwiceThrows()
    {
        var adapter = ui.CreateButton("menu.start");
        adapter.ConfigurePipeline(new[] { new CountingStage() });

        Assert.That(
            () => adapter.ConfigurePipeline(new[] { new CountingStage() }),
            Throws.InvalidOperationException);
    }

    internal sealed class CountingStage : IInteractionStage<ClickCommand>
    {
        public int Executions { get; private set; }

        public string Id => "click.apply-state";

        public int Order => 10;

        public ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            Executions++;
            return default;
        }
    }
}
