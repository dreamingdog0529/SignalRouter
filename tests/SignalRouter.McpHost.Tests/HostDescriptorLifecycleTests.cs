using System;
using System.IO;
using NUnit.Framework;
using SignalRouter.Protocol.HostDiscovery;

namespace SignalRouter.McpHost.Tests;

// Deterministic tests for the publish/stop state machine. The Kestrel-integrated
// readiness gate (a connection before publication is refused) is proven by the
// runtime PlayMode tests; here the concern is the ordering guarantees the endpoint
// depends on (ADR 0008). Runs on both CI operating systems.
public sealed class HostDescriptorLifecycleTests
{
    private const int Port = 8017;

    private const string Token =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private string root = string.Empty;
    private HostDiscoveryLocation location;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "signalrouter-lifecycle-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "hosts");
        location = new HostDiscoveryLocation(
            directory,
            Path.Combine(directory, "host-" + Port + ".json"));
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Test]
    public void PublishOpensTheGateAndWritesTheDescriptor()
    {
        var instanceId = Guid.NewGuid();
        var lifecycle = Create(instanceId);

        lifecycle.Publish();

        Assert.That(lifecycle.IsReady, Is.True);
        Assert.That(File.Exists(location.FilePath), Is.True);
    }

    [Test]
    public void StopClosesTheGateAndRemovesTheDescriptor()
    {
        var instanceId = Guid.NewGuid();
        var lifecycle = Create(instanceId);
        lifecycle.Publish();

        lifecycle.Stop();

        Assert.That(lifecycle.IsReady, Is.False);
        Assert.That(File.Exists(location.FilePath), Is.False);
    }

    [Test]
    public void APublishThatLosesTheRaceToStopNeverOpensTheGate()
    {
        // Shutdown began before StartedAsync ran: the descriptor must not be written,
        // and the gate must stay closed, so no token is left on disk after the host is
        // gone.
        var instanceId = Guid.NewGuid();
        var lifecycle = Create(instanceId);

        lifecycle.Stop();
        lifecycle.Publish();

        Assert.That(lifecycle.IsReady, Is.False);
        Assert.That(File.Exists(location.FilePath), Is.False);
    }

    [Test]
    public void AFailedPublishPropagatesAndLeavesTheGateClosed()
    {
        // Place a file where the descriptor directory must be created so the store's
        // owner-only directory creation fails. The failure must surface (the host
        // fails fast) rather than be swallowed into a serving-but-undiscoverable host.
        Directory.CreateDirectory(root);
        File.WriteAllText(location.DirectoryPath, "not a directory");
        var lifecycle = Create(Guid.NewGuid());

        Assert.Throws<IOException>((Action)(() => lifecycle.Publish()));
        Assert.That(lifecycle.IsReady, Is.False);
    }

    [Test]
    public void StopLeavesASuccessorDescriptorInPlace()
    {
        // A successor that republished on the same port owns the descriptor now; this
        // instance's Stop must not remove it.
        var mine = Guid.NewGuid();
        var lifecycle = Create(mine);
        lifecycle.Publish();

        // Simulate the successor overwriting the descriptor with its own instance id.
        var successor = Guid.NewGuid();
        new HostDiscoveryStore().Publish(location, Descriptor(successor));

        lifecycle.Stop();

        Assert.That(File.Exists(location.FilePath), Is.True);
        Assert.That(HostDiscoveryDescriptor.TryParse(
            File.ReadAllText(location.FilePath), Port, out var parsed), Is.True);
        Assert.That(parsed!.InstanceId, Is.EqualTo(successor));
    }

    private HostDescriptorLifecycle Create(Guid instanceId)
    {
        return new HostDescriptorLifecycle(
            new HostDiscoveryStore(),
            location,
            Descriptor(instanceId),
            instanceId);
    }

    private static string Descriptor(Guid instanceId)
    {
        return HostDiscoveryDescriptor.Serialize(
            instanceId,
            new Uri("ws://127.0.0.1:" + Port + "/"),
            Token,
            1234,
            DateTimeOffset.UtcNow);
    }
}
