using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SignalRouter.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SignalRouter.Tests;

// A programmatically built scene for the PlayMode suite: event system, an
// InteractionRuntime, one managed button, and one managed text input, wired
// in a fixed order so registry revision arithmetic is identical between a
// recording run and its replay reconstruction. ExecuteEvents drives the uGUI
// handler path directly — raycasting, layout, and input modules are out of
// scope for these tests.
internal sealed class PlayModeRig : IDisposable
{
    public const string ButtonId = "sample.increment";
    public const string InputId = "sample.name";

    private PlayModeRig()
    {
    }

    public GameObject Root { get; private set; }

    public InteractionRuntime Runtime { get; private set; }

    public InteractionButton Button { get; private set; }

    public InteractionTextInput TextInput { get; private set; }

    public TMP_InputField Field { get; private set; }

    public CounterProbe Counter { get; private set; }

    public MemoryStream Sink { get; private set; }

    public InteractionRecorder Recorder { get; private set; }

    public List<InteractionResult> ButtonResults { get; } = new();

    public List<InteractionResult> TextResults { get; } = new();

    public ConcurrentQueue<int> StageThreads { get; } = new();

    public static PlayModeRig Create(
        string sessionEpoch = null,
        bool record = false,
        bool yieldingClickStage = false,
        IEnumerable<IInteractionStage<ClickCommand>> clickStages = null)
    {
        var rig = new PlayModeRig();
        rig.Root = new GameObject("signalrouter-rig");

        var eventSystemGo = new GameObject("event-system");
        eventSystemGo.transform.SetParent(rig.Root.transform);
        eventSystemGo.AddComponent<EventSystem>();

        rig.Counter = new CounterProbe();
        if (record)
        {
            if (sessionEpoch == null)
            {
                throw new ArgumentException(
                    "A recording rig needs an explicit session epoch.",
                    nameof(sessionEpoch));
            }

            rig.Sink = new MemoryStream();
            rig.Recorder = new InteractionRecorder(
                rig.Sink,
                new InteractionRecorderOptions(sessionEpoch, "playmode"),
                leaveOpen: true);
        }

        var runtimeGo = new GameObject("runtime");
        runtimeGo.SetActive(false);
        runtimeGo.transform.SetParent(rig.Root.transform);
        rig.Runtime = runtimeGo.AddComponent<InteractionRuntime>();
        rig.Runtime.Initialize(new InteractionRuntimeOptions(
            sessionEpoch: sessionEpoch,
            recorder: rig.Recorder,
            additionalProbes: new IInteractionStateProbe[] { rig.Counter }));
        runtimeGo.SetActive(true);

        var canvasGo = new GameObject("canvas");
        canvasGo.transform.SetParent(rig.Root.transform);
        canvasGo.AddComponent<Canvas>();

        var buttonGo = new GameObject("button");
        buttonGo.SetActive(false);
        buttonGo.transform.SetParent(canvasGo.transform);
        buttonGo.AddComponent<Button>();
        rig.Button = buttonGo.AddComponent<InteractionButton>();
        rig.Button.Runtime = rig.Runtime;
        rig.Button.TargetId = ButtonId;
        rig.Button.Label = "Increment";
        rig.Button.ConfigurePipeline(
            clickStages ?? new[] { new CounterClickStage(rig, yieldingClickStage) });
        rig.Button.Dispatched += result => rig.ButtonResults.Add(result);
        buttonGo.SetActive(true);

        var inputGo = new GameObject("input");
        inputGo.SetActive(false);
        inputGo.transform.SetParent(canvasGo.transform);
        rig.Field = inputGo.AddComponent<TMP_InputField>();
        var textGo = new GameObject("text");
        textGo.transform.SetParent(inputGo.transform);
        rig.Field.textComponent = textGo.AddComponent<TextMeshProUGUI>();
        rig.TextInput = inputGo.AddComponent<InteractionTextInput>();
        rig.TextInput.Runtime = rig.Runtime;
        rig.TextInput.TargetId = InputId;
        rig.TextInput.Label = "Name";
        rig.TextInput.ConfigurePipeline();
        rig.TextInput.Dispatched += result => rig.TextResults.Add(result);
        inputGo.SetActive(true);

        return rig;
    }

    // A real uGUI pointer click through Button.OnPointerClick; only the
    // raycast/input-module front half is bypassed.
    public void ClickButton()
    {
        var pointer = new PointerEventData(EventSystem.current);
        ExecuteEvents.Execute(
            Button.gameObject,
            pointer,
            ExecuteEvents.pointerClickHandler);
    }

    // A committed human edit: the field content changes (onValueChanged-level
    // notification suppressed, as during typing the adapter ignores it anyway)
    // and the commit notification the adapter subscribes to fires.
    public void CommitText(string text)
    {
        Field.SetTextWithoutNotify(text);
        Field.onEndEdit.Invoke(text);
    }

    public InteractionRecording LoadRecording()
    {
        using var stream = new MemoryStream(Sink.ToArray());
        return InteractionRecordingReader.Load(stream);
    }

    public void Dispose()
    {
        if (Runtime != null && Runtime.IsInitialized)
        {
            Runtime.Shutdown();
        }

        if (Root != null)
        {
            UnityEngine.Object.Destroy(Root);
        }

        Recorder?.Dispose();
    }
}

// Deterministic snapshots (the counter value only); observed capture threads
// are collected on the side for main-thread assertions.
internal sealed class CounterProbe : IInteractionStateProbe
{
    private int value;

    public ConcurrentQueue<int> CaptureThreads { get; } = new();

    public string Id => "test-counter";

    public int Version => 1;

    public int Value => value;

    public void Increment()
    {
        value++;
    }

    public StateProbeSnapshot Capture()
    {
        CaptureThreads.Enqueue(Environment.CurrentManagedThreadId);
        return StateProbeSnapshot.FromJson("{\"value\":" + value + "}");
    }
}

// The default click apply stage: records the thread it starts on, mutates the
// probe-observed counter, and optionally yields off the ambient context to
// reproduce a stage that awaits real asynchronous work.
internal sealed class CounterClickStage : IInteractionStage<ClickCommand>
{
    private readonly PlayModeRig rig;
    private readonly bool yields;

    public CounterClickStage(PlayModeRig rig, bool yields)
    {
        this.rig = rig;
        this.yields = yields;
    }

    public string Id => "click.apply-state";

    public int Order => 10;

    public ValueTask ExecuteAsync(
        ClickCommand command,
        InteractionContext context,
        System.Threading.CancellationToken cancellationToken)
    {
        rig.StageThreads.Enqueue(Environment.CurrentManagedThreadId);
        rig.Counter.Increment();
        return yields ? YieldAsync(cancellationToken) : default;
    }

    private static async ValueTask YieldAsync(
        System.Threading.CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
    }
}

internal static class PlayModeAwait
{
    public static IEnumerator Until(
        Func<bool> condition,
        string description,
        float timeoutSeconds = 10f)
    {
        var deadline = Time.realtimeSinceStartup + timeoutSeconds;
        while (!condition())
        {
            if (Time.realtimeSinceStartup > deadline)
            {
                throw new TimeoutException("Timed out waiting for " + description + ".");
            }

            yield return null;
        }
    }

    public static IEnumerator Completion(Task task, float timeoutSeconds = 10f)
    {
        var deadline = Time.realtimeSinceStartup + timeoutSeconds;
        while (!task.IsCompleted)
        {
            if (Time.realtimeSinceStartup > deadline)
            {
                throw new TimeoutException("Timed out waiting for the task.");
            }

            yield return null;
        }

        // Surfaces the task's exception, if any.
        task.GetAwaiter().GetResult();
    }
}
