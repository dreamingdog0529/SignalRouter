using System.Text;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>Deterministic, spec-shaped values shared by the oracle fixtures.</summary>
internal static class TestData
{
    internal static RuntimeIncarnationId Incarnation { get; } = new("incarnation-1");

    internal static ContentId Content(string seed) =>
        new("sha256", 1, DigestValue.From(Encoding.UTF8.GetBytes(seed)));

    internal static RequestId Request(string id) => new(id);

    internal static OperationId Operation(string id) => new(id);

    internal static SemanticFingerprint Fingerprint(string seed) => new($"fp-{seed}");

    internal static ArgumentDigest Arguments(string seed) => new($"args-{seed}");

    internal static CapabilityContractRef Capability { get; } =
        new(new CapabilityContractId("Invoke"), new ContractVersion(1, 0));

    internal static CompletionProfileRef CompletionProfile { get; } =
        new(new CompletionProfileId("Applied"), new ContractVersion(1, 0));

    internal static ViewContractRef RecordView { get; } =
        new(new ViewContractId("record-view"), new ContractVersion(1, 0));

    internal static ReplayComparisonProfileRef ComparisonProfile { get; } =
        new(new ReplayComparisonProfileId("strict-semantic"), new ContractVersion(1, 0));

    internal static PredicateContractRef Predicate { get; } =
        new(new PredicateContractId("std.exists"), new ContractVersion(1, 0));

    internal static NodeRef Node(ulong value) => new(Incarnation, value);

    internal static ResolvedTarget KeyedTarget(string key, ulong node = 1) =>
        new(Node(node), new AuthorKey(key));

    internal static IdentityEnvelope Envelope(Causality? causality = null) =>
        new(
            new Principal(Principal.WellKnownKinds.AgentSession, "agent-1"),
            IngressPath.Mcp,
            Provenance.Automation,
            causality ?? Causality.Root());

    internal static CapabilityInvocation Invocation(string seed) =>
        new(Capability, TargetReference.ForKey(new AuthorKey($"target-{seed}")), Arguments(seed));

    internal static CompletionEvidence Completion() =>
        new(CompletionProfile, CompletionEvidenceKind.Applied, default);

    internal static CancellationEvidence Cancellation(CancellationPhase phase) =>
        phase == CancellationPhase.BeforeEffect
            ? new CancellationEvidence(new LogicalOrder(1), new LogicalOrder(2), phase, "Honored", false, false)
            : new CancellationEvidence(new LogicalOrder(1), new LogicalOrder(2), phase, "Honored", true, true);
}
