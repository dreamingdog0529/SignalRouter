#nullable enable

using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using SignalRouter.Protocol;
using SignalRouter.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace SignalRouter.Tests;

// End-to-end wire coverage for the runtime bridge: an MCP-originated click
// runs the identical command path a human uGUI click uses (MVP criterion 1),
// and a dropped connection recovers its results through the ledger after the
// bridge reconnects with the same session epoch (§21.3) without re-executing
// anything.
public sealed class WireBridgePlayModeTests
{
    [UnityTest]
    public IEnumerator WireExecuteRunsTheSameCommandPathAsAHumanClick()
    {
        using var peer = new WireHostPeer();
        using var rig = WireRig.Create(peer.EndpointUrl);

        var hostTask = Task.Run(async () =>
        {
            using var connection = await peer.AcceptAsync().ConfigureAwait(false);
            var hello = await ExpectHelloAsync(connection).ConfigureAwait(false);
            await SendWelcomeAsync(peer, connection, hello).ConfigureAwait(false);

            await connection.SendAsync(new ExecuteInteractionMessage(
                peer.NextMessageId(),
                hello.SessionEpoch!,
                "r-wire-1",
                "click",
                1,
                "menu.start",
                "{}")).ConfigureAwait(false);

            var accepted = (InteractionAcceptedMessage?)await connection.ReceiveAsync()
                .ConfigureAwait(false);
            var result = (InteractionResultMessage?)await connection.ReceiveAsync()
                .ConfigureAwait(false);
            return (Accepted: accepted!, Result: result!);
        });

        yield return PlayModeAwait.Completion(hostTask, timeoutSeconds: 30f);
        var exchange = hostTask.Result;

        Assert.That(exchange.Accepted.RequestId, Is.EqualTo("r-wire-1"));
        Assert.That(
            exchange.Result.Result.Status,
            Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(exchange.Result.Result.CommandName, Is.EqualTo("click"));
        Assert.That(rig.ClickCount, Is.EqualTo(1));

        // The identical command path, driven by uGUI instead of the wire.
        rig.ClickButton();
        yield return PlayModeAwait.Completion(rig.Runtime.WhenIdleAsync());
        Assert.That(rig.ClickCount, Is.EqualTo(2));
    }

    [UnityTest]
    public IEnumerator ADroppedConnectionRecoversItsResultAfterReconnectWithoutReExecution()
    {
        using var peer = new WireHostPeer();
        using var rig = WireRig.Create(peer.EndpointUrl);

        var hostTask = Task.Run(async () =>
        {
            using (var first = await peer.AcceptAsync().ConfigureAwait(false))
            {
                var hello = await ExpectHelloAsync(first).ConfigureAwait(false);
                await SendWelcomeAsync(peer, first, hello).ConfigureAwait(false);
                await first.SendAsync(new ExecuteInteractionMessage(
                    peer.NextMessageId(),
                    hello.SessionEpoch!,
                    "r-wire-1",
                    "click",
                    1,
                    "menu.start",
                    "{}")).ConfigureAwait(false);
                _ = await first.ReceiveAsync().ConfigureAwait(false); // accepted

                // The runtime holds side effects the host can no longer
                // confirm — exactly the §8 recovery scenario.
                first.Drop();
                var firstEpoch = hello.SessionEpoch!;

                using var second = await peer.AcceptAsync().ConfigureAwait(false);
                var rejoinHello = await ExpectHelloAsync(second).ConfigureAwait(false);
                await SendWelcomeAsync(peer, second, rejoinHello).ConfigureAwait(false);

                // Query-first recovery: poll until the terminal result is
                // retrievable from the ledger.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (true)
                {
                    await second.SendAsync(new GetInteractionResultMessage(
                        peer.NextMessageId(),
                        rejoinHello.SessionEpoch!,
                        "r-wire-1")).ConfigureAwait(false);
                    var reply = await second.ReceiveAsync().ConfigureAwait(false);
                    if (reply is InteractionResultMessage recovered)
                    {
                        return (
                            FirstEpoch: firstEpoch,
                            SecondEpoch: rejoinHello.SessionEpoch!,
                            Result: recovered);
                    }

                    if (DateTime.UtcNow > deadline)
                    {
                        throw new TimeoutException(
                            "The result never became recoverable after reconnect.");
                    }

                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
        });

        yield return PlayModeAwait.Completion(hostTask, timeoutSeconds: 60f);
        var recovery = hostTask.Result;

        Assert.That(recovery.SecondEpoch, Is.EqualTo(recovery.FirstEpoch));
        Assert.That(
            recovery.Result.Result.Status,
            Is.EqualTo(InteractionStatus.Succeeded));
        Assert.That(recovery.Result.RequestId, Is.EqualTo("r-wire-1"));
        Assert.That(rig.ClickCount, Is.EqualTo(1), "The click must not re-execute.");
    }

    private static async Task<HelloMessage> ExpectHelloAsync(WireHostPeer.Connection connection)
    {
        var message = await connection.ReceiveAsync().ConfigureAwait(false);
        Assert.That(message, Is.InstanceOf<HelloMessage>());
        return (HelloMessage)message!;
    }

    private static Task SendWelcomeAsync(
        WireHostPeer peer,
        WireHostPeer.Connection connection,
        HelloMessage hello)
    {
        return connection.SendAsync(new WelcomeMessage(
            peer.NextMessageId(),
            hello.SessionEpoch!,
            hello.MessageId,
            "SignalRouter.McpHost test",
            Array.Empty<string>(),
            ProtocolLimits.DefaultMaxReceiveMessageBytes));
    }

    // A live runtime + bridge + one uGUI button adapter, created inactive so
    // the bridge endpoint and the click pipeline are configured before
    // OnEnable auto-connects and self-registers. The counter lives in the
    // pipeline stage, so wire executions and human clicks increment the same
    // number through the identical command path.
    private sealed class WireRig : IDisposable
    {
        private readonly GameObject root;
        private readonly GameObject buttonObject;
        private int clickCount;

        private WireRig(GameObject root, GameObject buttonObject)
        {
            this.root = root;
            this.buttonObject = buttonObject;
        }

        public InteractionRuntime Runtime { get; private set; } = null!;

        public int ClickCount => clickCount;

        public static WireRig Create(string endpointUrl)
        {
            var root = new GameObject("wire-rig");
            var eventSystemObject = new GameObject("event-system");
            eventSystemObject.transform.SetParent(root.transform);
            eventSystemObject.AddComponent<UnityEngine.EventSystems.EventSystem>();

            var runtimeObject = new GameObject("wire-runtime");
            runtimeObject.SetActive(false);
            runtimeObject.transform.SetParent(root.transform);
            var runtime = runtimeObject.AddComponent<InteractionRuntime>();
            var bridge = runtimeObject.AddComponent<InteractionRuntimeBridge>();
            bridge.EndpointUrl = endpointUrl;
            bridge.ConnectOnEnable = true;

            var buttonObject = new GameObject("wire-button");
            buttonObject.SetActive(false);
            buttonObject.transform.SetParent(root.transform);
            buttonObject.AddComponent<Button>();
            var adapter = buttonObject.AddComponent<InteractionButton>();
            adapter.TargetId = "menu.start";
            adapter.Runtime = runtime;

            var rig = new WireRig(root, buttonObject) { Runtime = runtime };
            adapter.ConfigurePipeline(new[] { new CountingStage(rig) });

            runtimeObject.SetActive(true);
            buttonObject.SetActive(true);
            return rig;
        }

        public void ClickButton()
        {
            UnityEngine.EventSystems.ExecuteEvents.Execute(
                buttonObject,
                new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current),
                UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(root);
        }

        private sealed class CountingStage : IInteractionStage<ClickCommand>
        {
            private readonly WireRig owner;

            public CountingStage(WireRig owner)
            {
                this.owner = owner;
            }

            public string Id => "click.count";

            public int Order => 0;

            public System.Threading.Tasks.ValueTask ExecuteAsync(
                ClickCommand command,
                InteractionContext context,
                System.Threading.CancellationToken cancellationToken)
            {
                owner.clickCount++;
                return default;
            }
        }
    }
}
