using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Tck;

namespace SignalRouter.V2.ReferenceAdapter.Tests;

/// <summary>
/// The bad-harness-first rule: before the reference adapter's pass is trusted,
/// each deliberately non-conformant world must make its targeted check Fail —
/// a suite that cannot fail proves nothing.
/// </summary>
public sealed class TckSelfVerificationTests
{
    private static TckCheckResult Check(TckReport report, string checkId) =>
        report.Checks.Single(check => check.CheckId == checkId);

    [Test]
    public void DuplicateCompletionsFailTheExactlyOnceCheck()
    {
        var report = TckSuite.Run(new ReferenceTckHarnessFactory(
            executor => new BadHarnesses.DuplicatingCompletionExecutor(executor)));

        Assert.That(report.Aggregate, Is.EqualTo(TckAggregate.Failed));
        Assert.That(
            Check(report, "effect-exactly-once-completion").Status,
            Is.EqualTo(TckCheckStatus.Failed),
            "the kernel rejects the duplicate and the suite must read that rejection as a violation");
    }

    [Test]
    public void ANeverCompletingExecutorFailsTheLatencyChecks()
    {
        var factory = new ReferenceTckHarnessFactory(
            executor => new BadHarnesses.NeverCompletingExecutor(executor));
        var report = TckSuite.Run(factory);

        Assert.That(report.Aggregate, Is.EqualTo(TckAggregate.Failed));
        Assert.That(
            Check(report, "effect-exactly-once-completion").Status,
            Is.EqualTo(TckCheckStatus.Failed));
        Assert.That(
            Check(report, "completion-within-declared-frames").Status,
            Is.EqualTo(TckCheckStatus.Failed),
            "an adopted effect that never completes must violate its declared MaxFrames");
    }

    [Test]
    public void AMisclassifiedManagedInputFailsTheClassificationCheck()
    {
        var report = TckSuite.Run(new BadHarnesses.WrappingFactory(
            new ReferenceTckHarnessFactory(),
            harness => new BadHarnesses.MisclassifyingHarness(harness)));

        Assert.That(report.Aggregate, Is.EqualTo(TckAggregate.Failed));
        Assert.That(
            Check(report, "managed-input-classification").Status,
            Is.EqualTo(TckCheckStatus.Failed),
            "a Managed input that surfaces as Observed must never count as classified conformantly");
    }

    [Test]
    public void InvalidPublicationsFailTheSourcePublicationCheck()
    {
        var report = TckSuite.Run(new BadHarnesses.WrappingFactory(
            new ReferenceTckHarnessFactory(),
            harness => new BadHarnesses.InvalidPublishingHarness(harness)));

        Assert.That(report.Aggregate, Is.EqualTo(TckAggregate.Failed));
        Assert.That(
            Check(report, "source-publication-atomicity").Status,
            Is.EqualTo(TckCheckStatus.Failed),
            "contract-violating documents never swap, so the count waits must never resolve");
    }

    // ── Report aggregation is a fixture oracle of adapter-conformance.md §7.2 ──

    private static TckCheckResult Result(string id, bool required, TckCheckStatus status) =>
        new(id, "effect-protocol", required, status, null);

    [Test]
    public void AllRequiredChecksPassingAggregateToPassed()
    {
        var report = new TckReport(TckSuite.Version, ValueList<TckCheckResult>.From(new[]
        {
            Result("a", required: true, TckCheckStatus.Passed),
            Result("b", required: false, TckCheckStatus.Skipped),
        }));
        Assert.That(report.Aggregate, Is.EqualTo(TckAggregate.Passed));
    }

    [Test]
    public void ARequiredSkipAggregatesToIncompleteNeverPassed()
    {
        var report = new TckReport(TckSuite.Version, ValueList<TckCheckResult>.From(new[]
        {
            Result("a", required: true, TckCheckStatus.Passed),
            Result("b", required: true, TckCheckStatus.Skipped),
        }));
        Assert.That(report.Aggregate, Is.EqualTo(TckAggregate.Incomplete));
    }

    [Test]
    public void ARequiredFailureDominatesSkips()
    {
        var report = new TckReport(TckSuite.Version, ValueList<TckCheckResult>.From(new[]
        {
            Result("a", required: true, TckCheckStatus.Skipped),
            Result("b", required: true, TckCheckStatus.Failed),
        }));
        Assert.That(report.Aggregate, Is.EqualTo(TckAggregate.Failed));
    }
}
