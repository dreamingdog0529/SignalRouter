using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using SignalRouter.Protocol.HostDiscovery;

namespace SignalRouter.McpHost;

// Owns the discovery descriptor across the host's lifetime and gates whether the
// host is willing to serve (design §19, ADR 0008). Publication happens after every
// hosted service has started — so Kestrel is already bound — and removal happens as
// shutdown begins, while the listener still holds the port.
//
// Publish and Stop are serialized under one lock, and the file I/O runs inside it on
// purpose: without that, a shutdown that observed no descriptor could complete its
// no-op delete just before a slower publish wrote the file, leaking a stale token to
// disk after the host was gone. The lock closes that window — a publish that loses the
// race to Stop is refused outright, and IsReady never opens for it.
//
// IsReady is the readiness gate the WebSocket endpoint consults: the host must not
// answer a handshake before its descriptor is published, nor once shutdown has begun.
internal sealed class HostDescriptorLifecycle : IHostedLifecycleService
{
    private readonly object gate = new();
    private readonly HostDiscoveryStore store;
    private readonly HostDiscoveryLocation location;
    private readonly string descriptorJson;
    private readonly Guid instanceId;
    private bool ready;
    private bool stopping;

    public HostDescriptorLifecycle(
        HostDiscoveryStore store,
        HostDiscoveryLocation location,
        string descriptorJson,
        Guid instanceId)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.location = location;
        this.descriptorJson = descriptorJson
            ?? throw new ArgumentNullException(nameof(descriptorJson));
        this.instanceId = instanceId;
    }

    // Whether the host has a published descriptor and shutdown has not begun.
    public bool IsReady
    {
        get
        {
            lock (gate)
            {
                return ready;
            }
        }
    }

    // Publishes the descriptor and opens the readiness gate. A failure to publish
    // propagates: the host must fail fast rather than listen without an owner-only
    // descriptor a runtime could never discover (ADR 0008: does not publish, does not
    // serve). A publish that raced in after shutdown began is skipped, leaving the
    // gate closed.
    public void Publish()
    {
        lock (gate)
        {
            if (stopping)
            {
                return;
            }

            store.Publish(location, descriptorJson);
            ready = true;
        }
    }

    // Closes the readiness gate and removes this instance's descriptor while the port
    // is still held. A successor that already republished on the same port is left
    // untouched (DeleteIfOwnedBy checks the instance id). Idempotent: it is driven
    // both by an ApplicationStopping registration (earliest) and by StoppingAsync.
    public void Stop()
    {
        lock (gate)
        {
            stopping = true;
            ready = false;
            store.DeleteIfOwnedBy(location, instanceId);
        }
    }

    Task IHostedLifecycleService.StartedAsync(CancellationToken cancellationToken)
    {
        Publish();
        return Task.CompletedTask;
    }

    Task IHostedLifecycleService.StoppingAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    Task IHostedLifecycleService.StartingAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    Task IHostedLifecycleService.StoppedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
