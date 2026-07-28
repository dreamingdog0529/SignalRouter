using System;
using NUnit.Framework;
using SignalRouter.Contracts;

namespace SignalRouter.AdapterSdk.Tests;

/// <summary>
/// adapter-conformance.md §4 / ADR 0010 — the adapter self-declaration the TCK
/// verifies: phases with a member fence phase, unique support/latency/
/// classification rows, and the normative bounds.
/// </summary>
public sealed class AdapterDescriptorTests
{
    private static ValueArray<FramePhase> Phases => ValueArray<FramePhase>.From(new[]
    {
        FramePhase.Update, FramePhase.LateUpdate,
    });

    private static AdapterDescriptor Build(
        FramePhase? fence = null,
        int syncBound = 5,
        ValueArray<CompletionLatencyBound>? latencies = null) =>
        new(
            "reference-adapter",
            new ContractVersion(1, 0),
            Phases,
            fence ?? FramePhase.LateUpdate,
            ValueArray<CapabilityProfileSupport>.From(new[]
            {
                new CapabilityProfileSupport(
                    SdkTestData.Capability,
                    ValueArray<CompletionProfileRef>.From(new[] { SdkTestData.Applied })),
            }),
            syncBound,
            latencies ?? ValueArray<CompletionLatencyBound>.From(new[]
            {
                new CompletionLatencyBound(SdkTestData.Applied, 1),
            }),
            ValueArray<InputClassification>.From(new[]
            {
                new InputClassification("synthetic-click", InputClass.Managed),
                new InputClassification("external-world-mutation", InputClass.Observed),
            }));

    [Test]
    public void AValidDescriptorConstructs()
    {
        var descriptor = Build();
        Assert.That(descriptor.FencePhase, Is.EqualTo(FramePhase.LateUpdate));
        Assert.That(descriptor.SyncExecutionBoundMilliseconds, Is.EqualTo(5));
    }

    [Test]
    public void TheFencePhaseMustBeADeclaredPhase()
    {
        AssertEx.Throws<ArgumentException>(() => Build(fence: new FramePhase("Undeclared")));
    }

    [Test]
    public void TheSyncBoundIsNormativeAndPositive()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(() => Build(syncBound: 0));
    }

    [Test]
    public void RowsAreUniquePerCapabilityProfileAndInputClass()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new CapabilityProfileSupport(
            SdkTestData.Capability,
            ValueArray<CompletionProfileRef>.From(new[] { SdkTestData.Applied, SdkTestData.Applied })));

        AssertEx.Throws<ArgumentException>(() => Build(
            latencies: ValueArray<CompletionLatencyBound>.From(new[]
            {
                new CompletionLatencyBound(SdkTestData.Applied, 1),
                new CompletionLatencyBound(SdkTestData.Applied, 2),
            })));

        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new CompletionLatencyBound(
            SdkTestData.FrameCommitted, 0));
    }

    [Test]
    public void EverySupportedProfileHasExactlyOneLatencyBound()
    {
        // A supported profile without a declared MaxFrames leaves the TCK and host
        // with nothing to enforce; an orphan bound declares latency for nothing.
        AssertEx.Throws<ArgumentException>(() => Build(
            latencies: ValueArray<CompletionLatencyBound>.Empty));
        AssertEx.Throws<ArgumentException>(() => Build(
            latencies: ValueArray<CompletionLatencyBound>.From(new[]
            {
                new CompletionLatencyBound(SdkTestData.Applied, 1),
                new CompletionLatencyBound(SdkTestData.FrameCommitted, 2),
            })));
    }
}
