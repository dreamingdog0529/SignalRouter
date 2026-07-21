#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SignalRouter.Unity
{
    // Converts uGUI Button.onClick into a ClickCommand through the dispatcher
    // (design §17.1). Application side effects belong in the configured stages;
    // persistent listeners on the managed button bypass the command boundary and
    // fail the editor validator.
    [RequireComponent(typeof(Button))]
    [DisallowMultipleComponent]
    [AddComponentMenu("SignalRouter/Interaction Button")]
    public sealed class InteractionButton : MonoBehaviour, IInteractionTarget
    {
        [SerializeField] private InteractionRuntime? runtime;
        [SerializeField] private string targetId = string.Empty;
        [SerializeField] private string label = string.Empty;
        [SerializeField] private bool agentVisible = true;

        private Button? button;
        private StagePipeline<ClickCommand>? pipeline;
        private IInteractionTargetRegistration? registration;

        // Raised with every terminal result of a human click on this button.
        // Rejected and Faulted arrive here as structured results, not exceptions.
        public event Action<InteractionResult>? Dispatched;

        public string Id => targetId;

        public InteractionRuntime? Runtime
        {
            get => runtime;
            set
            {
                RequireUnregistered();
                runtime = value;
            }
        }

        public string TargetId
        {
            get => targetId;
            set
            {
                RequireUnregistered();
                targetId = value ?? throw new ArgumentNullException(nameof(value));
            }
        }

        public string Label
        {
            get => label;
            set
            {
                RequireUnregistered();
                label = value ?? throw new ArgumentNullException(nameof(value));
            }
        }

        public bool AgentVisible
        {
            get => agentVisible;
            set
            {
                RequireUnregistered();
                agentVisible = value;
            }
        }

        public bool IsRegistered => registration != null;

        public void ConfigurePipeline(
            IEnumerable<IInteractionStage<ClickCommand>> stages,
            Func<ClickCommand, InteractionValidation>? validator = null)
        {
            if (pipeline != null)
            {
                throw new InvalidOperationException(
                    "The click pipeline is already configured for '" + targetId + "'.");
            }

            pipeline = new StagePipeline<ClickCommand>(stages, validator);
        }

        public InteractionDescriptor Describe()
        {
            return new InteractionDescriptor(
                targetId,
                null,
                "button",
                label,
                null,
                gameObject.activeInHierarchy,
                ResolveButton().interactable,
                new[]
                {
                    new AvailableInteraction(
                        "click",
                        1,
                        ClickCommandSchema.Instance.Arguments),
                });
        }

        public bool TryGetPipeline<TCommand>(
            out IInteractionPipeline<TCommand>? resolved)
            where TCommand : struct, IInteractionCommand
        {
            if (typeof(TCommand) == typeof(ClickCommand) && pipeline != null)
            {
                resolved = (IInteractionPipeline<TCommand>)(object)pipeline;
                return true;
            }

            resolved = null;
            return false;
        }

        // For stages that change observable button state (label, interactable):
        // report the change so registry revisions stay deterministic.
        public void NotifyChanged()
        {
            RequireRuntime().Registry.NotifyDescriptorChanged(targetId);
        }

        private void OnEnable()
        {
            if (pipeline == null)
            {
                throw new InvalidOperationException(
                    "InteractionButton '" + targetId + "' must have its pipeline "
                    + "configured before it is enabled; call ConfigurePipeline while "
                    + "the object is inactive.");
            }

            RegisterTarget();
        }

        private void OnDisable()
        {
            if (button != null)
            {
                button.onClick.RemoveListener(OnClicked);
            }

            registration?.Dispose();
            registration = null;
        }

        // Registration precedes the listener: a failed registration must not
        // leave a live listener on an unregistered target.
        private void RegisterTarget()
        {
            if (registration != null)
            {
                return;
            }

            var owner = RequireRuntime();
            var control = ResolveButton();
            registration = owner.Registry.Register(this, agentVisible);
            control.onClick.AddListener(OnClicked);
        }

        private void OnClicked()
        {
            var owner = RequireRuntime();
            if (owner.IsSuppressing)
            {
                return;
            }

            owner.TrackDispatch(
                owner.Dispatcher.DispatchAsync(
                    new ClickCommand(targetId),
                    new InteractionDispatchOptions(InteractionOrigin.Human),
                    owner.LifetimeToken),
                result => Dispatched?.Invoke(result));
        }

        private InteractionRuntime RequireRuntime()
        {
            if (runtime == null)
            {
                throw new InvalidOperationException(
                    "InteractionButton '" + targetId + "' has no InteractionRuntime reference.");
            }

            return runtime;
        }

        private Button ResolveButton()
        {
            if (button == null)
            {
                button = GetComponent<Button>();
            }

            return button;
        }

        private void RequireUnregistered()
        {
            if (registration != null)
            {
                throw new InvalidOperationException(
                    "InteractionButton '" + targetId + "' is registered; its "
                    + "configuration can no longer change.");
            }
        }
    }
}
