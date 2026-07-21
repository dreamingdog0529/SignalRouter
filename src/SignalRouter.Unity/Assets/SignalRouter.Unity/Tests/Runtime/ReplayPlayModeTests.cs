using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace SignalRouter.Tests;

// design §21.3 "replay over the identical path": a session recorded through
// the runtime replays to completion on a reconstructed runtime that restores
// the recorded session epoch (normal recreation always mints a fresh epoch —
// see RuntimeLifecyclePlayModeTests). The yielding variant proves the
// dispatcher/replayer stay on the Unity main thread across genuinely
// asynchronous stages.
public sealed class ReplayPlayModeTests
{
    private PlayModeRig recordRig;
    private PlayModeRig replayRig;

    [TearDown]
    public void TearDown()
    {
        recordRig?.Dispose();
        recordRig = null;
        replayRig?.Dispose();
        replayRig = null;
    }

    [UnityTest]
    public IEnumerator ReplayRunsThroughIdenticalPath()
    {
        recordRig = PlayModeRig.Create(sessionEpoch: "playmode-replay", record: true);
        recordRig.ClickButton();
        recordRig.CommitText("hello");
        yield return PlayModeAwait.Completion(recordRig.Runtime.WhenIdleAsync());

        recordRig.Dispose();
        var recording = recordRig.LoadRecording();
        recordRig = null;
        yield return null;

        Assert.That(recording.Interactions.Count, Is.EqualTo(2));

        replayRig = PlayModeRig.Create(sessionEpoch: recording.Session.SessionId);
        var replayTask = InteractionReplayer
            .ReplayAsync(recording, replayRig.Runtime.Dispatcher)
            .AsTask();
        yield return PlayModeAwait.Completion(replayTask);

        var report = replayTask.Result;
        Assert.That(report.Outcome, Is.EqualTo(InteractionReplayOutcome.Completed));
        Assert.That(report.TotalInteractions, Is.EqualTo(2));
        Assert.That(report.VerifiedInteractions, Is.EqualTo(2));
        Assert.That(report.Divergence, Is.Null);
        Assert.That(replayRig.Counter.Value, Is.EqualTo(1));
        Assert.That(replayRig.Field.text, Is.EqualTo("hello"));
    }

    [UnityTest]
    public IEnumerator ReplayWithYieldingStagesStaysOnTheMainThread()
    {
        var mainThreadId = System.Environment.CurrentManagedThreadId;
        recordRig = PlayModeRig.Create(
            sessionEpoch: "playmode-replay-yield",
            record: true,
            yieldingClickStage: true);
        recordRig.ClickButton();
        yield return PlayModeAwait.Completion(recordRig.Runtime.WhenIdleAsync());
        recordRig.ClickButton();
        yield return PlayModeAwait.Completion(recordRig.Runtime.WhenIdleAsync());

        recordRig.Dispose();
        var recording = recordRig.LoadRecording();
        recordRig = null;
        yield return null;

        replayRig = PlayModeRig.Create(
            sessionEpoch: recording.Session.SessionId,
            yieldingClickStage: true);
        var replayTask = InteractionReplayer
            .ReplayAsync(recording, replayRig.Runtime.Dispatcher)
            .AsTask();
        yield return PlayModeAwait.Completion(replayTask);

        Assert.That(
            replayTask.Result.Outcome,
            Is.EqualTo(InteractionReplayOutcome.Completed));
        Assert.That(replayTask.Result.VerifiedInteractions, Is.EqualTo(2));
        Assert.That(replayRig.StageThreads.Count, Is.EqualTo(2));
        Assert.That(replayRig.StageThreads, Is.All.EqualTo(mainThreadId));
        Assert.That(replayRig.Counter.CaptureThreads, Is.Not.Empty);
        Assert.That(
            replayRig.Counter.CaptureThreads.ToArray(),
            Is.All.EqualTo(mainThreadId));
    }
}
