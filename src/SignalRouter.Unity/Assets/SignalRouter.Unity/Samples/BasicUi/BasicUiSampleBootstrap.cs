#nullable enable

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SignalRouter.Unity;
using TMPro;
using UnityEngine;

namespace SignalRouter.Unity.Samples.BasicUi
{
    // The sanctioned wiring, end to end: the runtime and the managed controls are
    // authored inactive, the bootstrap initializes the runtime, configures every
    // pipeline, and only then activates the objects. All application side effects
    // live in registered stages — never in UnityEvent listeners — so human
    // clicks, agent requests, and replays run the identical path.
    public sealed class BasicUiSampleBootstrap : MonoBehaviour
    {
        [SerializeField] private InteractionRuntime? runtime;
        [SerializeField] private InteractionButton? incrementButton;
        [SerializeField] private InteractionTextInput? nameInput;
        [SerializeField] private TMP_Text? counterLabel;
        [SerializeField] private TMP_Text? greetingLabel;

        private int counter;

        private void Awake()
        {
            if (runtime == null
                || incrementButton == null
                || nameInput == null
                || counterLabel == null
                || greetingLabel == null)
            {
                throw new InvalidOperationException(
                    "The BasicUi sample bootstrap requires every scene reference to be wired.");
            }

            runtime.Initialize(new InteractionRuntimeOptions(
                additionalProbes: new IInteractionStateProbe[] { new SampleCounterProbe(this) }));
            runtime.gameObject.SetActive(true);

            incrementButton.ConfigurePipeline(new IInteractionStage<ClickCommand>[]
            {
                new IncrementCounterStage(this),
            });
            incrementButton.gameObject.SetActive(true);

            nameInput.ConfigurePipeline(new IInteractionStage<SetValueCommand>[]
            {
                new PresentGreetingStage(this),
            });
            nameInput.gameObject.SetActive(true);
        }

        private sealed class IncrementCounterStage : IInteractionStage<ClickCommand>
        {
            private readonly BasicUiSampleBootstrap owner;

            public IncrementCounterStage(BasicUiSampleBootstrap owner)
            {
                this.owner = owner;
            }

            public string Id => "click.apply-state";

            public int Order => 10;

            public ValueTask ExecuteAsync(
                ClickCommand command,
                InteractionContext context,
                CancellationToken cancellationToken)
            {
                owner.counter++;
                owner.counterLabel!.text =
                    owner.counter.ToString(CultureInfo.InvariantCulture);
                return default;
            }
        }

        private sealed class PresentGreetingStage : IInteractionStage<SetValueCommand>
        {
            private readonly BasicUiSampleBootstrap owner;

            public PresentGreetingStage(BasicUiSampleBootstrap owner)
            {
                this.owner = owner;
            }

            public string Id => "set_value.present";

            public int Order => 20;

            public ValueTask ExecuteAsync(
                SetValueCommand command,
                InteractionContext context,
                CancellationToken cancellationToken)
            {
                owner.greetingLabel!.text = "Hello, " + command.Value + "!";
                return default;
            }
        }

        private sealed class SampleCounterProbe : IInteractionStateProbe
        {
            private readonly BasicUiSampleBootstrap owner;

            public SampleCounterProbe(BasicUiSampleBootstrap owner)
            {
                this.owner = owner;
            }

            public string Id => "sample-counter";

            public int Version => 1;

            public StateProbeSnapshot Capture()
            {
                return StateProbeSnapshot.FromJson(
                    "{\"value\":" + owner.counter.ToString(CultureInfo.InvariantCulture) + "}");
            }
        }
    }
}
