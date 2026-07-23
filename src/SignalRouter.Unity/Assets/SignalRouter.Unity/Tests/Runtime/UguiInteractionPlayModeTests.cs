using System.Collections;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace SignalRouter.Tests;

// design §21.3 / acceptance criteria §22-1 and §22-4: a real uGUI click and
// an agent-equivalent request execute the same registered stages over the
// identical dispatch path, deterministic failures report partial progress,
// and text input commits on edit completion only.
public sealed class UguiInteractionPlayModeTests
{
    private PlayModeRig rig;

    [TearDown]
    public void TearDown()
    {
        rig?.Dispose();
        rig = null;
    }

    [UnityTest]
    public IEnumerator HumanClickExecutesRegisteredStages()
    {
        rig = PlayModeRig.Create();

        rig.ClickButton();
        yield return PlayModeAwait.Completion(rig.Runtime.WhenIdleAsync());

        Assert.That(rig.ButtonResults.Count, Is.EqualTo(1));
        var result = rig.ButtonResults[0];
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(result.Origin, Is.EqualTo(InteractionOrigin.Human));
        Assert.That(result.CommandName, Is.EqualTo("click"));
        Assert.That(result.TargetId, Is.EqualTo(PlayModeRig.ButtonId));
        Assert.That(result.Stages.Stages.Count, Is.EqualTo(1));
        Assert.That(result.Stages.Stages[0].Id, Is.EqualTo("click.apply-state"));
        Assert.That(
            result.Stages.Stages[0].Status,
            Is.EqualTo(InteractionStageStatus.Completed));
        Assert.That(rig.Counter.Value, Is.EqualTo(1));
        Assert.That(
            result.Diff.Probes.Select(probe => probe.ProbeId),
            Is.EqualTo(new[] { "test-counter" }));
    }

    [UnityTest]
    public IEnumerator AgentEquivalentRequestUsesIdenticalPath()
    {
        rig = PlayModeRig.Create();

        rig.ClickButton();
        yield return PlayModeAwait.Completion(rig.Runtime.WhenIdleAsync());
        var human = rig.ButtonResults.Single();

        using var arguments = JsonDocument.Parse("{}");
        var agentTask = rig.Runtime.Catalog
            .Decode("click", 1, PlayModeRig.ButtonId, arguments.RootElement)
            .DispatchAsync(
                rig.Runtime.Dispatcher,
                new InteractionDispatchOptions(InteractionOrigin.Agent))
            .AsTask();
        yield return PlayModeAwait.Completion(agentTask);
        var agent = agentTask.Result;

        Assert.That(agent.Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(agent.Origin, Is.EqualTo(InteractionOrigin.Agent));
        Assert.That(agent.Stages, Is.EqualTo(human.Stages));
        Assert.That(
            agent.Diff.Probes.Select(probe => probe.ProbeId),
            Is.EqualTo(human.Diff.Probes.Select(probe => probe.ProbeId)));
        Assert.That(rig.Counter.Value, Is.EqualTo(2));
    }

    [UnityTest]
    public IEnumerator StageTwoFailureReportsPartialProgress()
    {
        rig = PlayModeRig.Create(clickStages: new IInteractionStage<ClickCommand>[]
        {
            new NamedStage("click.apply-state", 10),
            new NamedStage(
                "click.transition",
                20,
                new InteractionFaultException(
                    "Sample.TransitionUnavailable",
                    "The sample transition is unavailable.")),
            new NamedStage("click.sound", 30),
        });

        rig.ClickButton();
        yield return PlayModeAwait.Completion(rig.Runtime.WhenIdleAsync());

        var result = rig.ButtonResults.Single();
        Assert.That(result.Status, Is.EqualTo(InteractionStatus.Faulted));
        Assert.That(
            result.Stages.Stages.Select(stage => stage.Id),
            Is.EqualTo(new[] { "click.apply-state", "click.transition" }));
        Assert.That(
            result.Stages.Stages.Select(stage => stage.Status),
            Is.EqualTo(new[]
            {
                InteractionStageStatus.Completed,
                InteractionStageStatus.Faulted,
            }));
        Assert.That(result.Fault, Is.Not.Null);
        Assert.That(result.Fault.FailedStageId, Is.EqualTo("click.transition"));
        Assert.That(result.Fault.ApplicationCode, Is.EqualTo("Sample.TransitionUnavailable"));
        Assert.That(
            result.Fault.CompletedStageIds,
            Is.EqualTo(new[] { "click.apply-state" }));
    }

    [UnityTest]
    public IEnumerator TextInputCommitsOnEndEditOnly()
    {
        rig = PlayModeRig.Create();

        // Typing-level changes never dispatch: the adapter subscribes to the
        // commit notification only.
        rig.Field.text = "hello";
        yield return null;
        Assert.That(rig.TextResults, Is.Empty);

        // A genuine commit dispatches exactly one human command.
        rig.CommitText("hello");
        yield return PlayModeAwait.Completion(rig.Runtime.WhenIdleAsync());
        Assert.That(rig.TextResults.Count, Is.EqualTo(1));
        Assert.That(rig.TextResults[0].Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(rig.TextResults[0].Origin, Is.EqualTo(InteractionOrigin.Human));
        Assert.That(
            rig.TextInput.Describe().Value,
            Is.EqualTo(InteractionValue.FromString("hello")));

        // Focus loss without a change (an unchanged re-commit) emits nothing.
        rig.CommitText("hello");
        yield return null;
        Assert.That(rig.TextResults.Count, Is.EqualTo(1));

        // An agent update runs through the identical path, updates the field
        // under suppression, and generates no human command.
        using var arguments = JsonDocument.Parse("{\"value\":\"world\"}");
        var agentTask = rig.Runtime.Catalog
            .Decode("set_value", 1, PlayModeRig.InputId, arguments.RootElement)
            .DispatchAsync(
                rig.Runtime.Dispatcher,
                new InteractionDispatchOptions(InteractionOrigin.Agent))
            .AsTask();
        yield return PlayModeAwait.Completion(agentTask);
        Assert.That(agentTask.Result.Status, Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(rig.Field.text, Is.EqualTo("world"));
        Assert.That(rig.TextResults.Count, Is.EqualTo(1));

        // The focus-loss echo after the agent apply emits nothing either:
        // the committed baseline already advanced to the agent's value.
        rig.CommitText("world");
        yield return null;
        Assert.That(rig.TextResults.Count, Is.EqualTo(1));

        // Exactly two set_value executions reached the pipeline in total —
        // one human, one agent.
        Assert.That(agentTask.Result.Sequence, Is.GreaterThan(rig.TextResults[0].Sequence));
    }

    private sealed class NamedStage : IInteractionStage<ClickCommand>
    {
        private readonly System.Exception fault;

        public NamedStage(string id, int order, System.Exception fault = null)
        {
            Id = id;
            Order = order;
            this.fault = fault;
        }

        public string Id { get; }

        public int Order { get; }

        public ValueTask ExecuteAsync(
            ClickCommand command,
            InteractionContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            if (fault != null)
            {
                throw fault;
            }

            return default;
        }
    }
}
