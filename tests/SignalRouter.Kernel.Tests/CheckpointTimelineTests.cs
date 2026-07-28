using System.Linq;
using NUnit.Framework;
using SignalRouter.AdapterSdk;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel.Tests;

/// <summary>
/// observation-state.md §8 as amended by ADR 0011 — the checkpoint-only timeline:
/// pump-boundary heartbeats, gap marks, SourceRevision indexing with LogicalOrder
/// as optional causation metadata, default-deny reading, and double-bounded
/// retention.
/// </summary>
public sealed class CheckpointTimelineTests
{
    private static readonly Principal Recorder =
        new(Principal.WellKnownKinds.TestHarness, "recorder");

    [Test]
    public void WithoutACodecTheTimelineStaysInert()
    {
        var fixture = new KernelFixture();
        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();
        Assert.That(fixture.Runtime.Timeline.Snapshot(Recorder), Is.Empty);
    }

    [Test]
    public void TimelineReadingIsPrincipalBoundDefaultDeny()
    {
        var fixture = new KernelFixture(codec: new TestCanonicalStateCodec());
        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();

        Assert.That(fixture.Runtime.Timeline.Snapshot(Recorder), Is.Not.Empty);
        Assert.That(
            fixture.Runtime.Timeline.Snapshot(KernelFixture.Agent), Is.Empty,
            "record-view ContentIds never reach an agent-domain reader");
        Assert.That(
            fixture.Runtime.Timeline.Snapshot(new Principal("UnknownKind", "nobody")), Is.Empty,
            "an unbound principal answers exactly as an empty timeline");
    }

    [Test]
    public void RevisionAdvancesProduceCheckpointsWithCausationMetadata()
    {
        var fixture = new KernelFixture(codec: new TestCanonicalStateCodec());
        fixture.Executor.OnExecute = _ => fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Saved"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();

        // A mutation-caused advance: the effect updates the node, the terminal
        // commits at that watermark, and the checkpoint cites the causing order.
        fixture.Submit("r1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(new CompletionEvidence(
            KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();

        var afterEffect = fixture.Runtime.Timeline.Snapshot(Recorder);
        Assert.That(afterEffect, Is.Not.Empty);
        Assert.That(
            afterEffect[^1].CausingOrder, Is.EqualTo(new LogicalOrder(1)),
            "the entry at the terminal's watermark cites the interaction's order");

        // A source-only advance carries no causing order (ADR 0009: no fabricated
        // LogicalOrder).
        fixture.PublishInventory(5);
        fixture.PumpUntilIdle();
        var afterPublication = fixture.Runtime.Timeline.Snapshot(Recorder);
        Assert.That(afterPublication[^1].CausingOrder, Is.Null);
        Assert.That(
            afterPublication[^1].Revision.Value,
            Is.GreaterThan(afterEffect[^1].Revision.Value));
        Assert.That(
            afterPublication.Select(entry => entry.EntrySequence),
            Is.Ordered.Ascending, "the deterministic per-feed entry sequence");
    }

    [Test]
    public void ABudgetStarvedPumpRecordsAGapAndTheNextCheckpointResyncs()
    {
        // One pump both consumes the whole observation budget (a pinned snapshot)
        // and advances the revision (a publication): the feed cannot retain a
        // checkpoint, so the advance is a gap and the next successful checkpoint
        // carries the mark (observation-state.md §4 heartbeats, ADR 0011).
        var view = new ViewContractRef(new ViewContractId("agent-standard"), new ContractVersion(1, 0));
        var fixture = new KernelFixture(
            codec: new TestCanonicalStateCodec(), observationBudgetNodes: 1, start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            view, ViewFamily.Agent, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Start(fixture.Executor);
        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();
        var baseline = fixture.Runtime.Timeline.Snapshot(Recorder).Count;

        var observer = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(view, KernelFixture.Agent, "root", observer);
        fixture.PublishInventory(2);
        fixture.Pump();
        Assert.That(observer.Pinned, Has.Count.EqualTo(1), "the snapshot consumed the pump budget");
        Assert.That(
            fixture.Runtime.Timeline.Snapshot(Recorder).Count, Is.EqualTo(baseline),
            "no checkpoint could be retained this pump — the advance is a gap");

        fixture.PublishInventory(3);
        fixture.PumpUntilIdle();
        var entries = fixture.Runtime.Timeline.Snapshot(Recorder);
        Assert.That(entries.Count, Is.GreaterThan(baseline));
        Assert.That(entries[^1].AfterGap, Is.True, "the resynchronizing checkpoint carries the mark");
    }

    private sealed class ThrowingCodec : ICanonicalStateCodec
    {
        public CanonicalStateResult Encode(ObservationMaterialization materialization) =>
            throw new System.InvalidOperationException("codec exploded");
    }

    [Test]
    public void AThrowingCodecNeverSuppressesTheTrueTerminal()
    {
        // codex review: the terminal is committed before the after-basis capture,
        // so a codec failure degrades the recording alone (guarantees.md §7).
        var fixture = new KernelFixture(codec: new ThrowingCodec());
        fixture.Submit("r1");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(new CompletionEvidence(
            KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));
        Assert.That(fixture.TraceKinds(), Has.Some.Contains("AfterBasisUnavailable"));
        Assert.That(
            fixture.Runtime.RecordObservation.TryGetAfterMaterialization(
                new RequestId("r1"), out _),
            Is.False, "no basis was retained — honest absence, never a crash");
    }

    [Test]
    public void TheFeedNeverRunsPastThePumpDeadlineAndRetriesLater()
    {
        // codex review: a deadline-exhausted pump must not materialize or invoke
        // the codec; the unobserved revision is retried at the next pump.
        var fixture = new KernelFixture(codec: new TestCanonicalStateCodec());
        fixture.PumpUntilIdle();
        var baseline = fixture.Runtime.Timeline.Snapshot(Recorder).Count;

        fixture.PublishInventory(1);
        fixture.Clock.Value = 10;
        fixture.Runtime.Pump(new PumpBudget(
            maxTurns: 64, deadline: 5, new LogicalTime(fixture.LogicalNow), FramePhase.Update));
        Assert.That(
            fixture.Runtime.Timeline.Snapshot(Recorder).Count, Is.EqualTo(baseline),
            "no checkpoint work past the deadline");

        fixture.PumpUntilIdle();
        Assert.That(
            fixture.Runtime.Timeline.Snapshot(Recorder).Count, Is.GreaterThan(baseline),
            "the unobserved revision is checkpointed at the next pump without another mutation");
    }

    [Test]
    public void ASkippedRevisionIsRetriedWithoutAnotherMutation()
    {
        // codex review: a budget-starved feed must not mark the revision observed;
        // the very next pump retries and the checkpoint carries the gap mark.
        var view = new ViewContractRef(new ViewContractId("agent-standard"), new ContractVersion(1, 0));
        var fixture = new KernelFixture(
            codec: new TestCanonicalStateCodec(), observationBudgetNodes: 1, start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            view, ViewFamily.Agent, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        fixture.Runtime.Start(fixture.Executor);
        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();
        var baseline = fixture.Runtime.Timeline.Snapshot(Recorder).Count;

        var observer = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(view, KernelFixture.Agent, "root", observer);
        fixture.PublishInventory(2);
        fixture.Pump();
        Assert.That(fixture.Runtime.Timeline.Snapshot(Recorder).Count, Is.EqualTo(baseline));

        fixture.Pump();
        var entries = fixture.Runtime.Timeline.Snapshot(Recorder);
        Assert.That(entries.Count, Is.GreaterThan(baseline), "retried against the fresh budget");
        Assert.That(entries[^1].AfterGap, Is.True);
    }

    [Test]
    public void MixedCausesInOnePumpNeverMisattributeASourceAdvance()
    {
        // codex review: a zero-effect rejection terminating in the same pump as a
        // source publication must not lend its order to the source-only checkpoint.
        var failingPrecondition = new PredicateDefinition(
            ValueArray<PredicateClause>.From(new[]
            {
                new PredicateClause(new ClauseId("c0"), new ComparisonExpression(
                    new FieldPath("nodes/save/attributes/label"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of("NeverThisValue"))),
            }));
        var fixture = new KernelFixture(
            codec: new TestCanonicalStateCodec(), invokePrecondition: failingPrecondition);
        fixture.PumpUntilIdle();

        fixture.Submit("r1");
        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Rejected)));
        var entries = fixture.Runtime.Timeline.Snapshot(Recorder);
        Assert.That(
            entries[^1].CausingOrder, Is.Null,
            "the advance was the publication's; the rejected interaction mutated nothing");
    }

    [Test]
    public void ASelfEvictedCheckpointIsAGapNotASilentSuccess()
    {
        // codex review: an entry larger than the retention bound evicts itself;
        // the feed must record a gap and the next retainable checkpoint must
        // carry the mark.
        var fixture = new KernelFixture(
            codec: new TestCanonicalStateCodec(), timelineRetentionBytes: 450);
        fixture.PumpUntilIdle();
        Assert.That(fixture.Runtime.Timeline.Snapshot(Recorder), Is.Not.Empty, "the small world fits");

        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute(
                    "label", FieldValue.Of(new string('x', 600)), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();
        var afterBig = fixture.Runtime.Timeline.Snapshot(Recorder);
        Assert.That(
            afterBig.All(entry => entry.Revision.Value < 100) && afterBig.Count <= 1, Is.True,
            "the oversized checkpoint never pretends to be retained");

        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("small"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();
        var entries = fixture.Runtime.Timeline.Snapshot(Recorder);
        Assert.That(entries[^1].AfterGap, Is.True, "the resynchronizing checkpoint carries the mark");
    }

    [Test]
    public void RetentionKeepsTheNewestEntriesOnly()
    {
        var fixture = new KernelFixture(
            codec: new TestCanonicalStateCodec(), timelineRetentionEntries: 2);
        for (var i = 1; i <= 4; i++)
        {
            fixture.PublishInventory(i);
            fixture.PumpUntilIdle();
        }

        var entries = fixture.Runtime.Timeline.Snapshot(Recorder);
        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(
            entries.Select(entry => entry.Revision.Value),
            Is.Ordered.Ascending, "the ring keeps the newest checkpoints");
    }
}
