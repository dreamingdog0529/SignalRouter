using System.Collections.Generic;
using NUnit.Framework;
using SignalRouter.AdapterSdk;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel.Tests;

/// <summary>
/// guarantees.md §4, the serializable tier — the same scripted input sequence
/// produces identical LogicalOrder assignment, identical terminals, and an
/// identical semantic-event sequence, run to run.
/// </summary>
public sealed class DeterminismTests
{
    private static (List<string> Answers, List<string> Trace) RunScript()
    {
        var fixture = new KernelFixture();
        var answers = new List<string>();

        var first = fixture.Submit("r1");
        var hidden = fixture.Submit("g1", targetKey: "secret");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(EffectResolution.Succeeded(
            new CompletionEvidence(KernelFixture.Applied, CompletionEvidenceKind.Applied, default)));
        fixture.PublishInventory(3);
        var second = fixture.Submit("r2");
        fixture.PumpUntilIdle();
        fixture.Executor.CompleteLast(EffectResolution.Faulted(new FaultCode("AppFault")));
        fixture.PumpUntilIdle();

        foreach (var observer in new[] { first, hidden, second })
        {
            foreach (var accepted in observer.Accepted)
            {
                answers.Add("accepted:" + accepted);
            }

            foreach (var rejection in observer.Rejected)
            {
                answers.Add("rejected:" + rejection.Request + ":" + rejection.Reason);
            }
        }

        answers.Add("q1:" + fixture.Query("r1"));
        answers.Add("q2:" + fixture.Query("r2"));
        return (answers, fixture.TraceKinds());
    }

    [Test]
    public void TheSameScriptProducesIdenticalAnswersAndEventSequences()
    {
        var firstRun = RunScript();
        var secondRun = RunScript();

        Assert.That(secondRun.Answers, Is.EqualTo(firstRun.Answers));
        Assert.That(secondRun.Trace, Is.EqualTo(firstRun.Trace));
    }
}
