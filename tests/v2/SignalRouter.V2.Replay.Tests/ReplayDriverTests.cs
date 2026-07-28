using System;
using NUnit.Framework;
using SignalRouter.V2.Codec.CanonicalState;
using SignalRouter.V2.Codec.Recording;
using SignalRouter.V2.Comparison;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;

namespace SignalRouter.V2.Replay.Tests;

/// <summary>
/// The two required end-to-end proofs (plan item 5 completion criteria;
/// recording-replay.md §6): a session recorded through the real vertical
/// replays into a factory-built isolated twin with every evidence cut
/// comparing Equal, and an injected divergence stops at the first non-Equal
/// comparison with a structured, typed diff.
/// </summary>
public sealed class ReplayDriverTests
{
    private sealed class TwinEnvironment : IReplayEnvironment
    {
        internal TwinEnvironment(KernelRuntime runtime)
        {
            Runtime = runtime;
        }

        public KernelRuntime Runtime { get; }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The test twin factory: the same bootstrap as the recording world (twin
    /// equivalence by construction), a fresh incarnation, the driver-supplied
    /// capture coordinator, and the deterministic auto-completing effect with a
    /// configurable published count — the divergence injection point.
    /// </summary>
    private sealed class TwinFactory : IReplayEnvironmentFactory
    {
        private readonly long? publishCount;

        internal TwinFactory(long? publishCount)
        {
            this.publishCount = publishCount;
        }

        public IReplayEnvironment Create(RecordingOpened opened, IEvidenceCoordinator evidence)
        {
            var runtime = ReplayArtifactWorld.BuildRuntime(
                evidence, sensitiveArgument: false, "twin-incarnation-1");
            var executor = new ReplayArtifactWorld.AutoCompletingExecutor
            {
                Runtime = runtime,
                PublishCountOnEffect = publishCount,
            };
            runtime.Start(executor);
            return new TwinEnvironment(runtime);
        }
    }

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

    private static ReplayPlan Plan(byte[] artifact)
    {
        var result = ReplayPreScan.Scan(
            artifact, ReplayArtifactWorld.Limits, AllowlistFor(artifact),
            new ComparisonVocabulary(), secretResolver: null,
            new ReplayTrustOptions(ArtifactProvenance.Trusted));
        Assert.That(result.Plan, Is.Not.Null, result.Refusal?.Code);
        return result.Plan!;
    }

    private static byte[] RichArtifact()
    {
        // The full recorded vocabulary of a replayable session: an effect that
        // publishes state, a wait satisfied by it (E6a/E6b with witness), an
        // assertion (E8), and a second effect — then an orderly close.
        var world = new ReplayArtifactWorld(autoPublishCount: 5);
        world.Open();
        world.SubmitAuto("r-pub");
        world.ArmWait();
        world.EvaluateAssertion();
        world.SubmitAuto("r-2");
        world.Close();
        return world.Artifact();
    }

    [Test]
    public void ARecordedSessionReplaysIntoTheTwinAllEqual()
    {
        var artifact = RichArtifact();
        var plan = Plan(artifact);
        Assert.That(plan.Stop, Is.Null, "a clean recording plans no stop");

        var driver = new ReplayDriver(new CanonicalStateCodec(), new ComparisonVocabulary());
        var report = driver.Execute(
            plan, AllowlistFor(artifact), new TwinFactory(publishCount: 5),
            secretResolver: null, ReplayMode.StrictSemantic);

        Assert.That(
            report.Outcome, Is.EqualTo(ReplayComparisonOutcome.Equal),
            report.DetailCode + ": " + Render(report.Diff));
        Assert.That(report.StoppedAt, Is.Null, "the whole artifact compared Equal");
    }

    [Test]
    public void AnInjectedDivergenceStopsAtTheFirstNonEqualComparisonWithATypedDiff()
    {
        var world = new ReplayArtifactWorld(autoPublishCount: 5);
        world.Open();
        world.SubmitAuto("r-pub");
        world.SubmitAuto("r-2");
        world.Close();
        var artifact = world.Artifact();
        var plan = Plan(artifact);

        // The twin's effect publishes a different count: the first divergent
        // comparison is the publishing interaction's own after state.
        var driver = new ReplayDriver(new CanonicalStateCodec(), new ComparisonVocabulary());
        var report = driver.Execute(
            plan, AllowlistFor(artifact), new TwinFactory(publishCount: 7),
            secretResolver: null, ReplayMode.StrictSemantic);

        Assert.That(
            report.Outcome, Is.EqualTo(ReplayComparisonOutcome.Diverged),
            "at " + report.StoppedAt + " detail=" + report.DetailCode + " " + Render(report.Diff));
        Assert.That(report.DetailCode, Is.EqualTo("AfterState"));
        Assert.That(
            report.StoppedAt, Is.EqualTo(plan.Entries[0].Terminal!.Sequence),
            "stop-at-first: the earlier cuts compared Equal, nothing later ran");
        var entry = report.Diff!.Entries[0];
        Assert.That(entry.Path, Is.EqualTo("sources/inventory/count"));
        Assert.That(entry.DetailCode, Is.EqualTo("ValueMismatch"));
        Assert.That(entry.Recorded, Is.EqualTo("value:5"));
        Assert.That(entry.Actual, Is.EqualTo("value:7"));
    }

    [Test]
    public void ExactArtifactModeHoldsOnASameBuildTwin()
    {
        var artifact = RichArtifact();
        var plan = Plan(artifact);
        var driver = new ReplayDriver(new CanonicalStateCodec(), new ComparisonVocabulary());
        var report = driver.Execute(
            plan, AllowlistFor(artifact), new TwinFactory(publishCount: 5),
            secretResolver: null, ReplayMode.ExactArtifact);

        Assert.That(
            report.Outcome, Is.EqualTo(ReplayComparisonOutcome.Equal),
            "same build, same effects: every canonical ContentId reproduces (§5.3)");
    }

    [Test]
    public void ADriverExecutesExactlyOnce()
    {
        var artifact = RichArtifact();
        var plan = Plan(artifact);
        var driver = new ReplayDriver(new CanonicalStateCodec(), new ComparisonVocabulary());
        driver.Execute(
            plan, AllowlistFor(artifact), new TwinFactory(publishCount: 5),
            secretResolver: null, ReplayMode.StrictSemantic);

        // Direct try/catch: the Assert.Throws lambda overloads are ambiguous
        // on this NUnit version.
        try
        {
            driver.Execute(
                plan, AllowlistFor(artifact), new TwinFactory(publishCount: 5),
                secretResolver: null, ReplayMode.StrictSemantic);
            Assert.Fail("single-flight (recording-replay.md §6): the second execution must throw");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Test]
    public void APlannedStopEndsTheRunWithItsVerdict()
    {
        // A contaminated recording: the driver replays the clean prefix and
        // ends at the planned stop with Incomparable(Contamination).
        var world = new ReplayArtifactWorld(autoPublishCount: 5);
        world.Open();
        world.SubmitAuto("r-pub");
        world.ReportExternal("external-effect");
        world.SubmitAuto("r-2");
        world.Close();
        var artifact = world.Artifact();
        var plan = Plan(artifact);
        Assert.That(plan.Stop, Is.Not.Null);

        var driver = new ReplayDriver(new CanonicalStateCodec(), new ComparisonVocabulary());
        var report = driver.Execute(
            plan, AllowlistFor(artifact), new TwinFactory(publishCount: 5),
            secretResolver: null, ReplayMode.StrictSemantic);

        Assert.That(report.StopKind, Is.EqualTo(ReplayStopKind.Contamination));
        Assert.That(
            report.Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(IncomparableReason.Contamination)));
        Assert.That(report.StoppedAt, Is.EqualTo(plan.Stop!.Position));
    }

    private static string Render(SemanticDiff? diff)
    {
        if (diff == null)
        {
            return "(no diff)";
        }

        var text = new System.Text.StringBuilder();
        foreach (var entry in diff.Entries)
        {
            text.Append(entry.Path).Append(' ').Append(entry.DetailCode)
                .Append(" recorded=").Append(entry.Recorded)
                .Append(" actual=").Append(entry.Actual).Append("; ");
        }

        return text.ToString();
    }
}
