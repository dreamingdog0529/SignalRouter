using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SignalRouter.Unity;
using TMPro;

namespace SignalRouter.Tests;

public sealed class InteractionTextInputAdapterTests
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
    public void DescribeMapsUnityStateOntoTheTextboxDescriptor()
    {
        var adapter = ui.CreateTextInput("profile.name", "Name");
        adapter.GetComponent<TMP_InputField>().SetTextWithoutNotify("Wanwan");

        var descriptor = adapter.Describe();
        Assert.That(descriptor.Id, Is.EqualTo("profile.name"));
        Assert.That(descriptor.Role, Is.EqualTo("textbox"));
        Assert.That(descriptor.Label, Is.EqualTo("Name"));
        Assert.That(descriptor.Value, Is.EqualTo(InteractionValue.FromString("Wanwan")));
        Assert.That(descriptor.AvailableInteractions.Count, Is.EqualTo(1));
        Assert.That(descriptor.AvailableInteractions[0].WireName, Is.EqualTo("set_value"));
        Assert.That(descriptor.AvailableInteractions[0].Version, Is.EqualTo(1));
    }

    [Test]
    public void PipelineContainsTheBuiltInApplyStage()
    {
        var adapter = ui.CreateTextInput("profile.name");
        adapter.ConfigurePipeline();

        Assert.That(adapter.TryGetPipeline<SetValueCommand>(out var pipeline), Is.True);
        Assert.That(pipeline, Is.Not.Null);
        Assert.That(adapter.TryGetPipeline<ClickCommand>(out var click), Is.False);
        Assert.That(click, Is.Null);
    }

    [Test]
    public void SuppliedStagesMustNotCollideWithTheReservedApplyOrder()
    {
        var adapter = ui.CreateTextInput("profile.name");

        Assert.That(
            () => adapter.ConfigurePipeline(
                new[] { new FixedOrderStage(InteractionTextInput.ApplyStageOrder) }),
            Throws.ArgumentException);
    }

    [Test]
    public void SuppliedStagesAtLaterOrdersAreAccepted()
    {
        var adapter = ui.CreateTextInput("profile.name");
        adapter.ConfigurePipeline(new[] { new FixedOrderStage(20) });

        Assert.That(adapter.TryGetPipeline<SetValueCommand>(out var pipeline), Is.True);
        Assert.That(pipeline, Is.Not.Null);
    }

    private sealed class FixedOrderStage : IInteractionStage<SetValueCommand>
    {
        public FixedOrderStage(int order)
        {
            Order = order;
        }

        public string Id => "set_value.present";

        public int Order { get; }

        public ValueTask ExecuteAsync(
            SetValueCommand command,
            InteractionContext context,
            CancellationToken cancellationToken)
        {
            return default;
        }
    }
}
