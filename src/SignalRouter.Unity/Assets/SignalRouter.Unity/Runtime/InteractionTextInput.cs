#nullable enable

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SignalRouter.Unity;

// Converts committed human edits of a TMP_InputField into SetValueCommand
// (design §17.1). Dispatch happens on edit completion only (onEndEdit, which
// covers submit and focus loss); the suppression scope keeps agent/replay
// updates from re-entering as human commands. The last committed value — not
// the in-progress edit buffer — is the semantic value, so the before-state
// observation of a human commit still records the transition caused by the
// command itself.
[RequireComponent(typeof(TMP_InputField))]
[DisallowMultipleComponent]
[AddComponentMenu("SignalRouter/Interaction Text Input")]
public sealed class InteractionTextInput : MonoBehaviour, IInteractionTarget
{
    // Reserved stage order of the built-in apply stage; supplied stages must
    // use other orders (StagePipeline rejects collisions at construction).
    public const int ApplyStageOrder = 10;

    public const string ApplyStageId = "set_value.apply-state";

    [SerializeField] private InteractionRuntime? runtime;
    [SerializeField] private string targetId = string.Empty;
    [SerializeField] private string label = string.Empty;
    [SerializeField] private bool agentVisible = true;

    private TMP_InputField? field;
    private StagePipeline<SetValueCommand>? pipeline;
    private IInteractionTargetRegistration? registration;
    private string? lastCommittedValue;
    private bool pendingCommit;

    // Raised with every terminal result of a human commit on this input.
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

    // Builds the pipeline as [built-in apply stage] + additionalStages. The
    // apply stage owns order 10; application stages observe the committed
    // value at later orders.
    public void ConfigurePipeline(
        IEnumerable<IInteractionStage<SetValueCommand>>? additionalStages = null,
        Func<SetValueCommand, InteractionValidation>? validator = null)
    {
        if (pipeline != null)
        {
            throw new InvalidOperationException(
                "The set-value pipeline is already configured for '" + targetId + "'.");
        }

        var stages = new List<IInteractionStage<SetValueCommand>> { new ApplyStage(this) };
        if (additionalStages != null)
        {
            stages.AddRange(additionalStages);
        }

        pipeline = new StagePipeline<SetValueCommand>(stages, validator);
    }

    public InteractionDescriptor Describe()
    {
        var input = ResolveField();
        var committed = lastCommittedValue ?? input.text ?? string.Empty;
        return new InteractionDescriptor(
            targetId,
            null,
            "textbox",
            label,
            InteractionValue.FromString(committed),
            gameObject.activeInHierarchy,
            input.interactable,
            new[]
            {
                new AvailableInteraction(
                    "set_value",
                    1,
                    SetValueCommandSchema.Instance.Arguments),
            });
    }

    public bool TryGetPipeline<TCommand>(
        out IInteractionPipeline<TCommand>? resolved)
        where TCommand : struct, IInteractionCommand
    {
        if (typeof(TCommand) == typeof(SetValueCommand) && pipeline != null)
        {
            resolved = (IInteractionPipeline<TCommand>)(object)pipeline;
            return true;
        }

        resolved = null;
        return false;
    }

    public void NotifyChanged()
    {
        RequireRuntime().Registry.NotifyDescriptorChanged(targetId);
    }

    private void OnEnable()
    {
        if (pipeline == null)
        {
            throw new InvalidOperationException(
                "InteractionTextInput '" + targetId + "' must have its pipeline "
                + "configured before it is enabled; call ConfigurePipeline while "
                + "the object is inactive.");
        }

        RegisterTarget();
    }

    private void OnDisable()
    {
        if (field != null)
        {
            field.onEndEdit.RemoveListener(OnEndEdit);
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
        var input = ResolveField();
        lastCommittedValue = input.text ?? string.Empty;
        registration = owner.Registry.Register(this, agentVisible);
        input.onEndEdit.AddListener(OnEndEdit);
    }

    // Commit rules for onEndEdit (design §17.1): emit exactly one human
    // SetValueCommand per genuine commit, and nothing for suppression echoes,
    // teardown notifications, cancelled edits (Escape restores the previous
    // text), unchanged focus-loss commits, or re-entrant commits while one is
    // still in flight.
    private void OnEndEdit(string text)
    {
        var owner = RequireRuntime();
        if (owner.IsSuppressing)
        {
            return;
        }

        if (registration == null || !isActiveAndEnabled)
        {
            return;
        }

        var input = ResolveField();
        if (input.wasCanceled)
        {
            return;
        }

        if (pendingCommit)
        {
            return;
        }

        if (string.Equals(text, lastCommittedValue, StringComparison.Ordinal))
        {
            return;
        }

        pendingCommit = true;
        try
        {
            owner.TrackDispatch(
                owner.Dispatcher.DispatchAsync(
                    new SetValueCommand(targetId, text),
                    new InteractionDispatchOptions(InteractionOrigin.Human),
                    owner.LifetimeToken),
                result =>
                {
                    pendingCommit = false;
                    Dispatched?.Invoke(result);
                });
        }
        catch
        {
            pendingCommit = false;
            throw;
        }
    }

    private InteractionRuntime RequireRuntime()
    {
        if (runtime == null)
        {
            throw new InvalidOperationException(
                "InteractionTextInput '" + targetId + "' has no InteractionRuntime reference.");
        }

        return runtime;
    }

    private TMP_InputField ResolveField()
    {
        if (field == null)
        {
            field = GetComponent<TMP_InputField>();
        }

        return field;
    }

    private void RequireUnregistered()
    {
        if (registration != null)
        {
            throw new InvalidOperationException(
                "InteractionTextInput '" + targetId + "' is registered; its "
                + "configuration can no longer change.");
        }
    }

    // Applies the committed value to the field and the committed baseline.
    // SetTextWithoutNotify keeps even onValueChanged silent, and the
    // suppression scope is the normative guard against any cascaded uGUI
    // notification re-entering as a human command. The revision bump happens
    // here so every set_value execution moves the registry revision exactly
    // once, in both live and replay runs.
    private sealed class ApplyStage : IInteractionStage<SetValueCommand>
    {
        private readonly InteractionTextInput owner;

        public ApplyStage(InteractionTextInput owner)
        {
            this.owner = owner;
        }

        public string Id => ApplyStageId;

        public int Order => ApplyStageOrder;

        public System.Threading.Tasks.ValueTask ExecuteAsync(
            SetValueCommand command,
            InteractionContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            var runtime = owner.RequireRuntime();
            var input = owner.ResolveField();
            using (InteractionScope.Suppress(runtime))
            {
                input.SetTextWithoutNotify(command.Value);
                owner.lastCommittedValue = command.Value;
                runtime.Registry.NotifyDescriptorChanged(owner.targetId);
            }

            return default;
        }
    }
}
