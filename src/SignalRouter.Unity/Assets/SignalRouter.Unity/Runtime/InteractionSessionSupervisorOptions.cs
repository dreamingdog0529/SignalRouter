#nullable enable

using System;
using UnityEngine;

namespace SignalRouter.Unity
{
    // Configuration for the recording/replay supervisor (item 8d, §25). All values
    // have runtime defaults; the artifact root defaults under persistentDataPath.
    public sealed class InteractionSessionSupervisorOptions
    {
        public const int DefaultOperationRetentionSeconds = 15 * 60;

        public InteractionSessionSupervisorOptions(
            string? artifactRoot = null,
            string? appBuild = null,
            IInteractionClock? clock = null,
            int operationRetentionSeconds = DefaultOperationRetentionSeconds)
        {
            if (operationRetentionSeconds < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(operationRetentionSeconds),
                    operationRetentionSeconds,
                    "The operation retention must be positive.");
            }

            ArtifactRoot = string.IsNullOrEmpty(artifactRoot)
                ? DefaultArtifactRoot()
                : artifactRoot!;
            AppBuild = string.IsNullOrEmpty(appBuild) ? DefaultAppBuild() : appBuild!;
            Clock = clock ?? InteractionSystemClock.Instance;
            OperationRetention = TimeSpan.FromSeconds(operationRetentionSeconds);
        }

        // Where recordings are written and read: "<root>/<handle>.jsonl".
        public string ArtifactRoot { get; }

        public string AppBuild { get; }

        public IInteractionClock Clock { get; }

        // How long a terminal operation stays in the ledger so a reconnect resend
        // or a query still finds its result. Must cover the runtime's advertised
        // recovery window.
        public TimeSpan OperationRetention { get; }

        private static string DefaultArtifactRoot()
        {
            return Application.persistentDataPath + "/SignalRouter/artifacts";
        }

        private static string DefaultAppBuild()
        {
            var version = Application.version;
            return string.IsNullOrEmpty(version) ? "runtime" : version;
        }
    }
}
