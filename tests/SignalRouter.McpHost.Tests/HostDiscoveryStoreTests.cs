using System;
using System.IO;
using NUnit.Framework;
using SignalRouter.Protocol.HostDiscovery;

namespace SignalRouter.McpHost.Tests;

// Real-filesystem integration tests for the owner-only descriptor store. They run
// on both CI operating systems (ubuntu-latest and windows-latest), so each OS
// exercises its own permission path (ADR 0008).
public sealed class HostDiscoveryStoreTests
{
    private const int Port = 8017;

    private const string Token =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private string root = string.Empty;
    private HostDiscoveryLocation location;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "signalrouter-store-" + Guid.NewGuid().ToString("N"));
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
    public void PublishWritesADescriptorThatRoundTripsAndIsOwnerOnly()
    {
        var instanceId = Guid.NewGuid();
        new HostDiscoveryStore().Publish(location, Descriptor(instanceId));

        var text = File.ReadAllText(location.FilePath);
        Assert.That(HostDiscoveryDescriptor.TryParse(text, Port, out var parsed), Is.True);
        Assert.That(parsed!.InstanceId, Is.EqualTo(instanceId));
        AssertOwnerOnly(location.FilePath);
        // No temp files are left behind.
        Assert.That(Directory.GetFiles(location.DirectoryPath), Has.Length.EqualTo(1));
    }

    [Test]
    public void PublishAtomicallyOverwritesAnExistingDescriptor()
    {
        var store = new HostDiscoveryStore();
        store.Publish(location, Descriptor(Guid.NewGuid()));
        var second = Guid.NewGuid();

        store.Publish(location, Descriptor(second));

        Assert.That(HostDiscoveryDescriptor.TryParse(
            File.ReadAllText(location.FilePath), Port, out var parsed), Is.True);
        Assert.That(parsed!.InstanceId, Is.EqualTo(second));
        Assert.That(Directory.GetFiles(location.DirectoryPath), Has.Length.EqualTo(1));
    }

    [Test]
    public void DeleteIfOwnedByRemovesOnlyThisInstancesDescriptor()
    {
        var store = new HostDiscoveryStore();
        var mine = Guid.NewGuid();
        store.Publish(location, Descriptor(mine));

        // A different instance must not remove it.
        store.DeleteIfOwnedBy(location, Guid.NewGuid());
        Assert.That(File.Exists(location.FilePath), Is.True);

        // The owning instance removes it.
        store.DeleteIfOwnedBy(location, mine);
        Assert.That(File.Exists(location.FilePath), Is.False);
    }

    [Test]
    public void DeleteIfOwnedByLeavesAnOversizeFileInPlace()
    {
        var store = new HostDiscoveryStore();
        var owner = Guid.NewGuid();
        store.Publish(location, Descriptor(owner));
        // Pad the file past the descriptor size cap. The bounded read must not attempt
        // to load the whole file, and an oversize file no longer parses as a
        // descriptor, so it names no instance and is left untouched. Deleting as the
        // real owner means retention can only be due to the oversize rejection, not an
        // instance-id mismatch.
        File.AppendAllText(
            location.FilePath,
            new string(' ', HostDiscoveryDescriptor.MaxDescriptorBytes));

        store.DeleteIfOwnedBy(location, owner);

        Assert.That(File.Exists(location.FilePath), Is.True);
    }

    [Test]
    public void DeleteIfOwnedByToleratesAMissingDescriptor()
    {
        // No descriptor was published; deleting must be a tolerated no-op.
        new HostDiscoveryStore().DeleteIfOwnedBy(location, Guid.NewGuid());

        Assert.That(File.Exists(location.FilePath), Is.False);
    }

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.That(
                new FileInfo(path).GetAccessControl().AreAccessRulesProtected,
                Is.True,
                "the descriptor ACL must be protected (inheritance disabled)");
        }
        else
        {
            var mode = File.GetUnixFileMode(path);
            var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.That(
                mode & ~ownerOnly,
                Is.EqualTo(UnixFileMode.None),
                "the descriptor must be 0600 (no group or other bits)");
        }
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
