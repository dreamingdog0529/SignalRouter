using System;
using System.Collections.Generic;
using System.Text.Json;
using SignalRouter;

namespace SignalRouter.Protocol
{
    // Host-side re-validation of a semantic-UI snapshot received from the runtime
    // (ADR 0008). The host does not trust the runtime peer: even within the negotiated
    // byte limit a snapshot can carry far more targets than the cardinality caps allow,
    // so the counts, per-field lengths, and parent graph are re-checked here against the
    // same InteractionSnapshotLimits the runtime enforces at registration and capture.
    // A violation is reported as an ArgumentException, which the reader maps to a
    // malformed-message protocol error, exactly like RequireJsonObject.
    internal static class ProtocolSnapshotBounds
    {
        public static void Validate(string json, int maxDepth, string parameterName)
        {
            if (json == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions { MaxDepth = maxDepth });
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    "The value must be a standalone JSON object within the depth budget.",
                    parameterName,
                    exception);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException(
                        "The value must be a standalone JSON object within the depth budget.",
                        parameterName);
                }

                JsonElement targets;
                if (!root.TryGetProperty("targets", out targets)
                    || targets.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                if (targets.GetArrayLength() > InteractionSnapshotLimits.MaxSnapshotTargets)
                {
                    throw Rejected(parameterName, "target count");
                }

                var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var target in targets.EnumerateArray())
                {
                    if (target.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    ValidateTarget(target, parameterName, parents);
                }

                ValidateParentGraph(parents, parameterName);
            }
        }

        private static void ValidateTarget(
            JsonElement target,
            string parameterName,
            Dictionary<string, string?> parents)
        {
            var id = RequireBounded(
                target,
                "id",
                InteractionSnapshotLimits.MaxTargetIdChars,
                parameterName);
            var parentId = RequireBounded(
                target,
                "parentId",
                InteractionSnapshotLimits.MaxTargetIdChars,
                parameterName);
            RequireBounded(target, "role", InteractionSnapshotLimits.MaxRoleChars, parameterName);
            RequireBounded(target, "label", InteractionSnapshotLimits.MaxLabelChars, parameterName);

            JsonElement value;
            if (target.TryGetProperty("value", out value)
                && value.ValueKind == JsonValueKind.Object)
            {
                RequireBounded(
                    value,
                    "value",
                    InteractionSnapshotLimits.MaxValueChars,
                    parameterName);
            }

            JsonElement interactions;
            if (target.TryGetProperty("availableInteractions", out interactions)
                && interactions.ValueKind == JsonValueKind.Array)
            {
                if (interactions.GetArrayLength()
                    > InteractionSnapshotLimits.MaxAvailableInteractionsPerTarget)
                {
                    throw Rejected(parameterName, "interaction count");
                }

                foreach (var interaction in interactions.EnumerateArray())
                {
                    if (interaction.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    ValidateInteraction(interaction, parameterName);
                }
            }

            if (id != null)
            {
                parents[id] = parentId;
            }
        }

        private static void ValidateInteraction(JsonElement interaction, string parameterName)
        {
            RequireBounded(
                interaction,
                "wireName",
                InteractionSnapshotLimits.MaxInteractionNameChars,
                parameterName);

            JsonElement arguments;
            if (!interaction.TryGetProperty("arguments", out arguments)
                || arguments.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            if (arguments.GetArrayLength() > InteractionSnapshotLimits.MaxArgumentsPerInteraction)
            {
                throw Rejected(parameterName, "argument count");
            }

            foreach (var argument in arguments.EnumerateArray())
            {
                if (argument.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                RequireBounded(
                    argument,
                    "name",
                    InteractionSnapshotLimits.MaxArgumentNameChars,
                    parameterName);
            }
        }

        // Validates the received parent links for cycles and excessive depth. An
        // unresolved parentId terminates the chain and is not an error, mirroring the
        // Core capture-side rule (ADR 0008).
        private static void ValidateParentGraph(
            Dictionary<string, string?> parents,
            string parameterName)
        {
            foreach (var startId in parents.Keys)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal) { startId };
                var current = startId;
                var depth = 0;
                while (true)
                {
                    string? parentId;
                    if (!parents.TryGetValue(current, out parentId)
                        || parentId == null
                        || !parents.ContainsKey(parentId))
                    {
                        break;
                    }

                    if (!seen.Add(parentId))
                    {
                        throw Rejected(parameterName, "parent cycle");
                    }

                    depth++;
                    if (depth > InteractionSnapshotLimits.MaxParentChainDepth)
                    {
                        throw Rejected(parameterName, "parent chain depth");
                    }

                    current = parentId;
                }
            }
        }

        private static string? RequireBounded(
            JsonElement owner,
            string property,
            int maxChars,
            string parameterName)
        {
            JsonElement element;
            if (!owner.TryGetProperty(property, out element)
                || element.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = element.GetString();
            if (value != null && value.Length > maxChars)
            {
                throw Rejected(parameterName, property + " length");
            }

            return value;
        }

        private static ArgumentException Rejected(string parameterName, string what)
        {
            // Deliberately generic: the wire-facing error must not echo snapshot content.
            return new ArgumentException(
                "The snapshot exceeds a resource bound (" + what + ").",
                parameterName);
        }
    }
}
