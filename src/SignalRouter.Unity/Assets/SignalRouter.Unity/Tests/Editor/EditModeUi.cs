using System.Collections.Generic;
using SignalRouter.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SignalRouter.Tests;

// Builds adapter components for EditMode tests. Objects start inactive:
// without ExecuteAlways no lifecycle callbacks run in edit mode anyway, and
// inactive objects keep the fixtures honest about which paths they exercise
// (Describe, ConfigurePipeline, and manual registry registration — never the
// OnEnable self-registration, which is PlayMode behavior).
internal sealed class EditModeUi : System.IDisposable
{
    private readonly List<GameObject> created = new();

    public InteractionButton CreateButton(string targetId, string label = "")
    {
        var go = Track(new GameObject("button:" + targetId));
        go.SetActive(false);
        go.AddComponent<Button>();
        var adapter = go.AddComponent<InteractionButton>();
        adapter.TargetId = targetId;
        adapter.Label = label;
        return adapter;
    }

    public InteractionRuntime CreateRuntime()
    {
        var go = Track(new GameObject("runtime"));
        go.SetActive(false);
        return go.AddComponent<InteractionRuntime>();
    }

    public InteractionTextInput CreateTextInput(string targetId, string label = "")
    {
        var go = Track(new GameObject("input:" + targetId));
        go.SetActive(false);
        var field = go.AddComponent<TMP_InputField>();
        var textGo = Track(new GameObject("text"));
        textGo.transform.SetParent(go.transform);
        field.textComponent = textGo.AddComponent<TextMeshProUGUI>();
        var adapter = go.AddComponent<InteractionTextInput>();
        adapter.TargetId = targetId;
        adapter.Label = label;
        return adapter;
    }

    public void Dispose()
    {
        foreach (var go in created)
        {
            if (go != null)
            {
                Object.DestroyImmediate(go);
            }
        }

        created.Clear();
    }

    private GameObject Track(GameObject go)
    {
        created.Add(go);
        return go;
    }
}
