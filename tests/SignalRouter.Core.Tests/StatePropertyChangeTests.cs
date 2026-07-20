using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class StatePropertyChangeTests
{
    [Test]
    public void BothSidesPresentAndDifferingIsAModifiedChange()
    {
        var change = new StatePropertyChange(
            "targets[menu.start].enabled",
            InteractionValue.FromBoolean(true),
            InteractionValue.FromBoolean(false));

        NUnitCompat.Multiple(() =>
        {
            Assert.That(change.Kind, Is.EqualTo(StatePropertyChangeKind.Modified));
            Assert.That(change.Before, Is.EqualTo(InteractionValue.FromBoolean(true)));
            Assert.That(change.After, Is.EqualTo(InteractionValue.FromBoolean(false)));
        });
    }

    [Test]
    public void AnAbsentBeforeIsAnAddedChange()
    {
        var change = new StatePropertyChange(
            "targets[menu.options].label",
            null,
            InteractionValue.FromString("Options"));

        NUnitCompat.Multiple(() =>
        {
            Assert.That(change.Kind, Is.EqualTo(StatePropertyChangeKind.Added));
            Assert.That(change.Before, Is.Null);
            Assert.That(change.After, Is.EqualTo(InteractionValue.FromString("Options")));
        });
    }

    [Test]
    public void AnAbsentAfterIsARemovedChange()
    {
        var change = new StatePropertyChange(
            "targets[menu.options].label",
            InteractionValue.FromString("Options"),
            null);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(change.Kind, Is.EqualTo(StatePropertyChangeKind.Removed));
            Assert.That(change.Before, Is.EqualTo(InteractionValue.FromString("Options")));
            Assert.That(change.After, Is.Null);
        });
    }

    [Test]
    public void BothSidesAbsentIsRejected()
    {
        NUnitCompat.Throws<ArgumentException>(
            () => new StatePropertyChange("targets[menu.start].label", null, null));
    }

    [Test]
    public void BothSidesPresentAndEqualIsRejected()
    {
        var value = InteractionValue.FromString("same");

        NUnitCompat.Throws<ArgumentException>(
            () => new StatePropertyChange("targets[menu.start].label", value, value));
    }

    [Test]
    public void AnAbsentSideDoesNotCollideWithAnExplicitNullValue()
    {
        // A field going from absent to present-null (an added target whose value is null) differs
        // from a Modified change: the model keeps the C# null side distinct from InteractionValue.Null.
        var added = new StatePropertyChange(
            "targets[menu.options].value",
            null,
            InteractionValue.Null);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(added.Kind, Is.EqualTo(StatePropertyChangeKind.Added));
            Assert.That(added.Before, Is.Null);
            Assert.That(added.After, Is.EqualTo(InteractionValue.Null));
        });
    }
}
