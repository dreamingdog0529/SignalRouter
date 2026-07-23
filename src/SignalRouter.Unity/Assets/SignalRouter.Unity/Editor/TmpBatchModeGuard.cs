using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace SignalRouter.Unity.Editor
{
    // TextMeshPro schedules a one-shot package-importer EditorWindow the first
    // time a TMP object initializes when its essential resources (fonts,
    // shaders, the TMP Settings asset) are absent. In headless batch mode that
    // window fails with "No graphic device is available to initialize the
    // view", and the delayed log — raised on the editor sync context, outside
    // any test's log scope, so LogAssert cannot suppress it — fails whichever
    // PlayMode test happens to be running. Importing the essentials stops the
    // prompt from ever being scheduled. The import is asynchronous; a
    // non-quitting Test/PlayTest launch keeps the editor alive long enough for
    // it to land on disk, after which the committed assets make this guard a
    // no-op. Runs only in batch mode.
    [InitializeOnLoad]
    internal static class TmpBatchModeGuard
    {
        static TmpBatchModeGuard()
        {
            if (!Application.isBatchMode)
            {
                return;
            }

            if (Resources.Load<TMP_Settings>("TMP Settings") != null)
            {
                return;
            }

            var package = Directory
                .GetFiles(
                    "Library/PackageCache",
                    "TMP Essential Resources.unitypackage",
                    SearchOption.AllDirectories)
                .FirstOrDefault();
            if (package == null)
            {
                return;
            }

            AssetDatabase.ImportPackage(package, interactive: false);
        }
    }
}
