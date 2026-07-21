using System.Linq;
using NUnit.Framework;
using SignalRouter.Unity;
using SignalRouter.Unity.Samples.BasicUi;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SignalRouter.Tests;

// The committed sample scene must stay loadable, fully wired, and clean under
// the scene validator — the sample is the integration guidance made concrete,
// so a validator finding in it would contradict the documentation.
public sealed class BasicUiSampleSceneTests
{
    private const string ScenePath =
        "Assets/SignalRouter.Unity/Samples/BasicUi/BasicUi.unity";

    private Scene scene;

    [SetUp]
    public void SetUp()
    {
        scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
    }

    [TearDown]
    public void TearDown()
    {
        if (scene.IsValid())
        {
            EditorSceneManager.CloseScene(scene, true);
        }
    }

    [Test]
    public void TheSampleSceneReportsNoValidationIssues()
    {
        Assert.That(
            SignalRouter.Unity.Editor.InteractionSceneValidator.Validate(scene),
            Is.Empty);
    }

    [Test]
    public void TheSampleSceneContainsTheWiredIntegrationObjects()
    {
        var roots = scene.GetRootGameObjects();
        var runtime = roots
            .SelectMany(root => root.GetComponentsInChildren<InteractionRuntime>(true))
            .Single();
        var button = roots
            .SelectMany(root => root.GetComponentsInChildren<InteractionButton>(true))
            .Single();
        var input = roots
            .SelectMany(root => root.GetComponentsInChildren<InteractionTextInput>(true))
            .Single();
        var bootstrap = roots
            .SelectMany(root => root.GetComponentsInChildren<BasicUiSampleBootstrap>(true))
            .Single();

        Assert.That(bootstrap.gameObject.activeSelf, Is.True);
        Assert.That(runtime.gameObject.activeSelf, Is.False);
        Assert.That(button.gameObject.activeSelf, Is.False);
        Assert.That(input.gameObject.activeSelf, Is.False);
        Assert.That(button.TargetId, Is.EqualTo("counter.increment"));
        Assert.That(input.TargetId, Is.EqualTo("greeting.name"));
        Assert.That(button.Runtime, Is.EqualTo(runtime));
        Assert.That(input.Runtime, Is.EqualTo(runtime));
    }
}
