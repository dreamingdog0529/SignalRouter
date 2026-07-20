using System;
using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class EquatableListTests
{
    [Test]
    public void EmptySourcesReuseTheEmptySingleton()
    {
        var schema = new InteractionArgumentSchema(
            Array.Empty<InteractionArgumentDefinition>());
        var observation = new StateObservation(
            Array.Empty<StateProbeObservation>());

        Assert.That(
            ReferenceEquals(
                schema.Arguments,
                InteractionArgumentSchema.Empty.Arguments),
            Is.True);
        Assert.That(
            ReferenceEquals(observation.Probes, StateObservation.Empty.Probes),
            Is.True);
    }

    [Test]
    public void IndependentlyBuiltListsAreStructurallyEqualWithMatchingHashCodes()
    {
        var first = new InteractionArgumentSchema(new[]
        {
            new InteractionArgumentDefinition(
                "value", InteractionArgumentType.String, true, false),
        });
        var second = new InteractionArgumentSchema(new[]
        {
            new InteractionArgumentDefinition(
                "value", InteractionArgumentType.String, true, false),
        });

        Assert.That(first.Arguments.Equals(second.Arguments), Is.True);
        Assert.That(
            first.Arguments.GetHashCode(),
            Is.EqualTo(second.Arguments.GetHashCode()));
    }

    [Test]
    public void ListsDifferingInLengthOrElementsAreNotEqual()
    {
        var definition = new InteractionArgumentDefinition(
            "value", InteractionArgumentType.String, true, false);
        var one = new InteractionArgumentSchema(new[] { definition });
        var two = new InteractionArgumentSchema(new[] { definition, new InteractionArgumentDefinition(
            "other", InteractionArgumentType.Number, false, false) });
        var different = new InteractionArgumentSchema(new[]
        {
            new InteractionArgumentDefinition(
                "value", InteractionArgumentType.Number, true, false),
        });

        Assert.That(one.Arguments.Equals(two.Arguments), Is.False);
        Assert.That(one.Arguments.Equals(different.Arguments), Is.False);
        Assert.That(one.Arguments.Equals(null), Is.False);
    }

    [Test]
    public void SourceMutationsAfterConstructionAreNotObserved()
    {
        var source = new System.Collections.Generic.List<InteractionArgumentDefinition>
        {
            new InteractionArgumentDefinition(
                "value", InteractionArgumentType.String, true, false),
        };
        var schema = new InteractionArgumentSchema(source);

        source.Add(new InteractionArgumentDefinition(
            "other", InteractionArgumentType.Number, false, false));

        Assert.That(schema.Arguments.Count, Is.EqualTo(1));
    }

    [Test]
    public void SortedUniqueFactoryOrdersByKeyAndRejectsDuplicates()
    {
        var observation = new StateObservation(new[]
        {
            new StateProbeObservation("b", "hash-b"),
            new StateProbeObservation("a", "hash-a"),
        });

        Assert.That(observation.Probes[0].ProbeId, Is.EqualTo("a"));
        Assert.That(observation.Probes[1].ProbeId, Is.EqualTo("b"));
        NUnitCompat.Throws<ArgumentException>(() => new StateObservation(new[]
        {
            new StateProbeObservation("a", "hash-1"),
            new StateProbeObservation("a", "hash-2"),
        }));
    }
}
