#nullable enable

using SignalRouter.Unity;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace SignalRouter.Unity.Samples.BasicUi.Editor
{
    // Regenerates the BasicUi sample scene deterministically. The scene is a
    // committed asset; this generator exists so it is reproducible instead of
    // hand-maintained: controls come from the TMP default factories, adapters are
    // wired through their configuration properties, and the bootstrap receives
    // its references through serialized properties.
    public static class BasicUiSampleSceneGenerator
    {
        public const string ScenePath =
            "Assets/SignalRouter.Unity/Samples/BasicUi/BasicUi.unity";

        [MenuItem("SignalRouter/Regenerate BasicUi Sample Scene")]
        public static void Generate()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects,
                NewSceneMode.Single);

            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<EventSystem>();
            eventSystemGo.AddComponent<InputSystemUIInputModule>();

            // Authored inactive: the bootstrap initializes, configures pipelines,
            // and activates in a deterministic order (see BasicUiSampleBootstrap).
            var runtimeGo = new GameObject("Runtime");
            runtimeGo.SetActive(false);
            var runtime = runtimeGo.AddComponent<InteractionRuntime>();

            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            var resources = new TMP_DefaultControls.Resources();

            var buttonGo = TMP_DefaultControls.CreateButton(resources);
            buttonGo.name = "IncrementButton";
            buttonGo.transform.SetParent(canvasGo.transform, false);
            Place(buttonGo, new Vector2(0f, 90f), new Vector2(240f, 40f));
            buttonGo.GetComponentInChildren<TMP_Text>().text = "Increment";
            buttonGo.SetActive(false);
            var buttonAdapter = buttonGo.AddComponent<InteractionButton>();
            buttonAdapter.Runtime = runtime;
            buttonAdapter.TargetId = "counter.increment";
            buttonAdapter.Label = "Increment";

            var counterLabel = CreateLabel(canvasGo, "CounterLabel", "0", new Vector2(0f, 40f));

            var inputGo = TMP_DefaultControls.CreateInputField(resources);
            inputGo.name = "NameInput";
            inputGo.transform.SetParent(canvasGo.transform, false);
            Place(inputGo, new Vector2(0f, -20f), new Vector2(240f, 40f));
            inputGo.SetActive(false);
            var inputAdapter = inputGo.AddComponent<InteractionTextInput>();
            inputAdapter.Runtime = runtime;
            inputAdapter.TargetId = "greeting.name";
            inputAdapter.Label = "Name";

            var greetingLabel = CreateLabel(
                canvasGo,
                "GreetingLabel",
                "Hello, stranger!",
                new Vector2(0f, -70f));

            var bootstrapGo = new GameObject("Bootstrap");
            var bootstrap = bootstrapGo.AddComponent<BasicUiSampleBootstrap>();
            using (var serialized = new SerializedObject(bootstrap))
            {
                SetReference(serialized, "runtime", runtime);
                SetReference(serialized, "incrementButton", buttonAdapter);
                SetReference(serialized, "nameInput", inputAdapter);
                SetReference(serialized, "counterLabel", counterLabel);
                SetReference(serialized, "greetingLabel", greetingLabel);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            // Guard against Unity's silent fallback for types its script parser
            // cannot associate with a .cs asset (e.g. unsupported syntax under
            // -langversion:preview): such components save as embedded MonoScript
            // stubs and load back as missing scripts.
            RequirePersistentScript(runtime);
            RequirePersistentScript(buttonAdapter);
            RequirePersistentScript(inputAdapter);
            RequirePersistentScript(bootstrap);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                throw new System.InvalidOperationException(
                    "Saving the BasicUi sample scene to '" + ScenePath + "' failed.");
            }

            AssetDatabase.SaveAssets();
            Debug.Log("SignalRouter: regenerated " + ScenePath + ".");
        }

        private static TMP_Text CreateLabel(
            GameObject canvas,
            string name,
            string text,
            Vector2 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(canvas.transform, false);
            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = 24f;
            label.alignment = TextAlignmentOptions.Center;
            Place(go, position, new Vector2(400f, 40f));
            return label;
        }

        private static void Place(GameObject go, Vector2 position, Vector2 size)
        {
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void RequirePersistentScript(MonoBehaviour component)
        {
            var script = MonoScript.FromMonoBehaviour(component);
            if (script == null
                || !EditorUtility.IsPersistent(script)
                || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    script,
                    out var guid,
                    out long _)
                || string.IsNullOrEmpty(guid))
            {
                throw new System.InvalidOperationException(
                    component.GetType().FullName + " resolved to a transient MonoScript; "
                    + "the scene would save an embedded stub instead of a GUID reference.");
            }
        }

        private static void SetReference(
            SerializedObject serialized,
            string propertyName,
            Object value)
        {
            var property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                throw new System.InvalidOperationException(
                    "BasicUiSampleBootstrap has no serialized property '" + propertyName + "'.");
            }

            property.objectReferenceValue = value;
        }
    }
}
