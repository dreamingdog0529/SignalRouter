using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class InteractionValueTests
{
    [Test]
    public void ScalarKindsHaveStructuralValueEquality()
    {
        var firstNumber = InteractionValue.FromNumber(1.00m);
        var secondNumber = InteractionValue.FromNumber(1m);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(InteractionValue.Null, Is.EqualTo(InteractionValue.Null));
            Assert.That(
                InteractionValue.FromString("value"),
                Is.EqualTo(InteractionValue.FromString("value")));
            Assert.That(
                InteractionValue.FromBoolean(true),
                Is.EqualTo(InteractionValue.FromBoolean(true)));
            Assert.That(firstNumber, Is.EqualTo(secondNumber));
            Assert.That(firstNumber.GetHashCode(), Is.EqualTo(secondNumber.GetHashCode()));
            Assert.That(
                InteractionValue.FromString("1"),
                Is.Not.EqualTo(InteractionValue.FromNumber(1m)));
        });
    }

    [Test]
    public void AccessorsRequireTheMatchingKind()
    {
        var text = InteractionValue.FromString(string.Empty);
        var boolean = InteractionValue.FromBoolean(false);
        var number = InteractionValue.FromNumber(decimal.MaxValue);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(text.GetString(), Is.Empty);
            Assert.That(boolean.GetBoolean(), Is.False);
            Assert.That(number.GetNumber(), Is.EqualTo(decimal.MaxValue));
            NUnitCompat.ThatThrows(() => text.GetBoolean(), Throws.InvalidOperationException);
            NUnitCompat.ThatThrows(() => boolean.GetNumber(), Throws.InvalidOperationException);
            NUnitCompat.ThatThrows(
                () => InteractionValue.Null.GetString(),
                Throws.InvalidOperationException);
            NUnitCompat.ThatThrows(
                () => InteractionValue.FromString(null!),
                Throws.ArgumentNullException);
        });
    }
}
