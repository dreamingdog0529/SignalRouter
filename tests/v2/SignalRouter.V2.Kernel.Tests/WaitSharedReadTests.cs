using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// The wait-reevaluation read discipline (plan P1d, finding A3): without
/// sampled sources a revision advance takes one shared evaluation read per
/// domain; with a sampled source exposed to the domain's family, every armed
/// wait keeps its own fresh read — sampled sources are read at materialization
/// time (observation-state.md §7), and sharing would freeze them.
/// </summary>
public sealed class WaitSharedReadTests
{
    private sealed class CountingSampledReader : ISampledSourceReader
    {
        internal int ReadCount;

        internal long ProducedAt = 100;

        public SampledDocument? Read()
        {
            ReadCount++;
            return new SampledDocument(
                new SourceDocument(ValueArray<NamedField>.From(new[]
                {
                    new NamedField("flag", FieldValue.Of(true)),
                })),
                ProducedAt);
        }
    }

    private static KernelFixture WithSampledSource(out CountingSampledReader reader)
    {
        var fixture = new KernelFixture(start: false);
        reader = new CountingSampledReader();
        fixture.Runtime.Bootstrap.RegisterStateSource(new StateSourceRegistration(
            new StateSourceKey("sampled"),
            new StateSourceContractDescriptor(
                new StateSourceContractRef(
                    new StateSourceContractId("sampled"), new ContractVersion(1, 0)),
                ValueArray<SourceFieldSchema>.From(new[]
                {
                    new SourceFieldSchema("flag", FieldType.Boolean, Sensitivity.Standard),
                }),
                agentVisible: true,
                recordVisible: true,
                maxDocumentBytes: 4096),
            StateSourceClass.Sampled,
            reader,
            freshnessBoundLogicalTime: 1_000_000));
        fixture.Runtime.Start(fixture.Executor);
        return fixture;
    }

    [Test]
    public void EveryArmedWaitReadsTheSampledSourceFreshlyOnARevisionAdvance()
    {
        var fixture = WithSampledSource(out var reader);
        var observer = new RecordingWaitObserver();
        for (var i = 0; i < 3; i++)
        {
            fixture.Runtime.Control.ArmWait(
                KernelFixture.LabelExists, KernelFixture.Agent, long.MaxValue, observer);
        }

        fixture.PumpUntilIdle();
        var readsAfterArming = reader.ReadCount;
        Assert.That(readsAfterArming, Is.GreaterThanOrEqualTo(3), "each arm evaluates immediately");

        fixture.PublishInventory(1); // revision advance
        fixture.PumpUntilIdle();

        Assert.That(
            reader.ReadCount - readsAfterArming, Is.GreaterThanOrEqualTo(3),
            "sampled sources are read at materialization time: one fresh read per wait, never shared");
        Assert.That(observer.Resolutions, Is.Empty, "the label predicate stays unsatisfied");
    }

    [Test]
    public void SharedReadsAnswerWaitsIdenticallyToPerWaitReads()
    {
        // Behavior parity of the shared-read path (no sampled sources): waits
        // whose predicate becomes satisfied by a mutation all resolve on the
        // advance, exactly as before.
        var fixture = new KernelFixture();
        var observer = new RecordingWaitObserver();
        for (var i = 0; i < 8; i++)
        {
            fixture.Runtime.Control.ArmWait(
                KernelFixture.LabelExists, KernelFixture.Agent, long.MaxValue, observer);
        }

        fixture.PumpUntilIdle();
        Assert.That(observer.Resolutions, Is.Empty);

        // labelExists compares nodes/save/attributes/label to "Saved".
        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Saved"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();

        Assert.That(observer.Resolutions.Count, Is.EqualTo(8), "every wait saw the satisfying state");
        Assert.That(
            observer.Resolutions.Select(pair => pair.Resolution).Distinct().Single(),
            Is.EqualTo(PredicateResolution.Satisfied));
    }
}
