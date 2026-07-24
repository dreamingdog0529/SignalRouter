#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SignalRouter;
using SignalRouter.Protocol;
using SignalRouter.Protocol.Transport;
using SignalRouter.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace SignalRouter.Tests
{
    // End-to-end proof of the recording/replay supervisor over the wire (item 8d,
    // §22 criterion 5): a scripted host drives record -> execute -> stop -> replay
    // against a live runtime, and the recording round-trips through the artifact
    // file. The supervisor attaches the recorder in place (no epoch change) and
    // verifies replay on an isolated runtime built by the test factory.
    public sealed class RecordingSupervisorPlayModeTests
    {
        private SupervisorRig? rig;
        private WireHostPeer? peer;
        private string? artifactRoot;

        [SetUp]
        public void SetUp()
        {
            artifactRoot = Path.Combine(
                Application.temporaryCachePath,
                "supervisor-test-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            peer?.Dispose();
            peer = null;
            if (artifactRoot != null && Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }

        [UnityTest]
        public IEnumerator RecordExecuteStopThenReplayRunsOverTheWire()
        {
            peer = new WireHostPeer();
            rig = SupervisorRig.Create(peer.EndpointUrl, artifactRoot!);

            var scenario = Task.Run(async () =>
            {
                using var connection = await peer!.AcceptAsync().ConfigureAwait(false);
                var hello = await Handshake(connection).ConfigureAwait(false);
                var epoch = hello.SessionEpoch!;

                // Start recording — the acknowledgment arrives on the same epoch.
                await connection.SendAsync(new StartRecordingMessage(
                    peer.NextMessageId(), epoch, "op-start", "e2e")).ConfigureAwait(false);
                var started = (RecordingStartedMessage)await Expect<RecordingStartedMessage>(connection)
                    .ConfigureAwait(false);

                // Execute a click over the wire — it is recorded.
                await connection.SendAsync(new ExecuteInteractionMessage(
                    peer.NextMessageId(), epoch, "r-1", "click", 1, "menu.start", "{}"))
                    .ConfigureAwait(false);
                _ = await Expect<InteractionAcceptedMessage>(connection).ConfigureAwait(false);
                _ = await Expect<InteractionResultMessage>(connection).ConfigureAwait(false);

                // Stop recording.
                await connection.SendAsync(new StopRecordingMessage(
                    peer.NextMessageId(), epoch, "op-stop")).ConfigureAwait(false);
                var stopped = (RecordingStoppedMessage)await Expect<RecordingStoppedMessage>(connection)
                    .ConfigureAwait(false);

                // Replay the recording — isolated verification, live epoch unchanged.
                await connection.SendAsync(new ReplayRecordingMessage(
                    peer.NextMessageId(), epoch, "op-replay", stopped.RecordingHandle))
                    .ConfigureAwait(false);
                var report = (ReplayReportMessage)await Expect<ReplayReportMessage>(connection)
                    .ConfigureAwait(false);

                return (epoch, started, stopped, report);
            });

            yield return PlayModeAwait.Completion(scenario, timeoutSeconds: 40f);
            var (epoch, started, stopped, report) = scenario.Result;

            Assert.That(started.NewSessionEpoch, Is.EqualTo(epoch), "recording must not change the epoch");
            Assert.That(RecordingHandles.IsValid(started.RecordingHandle), Is.True);
            Assert.That(stopped.RecordingHandle, Is.EqualTo(started.RecordingHandle));
            Assert.That(stopped.EntryCount, Is.EqualTo(1));
            Assert.That(stopped.NewSessionEpoch, Is.EqualTo(epoch));
            Assert.That(report.OutcomeKind, Is.EqualTo(ProtocolReplayOutcomes.Completed));
            Assert.That(report.NewSessionEpoch, Is.EqualTo(epoch), "replay must not change the live epoch");
            Assert.That(rig.LiveCount, Is.EqualTo(1), "the wire click executed once on the live runtime");
            Assert.That(rig.LastReplayCount, Is.EqualTo(1), "replay re-drove the isolated runtime");

            // The recording persisted to disk with exactly the one interaction.
            var path = Path.Combine(artifactRoot!, started.RecordingHandle + ".jsonl");
            using var stream = File.OpenRead(path);
            var recording = InteractionRecordingReader.Load(stream);
            Assert.That(recording.Interactions.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator StopWithoutARecordingIsRefused()
        {
            peer = new WireHostPeer();
            rig = SupervisorRig.Create(peer.EndpointUrl, artifactRoot!);

            var scenario = Task.Run(async () =>
            {
                using var connection = await peer!.AcceptAsync().ConfigureAwait(false);
                var hello = await Handshake(connection).ConfigureAwait(false);
                await connection.SendAsync(new StopRecordingMessage(
                    peer.NextMessageId(), hello.SessionEpoch!, "op-stop")).ConfigureAwait(false);
                return (ErrorMessage)await Expect<ErrorMessage>(connection).ConfigureAwait(false);
            });

            yield return PlayModeAwait.Completion(scenario, timeoutSeconds: 20f);
            Assert.That(scenario.Result.Code, Is.EqualTo(ProtocolErrorCodes.RecordingUnavailable));
        }

        [UnityTest]
        public IEnumerator ReplayOfAnUnknownHandleIsRefused()
        {
            peer = new WireHostPeer();
            rig = SupervisorRig.Create(peer.EndpointUrl, artifactRoot!);

            var scenario = Task.Run(async () =>
            {
                using var connection = await peer!.AcceptAsync().ConfigureAwait(false);
                var hello = await Handshake(connection).ConfigureAwait(false);
                await connection.SendAsync(new ReplayRecordingMessage(
                    peer.NextMessageId(),
                    hello.SessionEpoch!,
                    "op-replay",
                    "rec-20260101000000000-deadbeef")).ConfigureAwait(false);
                return (ErrorMessage)await Expect<ErrorMessage>(connection).ConfigureAwait(false);
            });

            yield return PlayModeAwait.Completion(scenario, timeoutSeconds: 20f);
            Assert.That(scenario.Result.Code, Is.EqualTo(ProtocolErrorCodes.RecordingUnavailable));
        }

        private static async Task<HelloMessage> Handshake(WireHostPeer.Connection connection)
        {
            var hello = (HelloMessage)(await connection.ReceiveAsync().ConfigureAwait(false))!;
            await connection.SendAsync(new WelcomeMessage(
                "h-welcome",
                hello.SessionEpoch!,
                hello.MessageId,
                "SignalRouter.McpHost test",
                Array.Empty<string>(),
                ProtocolLimits.DefaultMaxReceiveMessageBytes)).ConfigureAwait(false);
            return hello;
        }

        private static async Task<ProtocolMessage> Expect<T>(WireHostPeer.Connection connection)
            where T : ProtocolMessage
        {
            var message = await connection.ReceiveAsync().ConfigureAwait(false);
            Assert.That(message, Is.InstanceOf<T>(), "unexpected message: " + message?.Type);
            return message!;
        }

        // A live runtime + bridge + supervisor + one uGUI button, plus a test
        // replay factory that reconstructs an isolated runtime at initial state.
        private sealed class SupervisorRig : IDisposable
        {
            private readonly GameObject root;
            private readonly GameObject buttonObject;
            private readonly ReplayFactory factory;

            private SupervisorRig(GameObject root, GameObject buttonObject, ReplayFactory factory)
            {
                this.root = root;
                this.buttonObject = buttonObject;
                this.factory = factory;
            }

            public InteractionRuntime Runtime { get; private set; } = null!;

            public int LiveCount { get; private set; }

            public int LastReplayCount => factory.LastCount;

            public static SupervisorRig Create(string endpointUrl, string artifactRoot)
            {
                var root = new GameObject("supervisor-rig");
                var eventSystemObject = new GameObject("event-system");
                eventSystemObject.transform.SetParent(root.transform);
                eventSystemObject.AddComponent<UnityEngine.EventSystems.EventSystem>();

                var runtimeObject = new GameObject("supervisor-runtime");
                runtimeObject.SetActive(false);
                runtimeObject.transform.SetParent(root.transform);
                var runtime = runtimeObject.AddComponent<InteractionRuntime>();
                var bridge = runtimeObject.AddComponent<InteractionRuntimeBridge>();
                var supervisor = runtimeObject.AddComponent<InteractionSessionSupervisor>();
                bridge.EndpointUrl = endpointUrl;
                bridge.ConnectOnEnable = true;
                supervisor.Configure(new InteractionSessionSupervisorOptions(artifactRoot: artifactRoot));
                var factory = new ReplayFactory();
                supervisor.SetReplayEnvironmentFactory(factory);

                var buttonObject = new GameObject("supervisor-button");
                buttonObject.SetActive(false);
                buttonObject.transform.SetParent(root.transform);
                buttonObject.AddComponent<UnityEngine.UI.Button>();
                var adapter = buttonObject.AddComponent<InteractionButton>();
                adapter.TargetId = "menu.start";
                adapter.Runtime = runtime;

                var rig = new SupervisorRig(root, buttonObject, factory);
                adapter.ConfigurePipeline(new[] { new CountingStage(() => rig.LiveCount++) });

                runtimeObject.SetActive(true);
                buttonObject.SetActive(true);
                return rig;
            }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // Builds a fresh isolated runtime seeded with the recording epoch, with its
        // own probe and stage instances and no bridge — the replay contract.
        private sealed class ReplayFactory : IInteractionReplayEnvironmentFactory
        {
            public int LastCount { get; private set; }

            public ValueTask<IInteractionReplayEnvironment> CreateAsync(
                InteractionRecording recording,
                CancellationToken cancellationToken)
            {
                var root = new GameObject("replay-env");
                var runtimeObject = new GameObject("replay-runtime");
                runtimeObject.SetActive(false);
                runtimeObject.transform.SetParent(root.transform);
                var runtime = runtimeObject.AddComponent<InteractionRuntime>();
                runtime.Initialize(new InteractionRuntimeOptions(
                    sessionEpoch: recording.Session.SessionId));
                runtimeObject.SetActive(true);

                var count = 0;
                var buttonObject = new GameObject("replay-button");
                buttonObject.SetActive(false);
                buttonObject.transform.SetParent(root.transform);
                buttonObject.AddComponent<UnityEngine.UI.Button>();
                var adapter = buttonObject.AddComponent<InteractionButton>();
                adapter.TargetId = "menu.start";
                adapter.Runtime = runtime;
                adapter.ConfigurePipeline(new[] { new CountingStage(() => count++) });
                buttonObject.SetActive(true);

                var environment = new ReplayEnvironment(root, runtime, () => LastCount = count);
                return new ValueTask<IInteractionReplayEnvironment>(environment);
            }

            private sealed class ReplayEnvironment : IInteractionReplayEnvironment
            {
                private readonly GameObject root;
                private readonly Action captureCount;

                public ReplayEnvironment(GameObject root, InteractionRuntime runtime, Action captureCount)
                {
                    this.root = root;
                    Runtime = runtime;
                    this.captureCount = captureCount;
                }

                public InteractionRuntime Runtime { get; }

                public void Dispose()
                {
                    captureCount();
                    UnityEngine.Object.Destroy(root);
                }
            }
        }

        private sealed class CountingStage : IInteractionStage<ClickCommand>
        {
            private readonly Action onExecute;

            public CountingStage(Action onExecute)
            {
                this.onExecute = onExecute;
            }

            public string Id => "click.count";

            public int Order => 0;

            public ValueTask ExecuteAsync(
                ClickCommand command,
                InteractionContext context,
                CancellationToken cancellationToken)
            {
                onExecute();
                return default;
            }
        }
    }
}
