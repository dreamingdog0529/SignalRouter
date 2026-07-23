#nullable enable

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SignalRouter.Unity.Editor
{
    public enum InteractionValidationCode
    {
        // A persistent UnityEvent listener on a managed control bypasses the
        // command boundary (design §17.1): the adapter is the only sanctioned
        // listener, and application side effects belong in registered stages.
        PersistentListenerBypass = 0,
        DuplicateTargetId = 1,
        MissingTargetId = 2,
        MissingRuntimeReference = 3,
    }

    public sealed class InteractionValidationIssue
    {
        public InteractionValidationIssue(
            string targetPath,
            InteractionValidationCode code,
            string message)
        {
            TargetPath = targetPath ?? throw new ArgumentNullException(nameof(targetPath));
            Code = code;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public string TargetPath { get; }

        public InteractionValidationCode Code { get; }

        public string Message { get; }
    }

    // Editor-side integration validator (design §17.1/§21.2). Runtime-added
    // listeners cannot be attributed reliably, so this validates what the asset
    // can prove: persistent listeners, stable-ID uniqueness, and wiring.
    public static class InteractionSceneValidator
    {
        public static IReadOnlyList<InteractionValidationIssue> Validate(Scene scene)
        {
            if (!scene.IsValid())
            {
                throw new ArgumentException(
                    "The scene to validate must be a valid, loaded scene.",
                    nameof(scene));
            }

            var issues = new List<InteractionValidationIssue>();
            var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var button in root.GetComponentsInChildren<InteractionButton>(true))
                {
                    ValidateAdapter(button, button.TargetId, button.Runtime, issues, seenIds);
                    ReportPersistentListeners(
                        button,
                        button.GetComponent<Button>().onClick,
                        "Button.onClick",
                        issues);
                }

                foreach (var input in root.GetComponentsInChildren<InteractionTextInput>(true))
                {
                    ValidateAdapter(input, input.TargetId, input.Runtime, issues, seenIds);
                    var field = input.GetComponent<TMP_InputField>();
                    ReportPersistentListeners(input, field.onEndEdit, "TMP_InputField.onEndEdit", issues);
                    ReportPersistentListeners(input, field.onSubmit, "TMP_InputField.onSubmit", issues);
                    ReportPersistentListeners(input, field.onValueChanged, "TMP_InputField.onValueChanged", issues);
                }
            }

            return issues;
        }

        private static void ValidateAdapter(
            Component adapter,
            string targetId,
            InteractionRuntime? runtime,
            List<InteractionValidationIssue> issues,
            Dictionary<string, string> seenIds)
        {
            var path = PathOf(adapter.transform);
            if (string.IsNullOrEmpty(targetId))
            {
                issues.Add(new InteractionValidationIssue(
                    path,
                    InteractionValidationCode.MissingTargetId,
                    "The adapter has no stable target ID."));
            }
            else if (seenIds.TryGetValue(targetId, out var existingPath))
            {
                issues.Add(new InteractionValidationIssue(
                    path,
                    InteractionValidationCode.DuplicateTargetId,
                    "Target ID '" + targetId + "' is already used by '" + existingPath + "'."));
            }
            else
            {
                seenIds.Add(targetId, path);
            }

            if (runtime == null)
            {
                issues.Add(new InteractionValidationIssue(
                    path,
                    InteractionValidationCode.MissingRuntimeReference,
                    "The adapter has no InteractionRuntime reference."));
            }
        }

        private static void ReportPersistentListeners(
            Component adapter,
            UnityEventBase unityEvent,
            string eventName,
            List<InteractionValidationIssue> issues)
        {
            var count = unityEvent.GetPersistentEventCount();
            if (count == 0)
            {
                return;
            }

            issues.Add(new InteractionValidationIssue(
                PathOf(adapter.transform),
                InteractionValidationCode.PersistentListenerBypass,
                eventName + " has " + count + " persistent listener(s); application "
                + "side effects belong in registered stages, not UnityEvent listeners."));
        }

        private static string PathOf(Transform transform)
        {
            var path = transform.name;
            var current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }
    }
}
