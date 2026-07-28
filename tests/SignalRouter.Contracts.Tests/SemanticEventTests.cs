using System;
using NUnit.Framework;

namespace SignalRouter.Contracts.Tests;

/// <summary>
/// observation-state.md §6 / ADR 0003 — the in-memory event algebra:
/// the causation union, the reserved EventKind vocabulary, and per-kind identifier
/// participation.
/// </summary>
public sealed class SemanticEventTests
{
    [Test]
    public void CausationIsAUnionNotAnIdentifier()
    {
        var byRequest = EventCausation.OfRequest(TestData.Request("r1"));
        Assert.That(byRequest.Kind, Is.EqualTo(EventCausationKind.Request));
        Assert.That(byRequest.Request, Is.EqualTo(TestData.Request("r1")));
        AssertEx.Throws<InvalidOperationException>(() => _ = byRequest.ExternalHint);

        var external = EventCausation.OfExternal("scene-loader");
        Assert.That(external.ExternalHint, Is.EqualTo("scene-loader"));
        AssertEx.Throws<InvalidOperationException>(() => _ = external.Request);

        Assert.That(EventCausation.None.Kind, Is.EqualTo(EventCausationKind.None));
        AssertEx.Throws<ArgumentException>(() => EventCausation.OfRequest(default));
    }

    [Test]
    public void ReservedEventKindsMatchTheSpecList()
    {
        Assert.That(EventKind.Admitted.Value, Is.EqualTo("Admitted"));
        Assert.That(EventKind.StateTransition.Value, Is.EqualTo("StateTransition"));
        Assert.That(EventKind.EffectPermitted.Value, Is.EqualTo("EffectPermitted"));
        Assert.That(EventKind.EffectFenceReached.Value, Is.EqualTo("EffectFenceReached"));
        Assert.That(EventKind.TerminalCommitted.Value, Is.EqualTo("TerminalCommitted"));
        Assert.That(EventKind.SourcePublicationAdopted.Value, Is.EqualTo("SourcePublicationAdopted"));
        Assert.That(EventKind.PredicateArmed.Value, Is.EqualTo("PredicateArmed"));
        Assert.That(EventKind.PredicateResolved.Value, Is.EqualTo("PredicateResolved"));
        Assert.That(EventKind.AssertionEvaluated.Value, Is.EqualTo("AssertionEvaluated"));
        Assert.That(EventKind.HumanIntentBlocked.Value, Is.EqualTo("HumanIntentBlocked"));
        Assert.That(EventKind.ContaminationObserved.Value, Is.EqualTo("ContaminationObserved"));
        Assert.That(EventKind.IncarnationLifecycle.Value, Is.EqualTo("IncarnationLifecycle"));
        Assert.That(EventKind.TraceGap.Value, Is.EqualTo("TraceGap"));
        Assert.That(new EventKind("AdapterDefinedKind").IsDefault, Is.False, "the vocabulary is open");
    }

    [Test]
    public void IdentifierParticipationIsPerKindNeverUniversal()
    {
        var mutationEvent = new SemanticEvent(
            EventKind.Admitted,
            TestData.Incarnation,
            EventCausation.OfRequest(TestData.Request("r1")),
            request: TestData.Request("r1"),
            order: new LogicalOrder(1));
        Assert.That(mutationEvent.Operation, Is.Null);

        var waitEvent = new SemanticEvent(
            EventKind.PredicateArmed,
            TestData.Incarnation,
            EventCausation.None,
            operation: TestData.Operation("op1"));
        Assert.That(waitEvent.Request, Is.Null);
    }

    [Test]
    public void EventsRejectDefaultComponentsAndFreeTextDetails()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new SemanticEvent(
            default, TestData.Incarnation, EventCausation.None));
        AssertEx.Throws<ArgumentException>(() => _ = new SemanticEvent(
            EventKind.Admitted, default, EventCausation.None));
        AssertEx.Throws<ArgumentException>(() => _ = new SemanticEvent(
            EventKind.Admitted, TestData.Incarnation, EventCausation.None,
            detailCode: "free text with spaces"));
    }
}
