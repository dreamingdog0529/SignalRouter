#nullable enable

using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SignalRouter.Unity.Editor
{
    public static class InteractionValidationMenu
    {
        [MenuItem("SignalRouter/Validate Active Scene")]
        public static void ValidateActiveScene()
        {
            var scene = SceneManager.GetActiveScene();
            var issues = InteractionSceneValidator.Validate(scene);
            if (issues.Count == 0)
            {
                Debug.Log(
                    "SignalRouter: no interaction validation issues in scene '"
                    + scene.name + "'.");
                return;
            }

            foreach (var issue in issues)
            {
                Debug.LogError(
                    "SignalRouter: [" + issue.Code + "] " + issue.TargetPath
                    + " - " + issue.Message);
            }
        }
    }
}
