using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SignalRouter.Tests;

// design §21.3 "main-thread enforcement" and "session-epoch change on runtime
// recreation" (acceptance criterion §22-8), plus the shutdown contract:
// rejected new work and lifetime-token cancellation of in-flight dispatches.
public sealed class RuntimeLifecyclePlayModeTests
{
    private PlayModeRig rig;
    private PlayModeRig secondRig;

    [TearDown]
    public void TearDown()
    {
        rig?.Dispose();
        rig = null;
        secondRig?.Dispose();
        secondRig = null;
    }

    [UnityTest]
    public IEnumerator MainThreadPolicyIsEnforced()
    {
        var mainThreadId = Environment.CurrentManagedThreadId;
        rig = PlayModeRig.Create(yieldingClickStage: true);

        // Off-thread use of main-thread members throws.
        var offThread = Task.Run<Exception>(() =>
        {
            try
            {
                rig.Runtime.RequireMainThread();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });
        yield return PlayModeAwait.Completion(offThread);
        Assert.That(offThread.Result, Is.InstanceOf<InvalidOperationException>());

        // Work posted from a background thread runs on the main thread during
        // Update — the §17.2 handoff the transports will rely on.
        var postedThreadId = 0;
        var postTask = Task.Run(() => rig.Runtime.Post(
            () => postedThreadId = Environment.CurrentManagedThreadId));
        yield return PlayModeAwait.Completion(postTask);
        yield return PlayModeAwait.Until(
            () => postedThreadId != 0,
            "the posted work to run");
        Assert.That(postedThreadId, Is.EqualTo(mainThreadId));

        // Stages start on the main thread, and the after-state capture stays
        // there even when the stage yields off the ambient context.
        rig.ClickButton();
        yield return PlayModeAwait.Completion(rig.Runtime.WhenIdleAsync());
        Assert.That(rig.ButtonResults.Single().Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(rig.StageThreads.ToArray(), Is.All.EqualTo(mainThreadId));
        Assert.That(rig.Counter.CaptureThreads, Is.Not.Empty);
        Assert.That(rig.Counter.CaptureThreads.ToArray(), Is.All.EqualTo(mainThreadId));
    }

    [UnityTest]
    public IEnumerator SessionEpochChangesOnRuntimeRecreation()
    {
        rig = PlayModeRig.Create();
        var firstEpoch = rig.Runtime.SessionEpoch;
        var firstRevision = rig.Runtime.Registry.Revision;

        rig.Dispose();
        rig = null;
        yield return null;

        secondRig = PlayModeRig.Create();
        Assert.That(secondRig.Runtime.SessionEpoch, Is.Not.EqualTo(firstEpoch));
        Assert.That(secondRig.Runtime.Registry.Revision, Is.EqualTo(firstRevision));
    }

    [UnityTest]
    public IEnumerator ShutdownRejectsNewWorkAndCancelsInFlightDispatches()
    {
        rig = PlayModeRig.Create(clickStages: new IInteractionStage<ClickCommand>[]
        {
            new BlockingStage(),
        });

        rig.ClickButton();
        Assert.That(rig.Runtime.InFlightDispatches, Is.EqualTo(1));

        rig.Runtime.Shutdown();

        // The lifetime token cancels the blocking stage; the dispatch reaches
        // a Cancelled terminal result instead of outliving the runtime.
        yield return PlayModeAwait.Until(
            () => rig.ButtonResults.Count == 1,
            "the in-flight dispatch to terminate");
        Assert.That(
            rig.ButtonResults[0].Status,
            Is.EqualTo(InteractionStatus.Cancelled));
        Assert.That(rig.Runtime.InFlightDispatches, Is.Zero);

        // New work is rejected loudly on every entry point. ExecuteEvents
        // catches handler exceptions and logs them, so the click rejection
        // surfaces as a logged exception rather than a thrown one.
        Assert.That(
            () => rig.Runtime.Post(() => { }),
            Throws.InvalidOperationException);
        LogAssert.Expect(
            LogType.Exception,
            new Regex("InvalidOperationException: The interaction runtime is shut down"));
        rig.ClickButton();
    }

    private sealed class BlockingStage : IInteractionStage<ClickCommand>
    {
        public string Id => "click.block";

        public int Order => 10;

        public async ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }
}
