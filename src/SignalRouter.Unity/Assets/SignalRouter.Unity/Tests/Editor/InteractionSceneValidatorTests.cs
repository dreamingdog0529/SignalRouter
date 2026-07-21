using System.Linq;
using NUnit.Framework;
using SignalRouter.Unity;
using SignalRouter.Unity.Editor;
using TMPro;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SignalRouter.Tests;

// Editor validator coverage (design §21.2 "invalid UnityEvent listener
// detection"): persistent listeners on managed controls, duplicate and
// missing stable IDs, and missing runtime references are all reported;
// a clean scene reports nothing.
public sealed class InteractionSceneValidatorTests
{
    private EditModeUi ui;
    private InteractionRuntime runtime;

    [SetUp]
    public void SetUp()
    {
        ui = new EditModeUi();
        runtime = ui.CreateRuntime();
    }

    [TearDown]
    public void TearDown()
    {
        ui.Dispose();
    }

    [Test]
    public void ACleanSceneReportsNoIssues()
    {
        CreateWiredButton("menu.start");
        CreateWiredTextInput("profile.name");

        Assert.That(Validate(), Is.Empty);
    }

    [Test]
    public void PersistentOnClickListenersAreReported()
    {
        var button = CreateWiredButton("menu.start");
        var host = button.gameObject.AddComponent<ListenerHost>();
        UnityEventTools.AddPersistentListener(
            button.GetComponent<Button>().onClick,
            host.OnClickHandler);

        var issues = Validate();
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(
            issues[0].Code,
            Is.EqualTo(InteractionValidationCode.PersistentListenerBypass));
        Assert.That(issues[0].TargetPath, Does.Contain("menu.start"));
    }

    [Test]
    public void PersistentTextInputListenersAreReportedPerEvent()
    {
        var input = CreateWiredTextInput("profile.name");
        var host = input.gameObject.AddComponent<ListenerHost>();
        var field = input.GetComponent<TMP_InputField>();
        UnityEventTools.AddPersistentListener<string>(field.onEndEdit, host.OnTextHandler);
        UnityEventTools.AddPersistentListener<string>(field.onValueChanged, host.OnTextHandler);

        var issues = Validate();
        Assert.That(issues.Count, Is.EqualTo(2));
        Assert.That(
            issues.Select(issue => issue.Code).Distinct().Single(),
            Is.EqualTo(InteractionValidationCode.PersistentListenerBypass));
        Assert.That(
            issues.Any(issue => issue.Message.Contains("onEndEdit")),
            Is.True);
        Assert.That(
            issues.Any(issue => issue.Message.Contains("onValueChanged")),
            Is.True);
    }

    [Test]
    public void DuplicateTargetIdsAreReported()
    {
        CreateWiredButton("menu.start");
        CreateWiredButton("menu.start");

        var issues = Validate();
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(
            issues[0].Code,
            Is.EqualTo(InteractionValidationCode.DuplicateTargetId));
    }

    [Test]
    public void MissingTargetIdsAreReported()
    {
        CreateWiredButton(string.Empty);

        var issues = Validate();
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(
            issues[0].Code,
            Is.EqualTo(InteractionValidationCode.MissingTargetId));
    }

    [Test]
    public void MissingRuntimeReferencesAreReported()
    {
        var button = ui.CreateButton("menu.start");
        button.gameObject.SetActive(true);

        var issues = Validate();
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(
            issues[0].Code,
            Is.EqualTo(InteractionValidationCode.MissingRuntimeReference));
    }

    private System.Collections.Generic.IReadOnlyList<InteractionValidationIssue> Validate()
    {
        return InteractionSceneValidator.Validate(SceneManager.GetActiveScene());
    }

    private InteractionButton CreateWiredButton(string targetId)
    {
        var adapter = ui.CreateButton(targetId);
        adapter.Runtime = runtime;
        adapter.gameObject.SetActive(true);
        return adapter;
    }

    private InteractionTextInput CreateWiredTextInput(string targetId)
    {
        var adapter = ui.CreateTextInput(targetId);
        adapter.Runtime = runtime;
        adapter.gameObject.SetActive(true);
        return adapter;
    }
}

internal sealed class ListenerHost : MonoBehaviour
{
    public void OnClickHandler()
    {
    }

    public void OnTextHandler(string value)
    {
    }
}
