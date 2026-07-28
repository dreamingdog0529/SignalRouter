using System;
using System.Security.Cryptography;
using NUnit.Framework;
using SignalRouter.V2.Codec.Recording;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Codec.Recording.Tests;

/// <summary>
/// RecordingEventSchema@1.0 (ADR 0016): every cut kind round-trips through the
/// framed writer and the bounded reader; the reader's facts drive the existing
/// EvidenceSemantics decision tables to the expected classification.
/// </summary>
public sealed class ArtifactRoundTripTests
{
    internal static readonly RuntimeIncarnationId Incarnation = new("incarnation-1");

    internal static readonly ArtifactReadLimits Limits = new(
        maxArtifactBytes: 1024 * 1024,
        maxRecordCount: 1024,
        maxRecordBytes: 256 * 1024,
        maxBlobBytes: 128 * 1024,
        maxStringLength: 4096);

    private static ContentId IdOf(byte[] payload)
    {
        using var sha = SHA256.Create();
        return new ContentId("sha256", 1, DigestValue.From(sha.ComputeHash(payload)));
    }

    private static ContractVersion V1 => new(1, 0);

    private static ReplayComparisonProfile Profile() => new(
        new ReplayComparisonProfileRef(new ReplayComparisonProfileId("strict"), V1),
        new ViewContractRef(new ViewContractId("record-standard"), V1),
        "root",
        new RedactionPolicyId("default-redaction"),
        ReplayComparisonProfile.MatchByAuthorKey,
        ValueArray<ComparedNodeRule>.From(new[]
        {
            new ComparedNodeRule("button", ValueArray<string>.From(new[] { "label", "value" })),
        }),
        ValueArray<ComparedSourceRule>.Empty,
        ValueArray<ItemKeyRule>.From(new[] { new ItemKeyRule("nodes/list/items", "key") }),
        ValueArray<CollectionRule>.From(new[]
        {
            new CollectionRule("nodes/list/items", CollectionComparison.Set),
        }),
        ValueArray<NormalizationRule>.From(new[]
        {
            new NormalizationRule("nodes/save/attributes/label", NormalizationRule.Identity),
        }),
        requireCompleteForScope: true,
        ValueArray<ExtensionPolicy>.From(new[] { new ExtensionPolicy("ext-a", mandatory: false) }),
        ValueArray<ContractVersion>.Empty);

    internal static (byte[] Bytes, byte[] BasePayload, byte[] AfterPayload) BuildCompleteArtifact(
        MemoryArtifactStore store, string artifactId = "artifact-1")
    {
        var basePayload = new byte[] { 1, 2, 3, 4 };
        var beforePayload = new byte[] { 5, 6 };
        var afterPayload = new byte[] { 7, 8, 9 };
        var finalPayload = new byte[] { 10 };
        var baseId = IdOf(basePayload);
        var beforeId = IdOf(beforePayload);
        var afterId = IdOf(afterPayload);
        var finalId = IdOf(finalPayload);

        using var writer = new ArtifactWriter(store.Create(artifactId));
        Assert.That(writer.WriteHeader(artifactId, Incarnation), Is.EqualTo(WriteAnswer.Committed));
        Assert.That(writer.AppendProfile(Profile()), Is.EqualTo(WriteAnswer.Committed));
        Assert.That(writer.AppendBlob(baseId, basePayload), Is.EqualTo(WriteAnswer.Committed));

        var capability = new CapabilityContractRef(new CapabilityContractId("Invoke"), V1);
        var recorded = new RecordedArguments(ValueArray<RecordedArgument>.From(new[]
        {
            RecordedArgument.OfSecret(
                "token",
                new SecretReference("6:Invoke@1.0/5:token"),
                new ArgumentDigest("ab12cd")),
            RecordedArgument.OfValue("value", FieldValue.Of("x")),
        }));
        var digest = InvocationCanonicalizer.DigestOf(recorded);

        Assert.That(writer.AppendCut(new RecordingOpened(
            new EvidenceSequence(0),
            Profile().Reference,
            new ViewContractRef(new ViewContractId("record-standard"), V1),
            new RedactionPolicyId("default-redaction"),
            ValueArray<CompletionBinding>.From(new[]
            {
                new CompletionBinding(
                    capability,
                    new CompletionProfileRef(new CompletionProfileId("Applied"), V1)),
            }),
            ValueArray<StateSourceBinding>.From(new[]
            {
                new StateSourceBinding(
                    new StateSourceKey("inventory"),
                    new StateSourceContractRef(new StateSourceContractId("inventory"), V1)),
            }),
            ValueArray<PredicateContractRef>.From(new[]
            {
                new PredicateContractRef(new PredicateContractId("labelExists"), V1),
            }),
            Incarnation,
            baseId)), Is.EqualTo(WriteAnswer.Committed));

        Assert.That(writer.AppendCut(new AdmissionCut(
            new EvidenceSequence(1),
            new RequestId("r-1"),
            new LogicalOrder(1),
            new SemanticFingerprint("fp-r-1"),
            new CapabilityInvocation(
                capability, TargetReference.ForKey(new AuthorKey("save")), digest),
            recorded,
            new ResolvedTarget(new NodeRef(Incarnation, 7), new AuthorKey("save")),
            new IdentityEnvelope(
                new Principal(Principal.WellKnownKinds.AgentSession, "agent-1"),
                IngressPath.Mcp,
                Provenance.Automation,
                Causality.Root()))), Is.EqualTo(WriteAnswer.Committed));

        Assert.That(writer.AppendBlob(beforeId, beforePayload), Is.EqualTo(WriteAnswer.Committed));
        Assert.That(writer.AppendCut(new EffectPermit(
            new EvidenceSequence(2),
            new RequestId("r-1"),
            new LogicalOrder(1),
            new SourceRevision(3),
            beforeId,
            reusedCheckpointBlob: false)), Is.EqualTo(WriteAnswer.Committed));

        Assert.That(writer.AppendBlob(afterId, afterPayload), Is.EqualTo(WriteAnswer.Committed));
        Assert.That(writer.AppendCut(new TerminalCut(
            new EvidenceSequence(3),
            new RequestId("r-1"),
            new LogicalOrder(1),
            InteractionOutcome.Succeeded,
            effectPermitted: true,
            afterId,
            rejectionReason: null,
            faultCode: null,
            new CompletionEvidence(
                new CompletionProfileRef(new CompletionProfileId("Applied"), V1),
                CompletionEvidenceKind.Applied,
                default),
            postcondition: null,
            cancellation: null,
            ValueArray<ContinuationCommitment>.Empty)), Is.EqualTo(WriteAnswer.Committed));

        Assert.That(writer.AppendBlob(finalId, finalPayload), Is.EqualTo(WriteAnswer.Committed));
        Assert.That(writer.AppendCut(new RecordingClosed(
            new EvidenceSequence(4),
            RecordingCloseReason.Completed,
            declaredEventCount: 5,
            finalId,
            ValueArray<ContentId>.From(new[] { baseId, beforeId, afterId, finalId }))),
            Is.EqualTo(WriteAnswer.Committed));

        return (store.ReadAll(artifactId, Limits.MaxArtifactBytes), basePayload, afterPayload);
    }

    [Test]
    public void ACompleteArtifactRoundTripsAndClassifiesCompleted()
    {
        var store = new MemoryArtifactStore();
        var (bytes, basePayload, _) = BuildCompleteArtifact(store);

        var result = ArtifactReader.Read(bytes, Limits);

        Assert.That(result.ArtifactId, Is.EqualTo("artifact-1"));
        Assert.That(result.Incarnation, Is.EqualTo(Incarnation));
        Assert.That(result.TruncatedTail, Is.False);
        Assert.That(result.IntegrityFailure, Is.False, result.IntegrityDetail);
        Assert.That(result.Cuts.Count, Is.EqualTo(5));
        Assert.That(result.Profile, Is.Not.Null);
        Assert.That(result.Profile!.Reference.Id.Value, Is.EqualTo("strict"));
        Assert.That(result.TryGetBlob(IdOf(basePayload), out var blob), Is.True);
        Assert.That(blob, Is.EqualTo(basePayload));

        var classification = EvidenceSemantics.ClassifyArtifact(result.Facts);
        Assert.That(classification.Outcome.Kind, Is.EqualTo(RecordingOutcomeKind.Completed),
            "a well-formed E1..E7 stream classifies Completed through the reader's facts");
    }

    [Test]
    public void EveryCutKindSurvivesTheRoundTripByValue()
    {
        var store = new MemoryArtifactStore();
        var (bytes, _, _) = BuildCompleteArtifact(store);
        var first = ArtifactReader.Read(bytes, Limits);

        // Re-write the decoded cuts and re-read: byte-identical value semantics.
        var second = new MemoryArtifactStore();
        using (var writer = new ArtifactWriter(second.Create("copy")))
        {
            writer.WriteHeader("copy", Incarnation);
            for (var i = 0; i < first.Cuts.Count; i++)
            {
                Assert.That(writer.AppendCut(first.Cuts[i]), Is.EqualTo(WriteAnswer.Committed));
            }
        }

        var reread = ArtifactReader.Read(
            second.ReadAll("copy", Limits.MaxArtifactBytes), Limits);
        Assert.That(reread.Cuts.Count, Is.EqualTo(first.Cuts.Count));
        for (var i = 0; i < first.Cuts.Count; i++)
        {
            Assert.That(reread.Cuts[i].Kind, Is.EqualTo(first.Cuts[i].Kind));
            Assert.That(reread.Cuts[i].Sequence, Is.EqualTo(first.Cuts[i].Sequence));
        }

        var admission = (AdmissionCut)reread.Cuts[1];
        Assert.That(admission.Arguments.Fields[0].IsSecret, Is.True);
        Assert.That(admission.Arguments.Fields[0].Secret.Value, Is.EqualTo("6:Invoke@1.0/5:token"));
        Assert.That(
            InvocationCanonicalizer.DigestOf(admission.Arguments),
            Is.EqualTo(admission.Invocation.Arguments));
    }

    [Test]
    public void TheRemainingCutKindsRoundTrip()
    {
        var store = new MemoryArtifactStore();
        using var writer = new ArtifactWriter(store.Create("cuts"));
        writer.WriteHeader("cuts", Incarnation);
        var witness = IdOf(new byte[] { 42 });
        writer.AppendBlob(witness, new byte[] { 42 });

        writer.AppendCut(new ExternalMutationBarrier(
            new EvidenceSequence(0),
            new EvidenceSequence(0),
            new EvidenceSequence(0),
            new SourceRevision(9),
            "external-source",
            ValueArray<RequestId>.From(new[] { new RequestId("r-9") })));
        writer.AppendCut(new PredicateArmed(
            new EvidenceSequence(1),
            new OperationId("w-1"),
            new PredicateContractRef(new PredicateContractId("labelExists"), V1),
            new ArgumentDigest("op-digest"),
            new SemanticFingerprint("fp-w-1"),
            new ViewContractRef(new ViewContractId("record-standard"), V1),
            "root",
            Causality.OfContinuation(new ContinuationLink(
                new RequestId("r-1"), 0, new SemanticFingerprint("fp-child"))),
            new ViewSequence(1)));
        writer.AppendCut(new PredicateResolved(
            new EvidenceSequence(2),
            new OperationId("w-1"),
            PredicateResolution.Satisfied,
            witness,
            new ViewSequence(2)));
        writer.AppendCut(new AssertionEvaluated(
            new EvidenceSequence(3),
            Incarnation,
            new SourceRevision(9),
            new ViewContractRef(new ViewContractId("record-standard"), V1),
            stateSourceTableVersion: 1,
            "root",
            new SecurityDomainId("record-domain"),
            witness,
            completeForScope: true,
            new PredicateContractRef(new PredicateContractId("labelExists"), V1),
            new ArgumentDigest("op-digest"),
            ValueArray<ClauseEvaluation>.From(new[]
            {
                new ClauseEvaluation("c0", "Saved", "Saved"),
            }),
            PredicateEvaluationOutcome.Satisfied,
            ValueArray<string>.From(new[] { "nodes/save/attributes/label" })));

        var result = ArtifactReader.Read(store.ReadAll("cuts", Limits.MaxArtifactBytes), Limits);
        Assert.That(result.IntegrityFailure, Is.False, result.IntegrityDetail);
        Assert.That(result.Cuts.Count, Is.EqualTo(4));
        var armed = (PredicateArmed)result.Cuts[1];
        Assert.That(armed.ObservationScope, Is.EqualTo("root"));
        Assert.That(armed.Causality.Kind, Is.EqualTo(CausalityKind.Continuation));
        var assertion = (AssertionEvaluated)result.Cuts[3];
        Assert.That(assertion.Outcome.Kind, Is.EqualTo(PredicateEvaluationKind.Satisfied));
        Assert.That(assertion.Clauses[0].Expected, Is.EqualTo("Saved"));
    }
}
