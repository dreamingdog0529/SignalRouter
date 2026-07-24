using System.IO;
using NUnit.Framework;
using SignalRouter.Protocol.HostDiscovery;

namespace SignalRouter.Protocol.Tests;

public sealed class HostDiscoveryPathsTests
{
    [Test]
    public void WindowsResolvesUnderLocalApplicationData()
    {
        var location = HostDiscoveryPaths.Resolve(
            8017,
            isWindows: true,
            localApplicationData: "C:\\Users\\dev\\AppData\\Local",
            xdgRuntimeDir: null);

        Assert.That(
            location.DirectoryPath,
            Is.EqualTo("C:\\Users\\dev\\AppData\\Local\\SignalRouter\\hosts"));
        Assert.That(
            location.FilePath,
            Is.EqualTo("C:\\Users\\dev\\AppData\\Local\\SignalRouter\\hosts\\host-8017.json"));
    }

    [Test]
    public void UnixResolvesUnderXdgRuntimeDir()
    {
        var location = HostDiscoveryPaths.Resolve(
            8017,
            isWindows: false,
            localApplicationData: null,
            xdgRuntimeDir: "/run/user/1000");

        Assert.That(location.DirectoryPath, Is.EqualTo("/run/user/1000/signalrouter"));
        Assert.That(location.FilePath, Is.EqualTo("/run/user/1000/signalrouter/host-8017.json"));
    }

    [Test]
    public void UnixFailsWhenXdgRuntimeDirIsUnset()
    {
        NUnitCompat.Throws<InvalidOperationException>(() => HostDiscoveryPaths.Resolve(
            8017,
            isWindows: false,
            localApplicationData: null,
            xdgRuntimeDir: null));
    }

    [Test]
    public void WindowsFailsWhenLocalApplicationDataIsMissing()
    {
        NUnitCompat.Throws<InvalidOperationException>(() => HostDiscoveryPaths.Resolve(
            8017,
            isWindows: true,
            localApplicationData: null,
            xdgRuntimeDir: null));
    }

    [Test]
    public void AnOutOfRangePortIsRejected()
    {
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => HostDiscoveryPaths.Resolve(0));
        NUnitCompat.Throws<ArgumentOutOfRangeException>(() => HostDiscoveryPaths.Resolve(70000));
    }

    [Test]
    public void ThePublicResolveNamesTheFileByPortOnTheCurrentOs()
    {
        // Runs on both CI OSes; each covers its own branch of the resolver. On Unix
        // XDG_RUNTIME_DIR must be set for the public resolver, and the ubuntu CI
        // agent does not define it — so set and restore it here rather than depend
        // on environment state.
        var original = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.GetTempPath());
            }

            var location = HostDiscoveryPaths.Resolve(8017);

            Assert.That(location.FilePath, Does.EndWith("host-8017.json"));
            Assert.That(location.DirectoryPath, Is.Not.Empty);
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", original);
            }
        }
    }
}
