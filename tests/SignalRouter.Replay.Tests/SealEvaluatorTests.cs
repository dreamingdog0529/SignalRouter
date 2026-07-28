using NUnit.Framework;
using SignalRouter.AdapterSdk;
using SignalRouter.Codec.Recording;
using SignalRouter.Comparison;
using SignalRouter.Contracts;

namespace SignalRouter.Replay.Tests;

/// <summary>
/// The verification.md §5.2 seal evaluator over artifacts the real vertical
/// wrote: every condition passes on a clean recording; each failing condition
/// reports its own code, and a failed seal leaves a diagnostic recording,
/// never a CI case.
/// </summary>
public sealed class SealEvaluatorTests
{
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

    /// <summary>The manifest's evidence references: the E8 positions in the artifact.</summary>
    private static ValueArray<EvidenceSequence> E8Sequences(byte[] artifact)
    {
        var reading = ArtifactReader.Read(artifact, ReplayArtifactWorld.Limits);
        var sequences = new System.Collections.Generic.List<EvidenceSequence>();
        foreach (var cut in reading.Cuts)
        {
            if (cut is AssertionEvaluated)
            {
                sequences.Add(cut.Sequence);
            }
        }

        return ValueArray<EvidenceSequence>.From(sequences);
    }

    private static SealEvaluation Evaluate(
        byte[] artifact, ValueArray<EvidenceSequence>? required = null)
    {
        return SealEvaluator.Evaluate(
            artifact,
            ReplayArtifactWorld.Limits,
            AllowlistFor(artifact),
            new ComparisonVocabulary(),
            new ReplayTrustOptions(ArtifactProvenance.Trusted),
            required ?? ValueArray<EvidenceSequence>.Empty);
    }

    [Test]
    public void ACleanRecordingWithItsRequiredAssertionSeals()
    {
        var world = new ReplayArtifactWorld(autoPublishCount: 5);
        world.Open();
        world.SubmitAuto("r-pub");
        world.EvaluateAssertion(); // count == 5 → Satisfied E8
        world.Close();

        var artifact = world.Artifact();
        var evaluation = Evaluate(artifact, E8Sequences(artifact));
        Assert.That(evaluation.IsSealable, Is.True, evaluation.FailedCondition);
    }

    [Test]
    public void ATornArtifactFailsTheCompletedCondition()
    {
        var world = new ReplayArtifactWorld(autoPublishCount: 5);
        world.Open();
        world.SubmitAuto("r-pub");
        world.Close();
        var artifact = world.Artifact();
        var torn = new byte[artifact.Length - 3];
        System.Array.Copy(artifact, torn, torn.Length);

        var evaluation = Evaluate(torn);
        Assert.That(evaluation.IsSealable, Is.False);
        Assert.That(evaluation.FailedCondition, Is.EqualTo(SealConditions.NotCompleted));
    }

    [Test]
    public void AContaminatedRecordingFailsStrictEligibility()
    {
        var world = new ReplayArtifactWorld(autoPublishCount: 5);
        world.Open();
        world.SubmitAuto("r-pub");
        world.ReportExternal("external-effect");
        world.SubmitAuto("r-2");
        world.Close();

        var evaluation = Evaluate(world.Artifact());
        Assert.That(evaluation.IsSealable, Is.False);
        Assert.That(
            evaluation.FailedCondition, Is.EqualTo(SealConditions.StrictIneligible),
            "a case known in advance to be unreplayable is not a test (verification.md §5.2)");
    }

    [Test]
    public void AMissingRequiredAssertionFailsTheAssertionCondition()
    {
        var world = new ReplayArtifactWorld(autoPublishCount: 5);
        world.Open();
        world.SubmitAuto("r-pub");
        world.Close(); // no E8 at all

        // The manifest references an evidence position that names no
        // AssertionEvaluated cut in this artifact.
        var evaluation = Evaluate(
            world.Artifact(),
            ValueArray<EvidenceSequence>.From(new[] { new EvidenceSequence(9999) }));
        Assert.That(evaluation.IsSealable, Is.False);
        Assert.That(
            evaluation.FailedCondition,
            Is.EqualTo(SealConditions.RequiredAssertionNotSatisfied));
    }

    [Test]
    public void ARequiredAssertionThatEvaluatedFalseFailsTheAssertionCondition()
    {
        var world = new ReplayArtifactWorld(autoPublishCount: 3);
        world.Open();
        world.SubmitAuto("r-pub"); // count becomes 3
        world.EvaluateAssertion(); // count == 5 → False E8
        world.Close();

        var artifact = world.Artifact();
        var evaluation = Evaluate(artifact, E8Sequences(artifact));
        Assert.That(evaluation.IsSealable, Is.False);
        Assert.That(
            evaluation.FailedCondition,
            Is.EqualTo(SealConditions.RequiredAssertionNotSatisfied),
            "a required assertion must have evaluated Satisfied (§5.2)");
    }

    [Test]
    public void ANonRequiredFalseAssertionDoesNotBlockTheSeal()
    {
        // The manifest selects required assertions by evidence reference: a
        // False evaluation the manifest never named is diagnostic context,
        // not a seal condition.
        var world = new ReplayArtifactWorld(autoPublishCount: 5);
        world.Open();
        world.SubmitAuto("r-pub"); // count becomes 5
        world.EvaluateAssertion(); // count == 5 → Satisfied E8
        world.SubmitAuto("r-pub-2"); // count becomes 10
        world.EvaluateAssertion(); // count == 5 → False E8, not required
        world.Close();

        var artifact = world.Artifact();
        var e8s = E8Sequences(artifact);
        Assert.That(e8s.Count, Is.EqualTo(2));

        var evaluation = Evaluate(
            artifact, ValueArray<EvidenceSequence>.From(new[] { e8s[0] }));
        Assert.That(evaluation.IsSealable, Is.True, evaluation.FailedCondition);
    }

    [Test]
    public void AContractDriftFailsThePreflightCondition()
    {
        var world = new ReplayArtifactWorld(autoPublishCount: 5);
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

        var evaluation = SealEvaluator.Evaluate(
            artifact, ReplayArtifactWorld.Limits, drifted, new ComparisonVocabulary(),
            new ReplayTrustOptions(ArtifactProvenance.Trusted),
            ValueArray<EvidenceSequence>.Empty);
        Assert.That(evaluation.FailedCondition, Is.EqualTo(SealConditions.ContractPreflight));
    }

    [Test]
    public void AnUntrustedArtifactIsRefusedNotSealed()
    {
        var world = new ReplayArtifactWorld(autoPublishCount: 5);
        world.Open();
        world.Close();
        var evaluation = SealEvaluator.Evaluate(
            world.Artifact(), ReplayArtifactWorld.Limits, AllowlistFor(world.Artifact()),
            new ComparisonVocabulary(),
            new ReplayTrustOptions(ArtifactProvenance.Untrusted),
            ValueArray<EvidenceSequence>.Empty);
        Assert.That(evaluation.FailedCondition, Is.EqualTo(SealConditions.ArtifactRefused));
    }

    [Test]
    public void ASecretBearingRecordingStillSeals()
    {
        // Secrets resolve at run time (§5.2): their references never
        // disqualify sealing — and never mask a later real stop.
        var world = new ReplayArtifactWorld(sensitiveArgument: true);
        world.Open();
        world.SubmitAndComplete("r-secret", new InvocationPayload(ValueArray<NamedField>.From(new[]
        {
            new NamedField("token", FieldValue.Of("hunter2")),
        })));
        world.Close();

        var evaluation = Evaluate(world.Artifact());
        Assert.That(evaluation.IsSealable, Is.True, evaluation.FailedCondition);
    }

    [Test]
    public void ADuringEffectCancellationFailsStrictEligibilityAndPlansItsStop()
    {
        // The replay-side matrix row (guarantees.md §7): a mid-effect cancel is
        // Incomparable(CancellationTiming) at that entry — planned by the
        // pre-scan, and disqualifying for the seal.
        var world = new ReplayArtifactWorld();
        world.Open();
        world.SubmitWithoutCompleting("r-cancel");
        world.Runtime.Control.RequestCancel(new RequestId("r-cancel"));
        world.CompleteCancelled();
        world.Close();
        var artifact = world.Artifact();

        var scan = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), null, new ReplayTrustOptions(ArtifactProvenance.Trusted));
        Assert.That(scan.Plan!.Stop!.Kind, Is.EqualTo(ReplayStopKind.CancellationTiming));
        Assert.That(
            scan.Plan.Stop.Incomparability,
            Is.EqualTo((IncomparableReason?)IncomparableReason.CancellationTiming));

        Assert.That(
            Evaluate(artifact).FailedCondition, Is.EqualTo(SealConditions.StrictIneligible));
    }
}
