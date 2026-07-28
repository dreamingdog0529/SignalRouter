using System;
using NUnit.Framework;
using SignalRouter.Contracts;

namespace SignalRouter.AdapterSdk.Tests;

/// <summary>
/// adapter-conformance.md §3 / ADR 0010 — the effect protocol shapes: single-use
/// permits, synchronous adopt-or-refuse, resolution kinds, and the completion
/// message carrying continuation declarations.
/// </summary>
public sealed class EffectProtocolTests
{
    [Test]
    public void PermitTokensAreOpaqueEqualityValues()
    {
        Assert.That(SdkTestData.Permit(), Is.EqualTo(SdkTestData.Permit()));
        Assert.That(SdkTestData.Permit(nonce: 2), Is.Not.EqualTo(SdkTestData.Permit(nonce: 1)));
        Assert.That(
            new EffectPermitToken(SdkTestData.Request("r1"), new RuntimeIncarnationId("other"), 1),
            Is.Not.EqualTo(SdkTestData.Permit()),
            "a token is incarnation-scoped; stale-incarnation tokens never match");
        AssertEx.Throws<ArgumentException>(() => _ = new EffectPermitToken(
            default, SdkTestData.Incarnation, 1));
    }

    [Test]
    public void AdoptionIsAdoptedOrRefusedWithAStableCode()
    {
        Assert.That(EffectAdoption.Adopted.IsAdopted, Is.True);
        AssertEx.Throws<InvalidOperationException>(() => _ = EffectAdoption.Adopted.RefusalCode);

        var refused = EffectAdoption.Refused(new FaultCode("TargetGone"));
        Assert.That(refused.IsAdopted, Is.False);
        Assert.That(refused.RefusalCode, Is.EqualTo(new FaultCode("TargetGone")));
        AssertEx.Throws<ArgumentException>(() => EffectAdoption.Refused(default));
    }

    [Test]
    public void ResolutionsCarryExactlyTheirKindsMaterial()
    {
        var succeeded = EffectResolution.Succeeded(SdkTestData.Completion);
        Assert.That(succeeded.CompletionEvidence, Is.SameAs(SdkTestData.Completion));
        AssertEx.Throws<InvalidOperationException>(() => _ = succeeded.FaultCode);

        var faulted = EffectResolution.Faulted(new FaultCode("AppFault"));
        AssertEx.Throws<InvalidOperationException>(() => _ = faulted.CompletionEvidence);

        var cancelled = EffectResolution.Cancelled(CancellationPhase.DuringEffect, "Honored");
        Assert.That(cancelled.CancellationPhase, Is.EqualTo(CancellationPhase.DuringEffect));
        Assert.That(cancelled.CancellationDisposition, Is.EqualTo("Honored"));
    }

    [Test]
    public void AnAdoptedEffectCannotCancelBeforeEffect()
    {
        // Pre-permit cancellation never reaches the executor (kernel-execution.md §8).
        AssertEx.Throws<ArgumentException>(
            () => EffectResolution.Cancelled(CancellationPhase.BeforeEffect, "Honored"));
    }

    [Test]
    public void CompletionsBindAPermitAndOrderedContinuations()
    {
        var completion = new EffectCompletion(
            SdkTestData.Permit(),
            EffectResolution.Succeeded(SdkTestData.Completion),
            ValueArray<ContinuationRequest>.From(new[]
            {
                new ContinuationRequest(
                    SdkTestData.Capability,
                    TargetReference.ForKey(new AuthorKey("save")),
                    InvocationPayload.Empty),
            }));
        Assert.That(completion.Continuations.Count, Is.EqualTo(1));

        var withoutContinuations = new EffectCompletion(
            SdkTestData.Permit(), EffectResolution.Faulted(new FaultCode("AppFault")));
        Assert.That(withoutContinuations.Continuations, Is.EqualTo(ValueArray<ContinuationRequest>.Empty));

        AssertEx.Throws<ArgumentException>(() => _ = new EffectCompletion(
            default, EffectResolution.Faulted(new FaultCode("AppFault"))));
    }

    [Test]
    public void EffectRequestsRequireEveryComponent()
    {
        var request = new EffectRequest(
            SdkTestData.Invocation,
            InvocationPayload.Empty,
            SdkTestData.Node(1),
            SdkTestData.Permit(),
            SdkTestData.Applied);
        Assert.That(request.Profile, Is.EqualTo(SdkTestData.Applied));

        AssertEx.Throws<ArgumentException>(() => _ = new EffectRequest(
            SdkTestData.Invocation, InvocationPayload.Empty, default,
            SdkTestData.Permit(), SdkTestData.Applied));
        AssertEx.Throws<ArgumentException>(() => _ = new EffectRequest(
            SdkTestData.Invocation, InvocationPayload.Empty, SdkTestData.Node(1),
            default, SdkTestData.Applied));
    }
}
