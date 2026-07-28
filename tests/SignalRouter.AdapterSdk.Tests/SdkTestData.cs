using System;
using NUnit.Framework;
using SignalRouter.Contracts;

namespace SignalRouter.AdapterSdk.Tests;

/// <summary>Deterministic values for SDK shape tests.</summary>
internal static class SdkTestData
{
    internal static RuntimeIncarnationId Incarnation { get; } = new("incarnation-1");

    internal static RequestId Request(string id) => new(id);

    internal static NodeRef Node(ulong value) => new(Incarnation, value);

    internal static CapabilityContractRef Capability { get; } =
        new(new CapabilityContractId("Invoke"), new ContractVersion(1, 0));

    internal static CompletionProfileRef Applied { get; } =
        new(new CompletionProfileId("Applied"), new ContractVersion(1, 0));

    internal static CompletionProfileRef FrameCommitted { get; } =
        new(new CompletionProfileId("FrameCommitted"), new ContractVersion(1, 0));

    internal static CapabilityInvocation Invocation { get; } = new(
        Capability,
        TargetReference.ForKey(new AuthorKey("save")),
        new ArgumentDigest("args-1"));

    internal static IdentityEnvelope Envelope { get; } = new(
        new Principal(Principal.WellKnownKinds.AgentSession, "agent-1"),
        IngressPath.Mcp,
        Provenance.Automation,
        Causality.Root());

    internal static EffectPermitToken Permit(string request = "r1", ulong nonce = 1) =>
        new(Request(request), Incarnation, nonce);

    internal static CompletionEvidence Completion { get; } =
        new(Applied, CompletionEvidenceKind.Applied, default);
}

/// <summary>Disambiguates NUnit's Assert.Throws overloads for lambda arguments.</summary>
internal static class AssertEx
{
    internal static TException Throws<TException>(Action action)
        where TException : Exception =>
        Assert.Throws<TException>(action)!;
}
