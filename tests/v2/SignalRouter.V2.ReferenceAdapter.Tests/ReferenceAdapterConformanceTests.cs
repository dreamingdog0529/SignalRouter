using System.Linq;
using System.Text;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Tck;

namespace SignalRouter.V2.ReferenceAdapter.Tests;

/// <summary>
/// The reference adapter under TCK 0.x Core Profile: every non-staged check
/// passes, and the aggregate is — by specification, not by accident —
/// <c>Incomplete</c>, because replay isolation and the fixture/reset contract are
/// staged to the recording and replay module (adapter-conformance.md §7.2).
/// </summary>
public sealed class ReferenceAdapterConformanceTests
{
    private static readonly string[] StagedCheckIds =
    {
        "replay-environment-isolation",
        "fixture-reset-contract",
    };

    [Test]
    public void TheReferenceAdapterPassesEveryNonStagedCheck()
    {
        var report = TckSuite.Run(new ReferenceTckHarnessFactory());

        var failures = new StringBuilder();
        foreach (var check in report.Checks)
        {
            if (check.Status == TckCheckStatus.Failed)
            {
                failures.AppendLine(check.ToString());
            }
        }

        Assert.That(failures.Length, Is.Zero, "unexpected failures:\n" + failures);
        foreach (var check in report.Checks)
        {
            if (StagedCheckIds.Contains(check.CheckId))
            {
                Assert.That(check.Status, Is.EqualTo(TckCheckStatus.Skipped), check.CheckId);
                Assert.That(check.Required, Is.True, "staged checks stay required — that is what makes the aggregate honest");
            }
            else
            {
                Assert.That(check.Status, Is.EqualTo(TckCheckStatus.Passed), check.ToString());
            }
        }

        Assert.That(
            report.Aggregate, Is.EqualTo(TckAggregate.Incomplete),
            "required skips must aggregate to Incomplete — this profile never claims SDK conformance");
    }

    // ── Adapter-specific behavior, transcribed from adapter-conformance.md ──

    private static CollectingObserver Submit(ReferenceAdapterHost host, CapabilityContractRef capability, string request)
    {
        var observer = new CollectingObserver();
        host.Runtime.Ingress.Submit(new IntentSubmission(
            new RequestId(request),
            capability,
            TargetReference.ForKey(ReferenceWorld.TargetKey),
            InvocationPayload.Empty,
            new IdentityEnvelope(
                ReferenceWorld.Agent, IngressPath.InProcessApi, Provenance.Automation, Causality.Root()),
            observer));
        return observer;
    }

    private sealed class CollectingObserver : ISubmissionObserver
    {
        internal int AcceptedCount { get; private set; }

        internal RejectionReason? LastRejection { get; private set; }

        public void OnAccepted(RequestId request) => AcceptedCount++;

        public void OnRejected(RequestId request, RejectionReason reason) => LastRejection = reason;
    }

    [Test]
    public void TheFastEffectCompletesWithinItsSingleDeclaredFrame()
    {
        var host = ReferenceAdapterHost.Create();
        try
        {
            var observer = Submit(host, ReferenceWorld.SetLabel, "fast-1");
            host.PumpHost.DriveFrames(1);
            Assert.That(observer.AcceptedCount, Is.EqualTo(1));
            Assert.That(
                host.Runtime.Queries.Query(new RequestId("fast-1"), ReferenceWorld.Agent),
                Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));
        }
        finally
        {
            host.TearDown();
        }
    }

    [Test]
    public void TheSlowEffectStaysInFlightAcrossFramesAndThenCommits()
    {
        var host = ReferenceAdapterHost.Create();
        try
        {
            Submit(host, ReferenceWorld.SlowSetLabel, "slow-1");
            host.PumpHost.DriveFrames(1);
            Assert.That(
                host.Runtime.Queries.Query(new RequestId("slow-1"), ReferenceWorld.Agent),
                Is.EqualTo(QueryAnswer.Pending),
                "the slow effect spans frames — no terminal after the adopting frame");

            host.PumpHost.DriveFrames(ReferenceEffectExecutor.SlowEffectFrames);
            Assert.That(
                host.Runtime.Queries.Query(new RequestId("slow-1"), ReferenceWorld.Agent),
                Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Succeeded)));
        }
        finally
        {
            host.TearDown();
        }
    }

    [Test]
    public void CancellingTheSlowEffectInFlightResolvesCancelledExactlyOnce()
    {
        var host = ReferenceAdapterHost.Create();
        try
        {
            Submit(host, ReferenceWorld.SlowSetLabel, "slow-cancel");
            host.PumpHost.DriveFrames(1);
            host.Runtime.Control.RequestCancel(new RequestId("slow-cancel"));
            host.PumpHost.DriveFrames(ReferenceEffectExecutor.SlowEffectFrames + 1);

            Assert.That(
                host.Runtime.Queries.Query(new RequestId("slow-cancel"), ReferenceWorld.Agent),
                Is.EqualTo(QueryAnswer.Terminal(InteractionOutcome.Cancelled)));
            var protocolRejections = host.Runtime.Trace.Snapshot().Count(semanticEvent =>
                semanticEvent.DetailCode == "CompletionRejected");
            Assert.That(
                protocolRejections, Is.Zero,
                "the matured slow path must not race a second completion after the cancel");
        }
        finally
        {
            host.TearDown();
        }
    }

    [Test]
    public void AnUnsupportedCapabilityContractIsRefusedAtAdoption()
    {
        // Bootstrap knows only the two declared capabilities, so an undeclared one
        // rejects at admission — observationally identical to a missing capability
        // (guarantees.md §3.5).
        var host = ReferenceAdapterHost.Create();
        try
        {
            var observer = Submit(
                host,
                new CapabilityContractRef(new CapabilityContractId("Undeclared"), new ContractVersion(1, 0)),
                "undeclared-1");
            host.PumpHost.DriveFrames(1);
            Assert.That(observer.LastRejection, Is.EqualTo(RejectionReason.CapabilityUnavailable));
        }
        finally
        {
            host.TearDown();
        }
    }

    [Test]
    public void TearDownIsIdempotent()
    {
        var host = ReferenceAdapterHost.Create();
        host.TearDown();
        Assert.DoesNotThrow((System.Action)host.TearDown);
    }
}
