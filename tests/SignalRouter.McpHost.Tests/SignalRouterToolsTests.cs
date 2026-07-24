using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;
using NUnit.Framework;

namespace SignalRouter.McpHost.Tests;

public sealed class SignalRouterToolsTests
{
    [Test]
    public void TheToolTypeIsAnMcpServerToolType()
    {
        Assert.That(
            typeof(SignalRouterTools).GetCustomAttribute<McpServerToolTypeAttribute>(),
            Is.Not.Null);
    }

    [TestCase("start_recording")]
    [TestCase("stop_recording")]
    [TestCase("replay_recording")]
    [TestCase("get_operation_result")]
    [TestCase("execute_interaction")]
    [TestCase("get_interaction_result")]
    [TestCase("get_ui_tree")]
    [TestCase("list_interactions")]
    [TestCase("wait_for")]
    public void TheToolIsRegisteredOnTheLiveSurface(string toolName)
    {
        var names = typeof(SignalRouterTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name != null)
            .ToArray();

        Assert.That(names, Contains.Item(toolName));
    }

    [Test]
    public void TheRecordingToolsAreExposedNotOmitted()
    {
        // Guards against the item-8d exclusion comment ever returning: the three
        // recording tools plus the reconciliation query must all be live.
        var recordingTools = typeof(SignalRouterTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name != null)
            .ToArray();

        Assert.That(recordingTools, Contains.Item("start_recording"));
        Assert.That(recordingTools, Contains.Item("stop_recording"));
        Assert.That(recordingTools, Contains.Item("replay_recording"));
        Assert.That(recordingTools, Contains.Item("get_operation_result"));
    }
}
