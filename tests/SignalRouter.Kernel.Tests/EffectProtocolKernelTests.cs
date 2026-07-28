using System.Linq;
using NUnit.Framework;
using SignalRouter.AdapterSdk;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel.Tests;

/// <summary>
/// adapter-conformance.md §3 / ADR 0010, kernel side — the permit-token lifecycle:
/// fences, completion-implies-fence, duplicates, and unknown tokens.
/// </summary>
public sealed class EffectProtocolKernelTests
{
    private static CompletionEvidence Applied() =>
        new(KernelFixture.Applied, CompletionEvidenceKind.Applied, default);

    [Test]
    public void AnExplicitFencePrecedingCompletionIsAccepted()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        fixture.Executor.FenceLast();
        fixture.Pump();
        Assert.That(fixture.TraceKinds(), Has.Some.StartsWith("EffectFenceReached"));

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        fixture.PumpUntilIdle();
        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));
    }

    [Test]
    public void DuplicateCompletionsAreRejectedAndTraced()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle();
        var permit = fixture.Executor.Requests.Single().Permit;

        fixture.Executor.CompleteLast(EffectResolution.Succeeded(Applied()));
        fixture.PumpUntilIdle();
        Assert.That(fixture.Query("r1"), Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));

        fixture.Executor.Sink.ReportCompletion(new EffectCompletion(
            permit, EffectResolution.Faulted(new FaultCode("Late"))));
        fixture.PumpUntilIdle();
        Assert.That(
            fixture.Query("r1"),
            Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)),
            "the late duplicate never alters interaction state");
        Assert.That(fixture.TraceKinds(), Has.Some.Contains("CompletionRejected"));
    }

    [Test]
    public void UnknownTokensAreRejectedAndTraced()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        var forged = new EffectPermitToken(
            new RequestId("r1"), fixture.Runtime.Incarnation, nonce: 999);
        fixture.Executor.Sink.ReportCompletion(new EffectCompletion(
            forged, EffectResolution.Succeeded(Applied())));
        fixture.PumpUntilIdle();

        Assert.That(fixture.Query("r1").Kind, Is.EqualTo(QueryAnswerKind.Pending));
        Assert.That(fixture.TraceKinds(), Has.Some.Contains("CompletionRejected"));
    }

    [Test]
    public void DuplicateFencesAreRejected()
    {
        var fixture = new KernelFixture();
        fixture.Submit("r1");
        fixture.PumpUntilIdle();

        fixture.Executor.FenceLast();
        fixture.Executor.FenceLast();
        fixture.Pump();
        Assert.That(
            fixture.TraceKinds().Count(kind => kind.StartsWith("EffectFenceReached")),
            Is.EqualTo(1));
        Assert.That(fixture.TraceKinds(), Has.Some.Contains("FenceRejected"));
    }
}
