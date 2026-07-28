using System;
using NUnit.Framework;
using SignalRouter.V2.Codec.Recording;
using SignalRouter.V2.Comparison;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Replay.Tests;

/// <summary>
/// The trust boundary and stop-point planner (recording-replay.md §6–§7) over
/// artifacts the real recording vertical wrote: provenance gates parsing,
/// limits and integrity refuse with stable codes, the contract allowlist and
/// predicate digests pin the target runtime to the recorded world, and the
/// planned stop is the earliest spec-named strict-replay stop.
/// </summary>
public sealed class ReplayPreScanTests
{
    private sealed class ResolvingEverything : ISecretReferenceResolver
    {
        public bool CanResolve(SecretReference reference) => true;

        public bool TryResolve(
            SecretReference reference, ArgumentDigest expectedDigest, out FieldValue value)
        {
            value = FieldValue.Of("resolved");
            return true;
        }
    }

    private static ReplayTrustOptions Trusted() =>
        new(ArtifactProvenance.Trusted);

    private static ReplayAllowlist AllowlistFor(byte[] artifact)
    {
        var reading = ArtifactReader.Read(artifact, ReplayArtifactWorld.Limits);
        var opened = (RecordingOpened)reading.Cuts[0];
        return new ReplayAllowlist(
            opened.CompletionBindings,
            opened.StateSourceContracts,
            ValueArray<PredicateAllowlistEntry>.From(new[]
            {
                new PredicateAllowlistEntry(
                    ReplayArtifactWorld.CountIsFive, ReplayArtifactWorld.CountIsFiveDefinition()),
            }),
            ReplayArtifactWorld.Profile());
    }

    private static byte[] HappyArtifact()
    {
        var world = new ReplayArtifactWorld();
        world.Open();
        world.SubmitAndComplete("r-1");
        world.Close();
        return world.Artifact();
    }

    [Test]
    public void AHappyArtifactPlansWithoutStops()
    {
        var artifact = HappyArtifact();
        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), secretResolver: null, Trusted());

        Assert.That(result.Refusal, Is.Null);
        Assert.That(result.Incomparability, Is.Null);
        var plan = result.Plan!;
        Assert.That(plan.Classification.Outcome.Kind, Is.EqualTo(RecordingOutcomeKind.Completed));
        Assert.That(plan.Entries.Count, Is.EqualTo(1));
        Assert.That(plan.Entries[0].Kind, Is.EqualTo(ReplayEntryKind.Completed));
        Assert.That(plan.Entries[0].Permit, Is.Not.Null);
        Assert.That(plan.Entries[0].Terminal, Is.Not.Null);
        Assert.That(plan.Stop, Is.Null);
        Assert.That(plan.Profile.Reference, Is.EqualTo(ReplayArtifactWorld.Profile().Reference));
    }

    [Test]
    public void AnUntrustedArtifactIsRefusedBeforeParsing()
    {
        var artifact = HappyArtifact();
        var refused = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null,
            new ReplayTrustOptions(ArtifactProvenance.Untrusted));
        Assert.That(refused.Refusal!.Code, Is.EqualTo(ReplayRefusalCodes.UntrustedProvenance));

        var accepted = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null,
            new ReplayTrustOptions(ArtifactProvenance.Untrusted, acceptUntrustedArtifacts: true));
        Assert.That(accepted.Plan, Is.Not.Null, "acceptance is an explicit opt-in");
    }

    [Test]
    public void ResourceAndIntegrityRefusalsCarryStableCodes()
    {
        var artifact = HappyArtifact();

        var tiny = new ArtifactReadLimits(
            maxArtifactBytes: 16,
            maxRecordCount: 4096,
            maxRecordBytes: 1024 * 1024,
            maxBlobBytes: 1024 * 1024,
            maxStringLength: 64 * 1024);
        var overBudget = ReplayPreScan.Scan(
            artifact, tiny, AllowlistFor(artifact), new ComparisonVocabulary(), null, Trusted());
        Assert.That(overBudget.Refusal!.Code, Is.EqualTo(ReplayRefusalCodes.ResourceLimit));

        var corrupted = (byte[])artifact.Clone();
        corrupted[0] ^= 0xFF;
        var integrity = ReplayPreScan.Scan(
            corrupted, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, Trusted());
        Assert.That(integrity.Refusal!.Code, Is.EqualTo(ReplayRefusalCodes.ArtifactIntegrity));
    }

    [Test]
    public void ATornArtifactStillPlansUnderItsHonestClassification()
    {
        var artifact = HappyArtifact();
        var torn = new byte[artifact.Length - 3];
        Array.Copy(artifact, torn, torn.Length);
        var result = ReplayPreScan.Scan(
            torn, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, Trusted());

        Assert.That(result.Refusal, Is.Null, "a torn tail is truncation, not refusal");
        Assert.That(result.Plan, Is.Not.Null);
        Assert.That(
            result.Plan!.Classification.Outcome.Kind,
            Is.Not.EqualTo(RecordingOutcomeKind.Completed));
    }

    [Test]
    public void AMissingCapabilityContractRefusesTheAllowlist()
    {
        var artifact = HappyArtifact();
        var reading = ArtifactReader.Read(artifact, ReplayArtifactWorld.Limits);
        var opened = (RecordingOpened)reading.Cuts[0];
        var withoutCapabilities = new ReplayAllowlist(
            ValueArray<CompletionBinding>.Empty,
            opened.StateSourceContracts,
            ValueArray<PredicateAllowlistEntry>.From(new[]
            {
                new PredicateAllowlistEntry(
                    ReplayArtifactWorld.CountIsFive, ReplayArtifactWorld.CountIsFiveDefinition()),
            }),
            ReplayArtifactWorld.Profile());

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, withoutCapabilities,
            new ComparisonVocabulary(), null, Trusted());
        Assert.That(result.Refusal!.Code, Is.EqualTo(ReplayRefusalCodes.ContractAllowlist));
    }

    [Test]
    public void APredicateDefinitionDriftRefusesTheDigest()
    {
        var world = new ReplayArtifactWorld();
        world.Open();
        world.PublishCount(5);
        world.EvaluateAssertion();
        world.Close();
        var artifact = world.Artifact();

        var reading = ArtifactReader.Read(artifact, ReplayArtifactWorld.Limits);
        var opened = (RecordingOpened)reading.Cuts[0];
        var drifted = new ReplayAllowlist(
            opened.CompletionBindings,
            opened.StateSourceContracts,
            ValueArray<PredicateAllowlistEntry>.From(new[]
            {
                new PredicateAllowlistEntry(
                    ReplayArtifactWorld.CountIsFive,
                    new PredicateDefinition(ValueArray<PredicateClause>.From(new[]
                    {
                        new PredicateClause(
                            new ClauseId("c0"),
                            new ComparisonExpression(
                                new FieldPath("sources/inventory/count"),
                                ComparisonOperator.Eq,
                                PredicateOperand.Of(6L))),
                    }))),
            }),
            ReplayArtifactWorld.Profile());

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, drifted,
            new ComparisonVocabulary(), null, Trusted());
        Assert.That(
            result.Refusal!.Code, Is.EqualTo(ReplayRefusalCodes.PredicateDigestMismatch),
            "the target runtime's definition is not the recorded one (ADR 0015)");
    }

    [Test]
    public void AProfileVersionMismatchIsIncomparableBeforeStart()
    {
        var artifact = HappyArtifact();
        var reading = ArtifactReader.Read(artifact, ReplayArtifactWorld.Limits);
        var opened = (RecordingOpened)reading.Cuts[0];
        var newerProfile = new ReplayAllowlist(
            opened.CompletionBindings,
            opened.StateSourceContracts,
            ValueArray<PredicateAllowlistEntry>.From(new[]
            {
                new PredicateAllowlistEntry(
                    ReplayArtifactWorld.CountIsFive, ReplayArtifactWorld.CountIsFiveDefinition()),
            }),
            ReplayArtifactWorld.Profile(new ContractVersion(2, 0)));

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, newerProfile,
            new ComparisonVocabulary(), null, Trusted());
        Assert.That(
            result.Incomparability,
            Is.EqualTo((IncomparableReason?)IncomparableReason.UnsupportedProfileVersion));
        Assert.That(result.Plan, Is.Null);
    }

    [Test]
    public void AContaminatedEffectStopsBeforeItsPermit()
    {
        var world = new ReplayArtifactWorld();
        world.Open();
        world.SubmitWithoutCompleting("r-1");
        world.ReportExternal("external-effect");
        world.CompletePending();
        world.Close();
        var artifact = world.Artifact();

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, Trusted());
        var plan = result.Plan!;
        Assert.That(plan.Stop, Is.Not.Null);
        Assert.That(plan.Stop!.Kind, Is.EqualTo(ReplayStopKind.Contamination));
        Assert.That(
            plan.Stop.Position, Is.EqualTo(plan.Entries[0].Permit!.Sequence),
            "the stop is before the contaminated effect's permit, not at E5 (guarantees.md §5.5)");
        Assert.That(
            plan.Stop.Incomparability,
            Is.EqualTo((IncomparableReason?)IncomparableReason.Contamination));
    }

    [Test]
    public void APositionBeyondTheBarrierIsContaminationEvenWithoutOverlap()
    {
        var world = new ReplayArtifactWorld();
        world.Open();
        world.SubmitAndComplete("r-1");
        world.ReportExternal("external-effect");
        world.SubmitAndComplete("r-2");
        world.Close();
        var artifact = world.Artifact();

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, Trusted());
        var plan = result.Plan!;
        Assert.That(plan.Stop!.Kind, Is.EqualTo(ReplayStopKind.Contamination));
        Assert.That(
            plan.Stop.Position, Is.EqualTo(plan.Entries[1].Admission.Sequence),
            "positions at or beyond the interval are incomparable (guarantees.md §3.5) — " +
            "the admission is already a compared position");
    }

    [Test]
    public void ATerminatedArtifactStopsAtItsFinalCheckpointComparison()
    {
        var world = new ReplayArtifactWorld(
            externalMutationPolicy: SignalRouter.V2.Recording.ExternalMutationPolicy.Terminate);
        world.Open();
        world.ReportExternal("external-effect");
        var artifact = world.Artifact();

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, Trusted());
        var plan = result.Plan!;
        Assert.That(
            plan.Classification.Outcome.Kind, Is.EqualTo(RecordingOutcomeKind.Incomplete));
        Assert.That(
            plan.Stop!.Kind, Is.EqualTo(ReplayStopKind.Contamination),
            "the close's final checkpoint is post-mutation comparison material");
    }

    [Test]
    public void APreCancelledEntryStaysReplayable()
    {
        var world = new ReplayArtifactWorld();
        world.Open();
        world.SubmitAndCancelBeforeEffect("r-1");
        world.Close();
        var artifact = world.Artifact();

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, Trusted());
        var plan = result.Plan!;
        Assert.That(plan.Entries.Count, Is.EqualTo(1));
        Assert.That(plan.Entries[0].Kind, Is.EqualTo(ReplayEntryKind.PreCancelled));
        Assert.That(plan.Entries[0].Permit, Is.Null);
        Assert.That(
            plan.Stop, Is.Null,
            "a BeforeEffect cancellation replays with a synthetic token (guarantees.md §5.7)");
    }

    [Test]
    public void AnInterruptedEffectPlansAnOutcomeUnknownStop()
    {
        var world = new ReplayArtifactWorld();
        world.Open();
        world.SubmitWithoutCompleting("r-1");
        world.TearDown();
        var artifact = world.Artifact();

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, Trusted());
        Assert.That(
            result.Refusal?.Code ?? result.Incomparability?.ToString(), Is.Null,
            "the scan must plan this artifact");
        var plan = result.Plan!;
        Assert.That(plan.Entries[0].Kind, Is.EqualTo(ReplayEntryKind.OutcomeUnknown));
        Assert.That(plan.Stop!.Kind, Is.EqualTo(ReplayStopKind.OutcomeUnknown));
        Assert.That(
            plan.Stop.Position, Is.EqualTo(plan.Entries[0].Permit!.Sequence),
            "strict replay stops before this effect (guarantees.md §7)");
    }

    [Test]
    public void ARecordedUnevaluableAssertionStopsWithItsVerbatimReason()
    {
        var world = new ReplayArtifactWorld();
        world.Open();
        world.EvaluateAssertion(); // the source has no document: Unevaluable(SourceUnavailable)
        world.Close();
        var artifact = world.Artifact();

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, Trusted());
        var plan = result.Plan!;
        Assert.That(plan.Stop!.Kind, Is.EqualTo(ReplayStopKind.RecordedUnevaluable));
        Assert.That(
            plan.Stop.Incomparability,
            Is.EqualTo((IncomparableReason?)new IncomparableReason("SourceUnavailable")),
            "the reason passes through verbatim (guarantees.md §3.3)");
    }

    [Test]
    public void APreEffectFaultStopsInsteadOfRedispatching()
    {
        var world = new ReplayArtifactWorld();
        world.Open();

        // Script the E3 cut append to fault: the permit's blob commits, the cut
        // does not — Faulted(EvidenceUnavailable, effectPermitted: false).
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.Committed);
        world.Store.ScriptedAnswers.Enqueue(WriteAnswer.Fault);
        world.SubmitWithoutCompleting("r-1");
        var artifact = world.Artifact();

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, Trusted());
        var plan = result.Plan!;
        Assert.That(plan.Entries[0].Kind, Is.EqualTo(ReplayEntryKind.PreEffectFault));
        Assert.That(plan.Stop!.Kind, Is.EqualTo(ReplayStopKind.PreEffectFault));
        Assert.That(
            plan.Stop.Position, Is.EqualTo(plan.Entries[0].Admission.Sequence),
            "a healthy replay environment would not fault: re-dispatch could " +
            "perform the never-permitted effect");
    }

    [Test]
    public void AStructurallyViolatingStreamIsRefused()
    {
        // A hand-written stream with an orphan permit: correctly framed and
        // digest-clean, but violating reader rule R1 — never execution input.
        var store = new MemoryArtifactStore();
        var writer = new ArtifactWriter(store.Create("malformed"));
        Assert.That(
            writer.WriteHeader("malformed", new RuntimeIncarnationId("incarnation-x")),
            Is.EqualTo(WriteAnswer.Committed));
        Assert.That(
            writer.AppendProfile(ReplayArtifactWorld.Profile()), Is.EqualTo(WriteAnswer.Committed));

        var basePayload = new byte[] { 1, 2, 3, 4 };
        var baseId = Sha256ContentId(basePayload);
        Assert.That(writer.AppendBlob(baseId, basePayload), Is.EqualTo(WriteAnswer.Committed));
        Assert.That(
            writer.AppendCut(new RecordingOpened(
                new EvidenceSequence(0),
                ReplayArtifactWorld.Profile().Reference,
                ReplayArtifactWorld.RecordView,
                new RedactionPolicyId("default-redaction"),
                ValueArray<CompletionBinding>.Empty,
                ValueArray<StateSourceBinding>.Empty,
                ValueArray<PredicateContractRef>.Empty,
                new RuntimeIncarnationId("incarnation-x"),
                baseId)),
            Is.EqualTo(WriteAnswer.Committed));
        Assert.That(
            writer.AppendCut(new EffectPermit(
                new EvidenceSequence(1),
                new RequestId("orphan"),
                new LogicalOrder(1),
                new SourceRevision(1),
                baseId,
                reusedCheckpointBlob: true)),
            Is.EqualTo(WriteAnswer.Committed));

        var artifact = store.ReadAll("malformed", ReplayArtifactWorld.Limits.MaxArtifactBytes);
        var allowlist = new ReplayAllowlist(
            ValueArray<CompletionBinding>.Empty,
            ValueArray<StateSourceBinding>.Empty,
            ValueArray<PredicateAllowlistEntry>.Empty,
            ReplayArtifactWorld.Profile());

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, allowlist,
            new ComparisonVocabulary(), null, Trusted());
        Assert.That(result.Refusal!.Code, Is.EqualTo(ReplayRefusalCodes.ArtifactIntegrity));
    }

    private static ContentId Sha256ContentId(byte[] payload)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return new ContentId("sha256", 1, DigestValue.From(sha.ComputeHash(payload)));
    }

    [Test]
    public void ATimedOutWaitPlansAWaitTimingStop()
    {
        var world = new ReplayArtifactWorld();
        world.Open();
        world.ArmWaitWithTimeout(timeoutAtLogicalTime: 500);
        world.AdvanceLogicalTime(600);
        world.Close();
        var artifact = world.Artifact();

        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, Trusted());
        var plan = result.Plan!;
        Assert.That(plan.Stop!.Kind, Is.EqualTo(ReplayStopKind.WaitTiming));
        Assert.That(plan.Stop.Incomparability, Is.Null, "timing out of tier is a plain stop");
    }

    [Test]
    public void AnUnresolvableSecretStopsBeforeTheEntry()
    {
        var world = new ReplayArtifactWorld(sensitiveArgument: true);
        world.Open();
        world.SubmitAndComplete("r-1", new InvocationPayload(ValueArray<NamedField>.From(new[]
        {
            new NamedField("token", FieldValue.Of("hunter2")),
        })));
        world.Close();
        var artifact = world.Artifact();

        var unresolvable = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), secretResolver: null, Trusted());
        var plan = unresolvable.Plan!;
        Assert.That(plan.Stop!.Kind, Is.EqualTo(ReplayStopKind.SecretUnresolvable));
        Assert.That(plan.Stop.Position, Is.EqualTo(plan.Entries[0].Admission.Sequence));

        var resolvable = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), new ResolvingEverything(), Trusted());
        Assert.That(resolvable.Plan!.Stop, Is.Null);
    }
}
