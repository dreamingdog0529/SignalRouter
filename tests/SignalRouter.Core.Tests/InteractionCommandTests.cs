using NUnit.Framework;
using VitalRouter;

namespace SignalRouter.Core.Tests;

public sealed class InteractionCommandTests
{
    [Test]
    public void CommandsAreVitalRouterValuesWithOrdinalEquality()
    {
        var click = new ClickCommand("menu.start");
        var sameClick = new ClickCommand("menu.start");
        var differentlyCasedClick = new ClickCommand("Menu.Start");
        var value = new SetValueCommand("profile.name", string.Empty);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(click, Is.EqualTo(sameClick));
            Assert.That(click.GetHashCode(), Is.EqualTo(sameClick.GetHashCode()));
            Assert.That(click, Is.Not.EqualTo(differentlyCasedClick));
            Assert.That(value.Value, Is.Empty);
            Assert.That(click, Is.InstanceOf<IInteractionCommand>());
            Assert.That(click, Is.InstanceOf<ICommand>());
        });
    }

    [Test]
    public void DefaultCommandsHaveStableValueEquality()
    {
        NUnitCompat.Multiple(() =>
        {
            Assert.That(default(ClickCommand), Is.EqualTo(default(ClickCommand)));
            Assert.That(default(SetValueCommand), Is.EqualTo(default(SetValueCommand)));
            Assert.That(default(ClickCommand).GetHashCode(), Is.Zero);
            Assert.That(default(SetValueCommand).GetHashCode(), Is.Zero);
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" menu.start")]
    [TestCase("menu.start ")]
    [TestCase("menu\nstart")]
    public void CommandsRejectInvalidTargetIds(string? targetId)
    {
        NUnitCompat.Multiple(() =>
        {
            NUnitCompat.ThatThrows(
                () => new ClickCommand(targetId!),
                Throws.InstanceOf<ArgumentException>());
            NUnitCompat.ThatThrows(
                () => new SetValueCommand(targetId!, "value"),
                Throws.InstanceOf<ArgumentException>());
        });
    }

    [Test]
    public void SetValueRejectsNullValue()
    {
        NUnitCompat.ThatThrows(
            () => new SetValueCommand("profile.name", null!),
            Throws.TypeOf<ArgumentNullException>());
    }

    [Test]
    public void DispatchOptionsUseOrdinalValueEquality()
    {
        var options = new InteractionDispatchOptions(
            InteractionOrigin.Agent,
            "correlation-1",
            "idempotency-1");
        var same = new InteractionDispatchOptions(
            InteractionOrigin.Agent,
            "correlation-1",
            "idempotency-1");
        var differentlyCased = new InteractionDispatchOptions(
            InteractionOrigin.Agent,
            "Correlation-1",
            "idempotency-1");

        NUnitCompat.Multiple(() =>
        {
            Assert.That(options, Is.EqualTo(same));
            Assert.That(options.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(options, Is.Not.EqualTo(differentlyCased));
            NUnitCompat.ThatThrows(
                () => new InteractionDispatchOptions((InteractionOrigin)99),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }
}
