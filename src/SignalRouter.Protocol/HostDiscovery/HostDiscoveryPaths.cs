using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SignalRouter.Protocol.HostDiscovery
{
    // The owner-only directory and file a host descriptor lives in, keyed by port
    // (ADR 0008): `%LOCALAPPDATA%\SignalRouter\hosts\host-<port>.json` on Windows and
    // `$XDG_RUNTIME_DIR/signalrouter/host-<port>.json` on Unix. If XDG_RUNTIME_DIR is
    // unset the host fails to start rather than falling back to a world-readable
    // location; macOS does not guarantee it, so a macOS host is out of scope.
    public readonly struct HostDiscoveryLocation
    {
        public HostDiscoveryLocation(string directoryPath, string filePath)
        {
            DirectoryPath = directoryPath;
            FilePath = filePath;
        }

        public string DirectoryPath { get; }

        public string FilePath { get; }
    }

    public static class HostDiscoveryPaths
    {
        public static HostDiscoveryLocation Resolve(int port)
        {
            return Resolve(
                port,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));
        }

        internal static HostDiscoveryLocation Resolve(
            int port,
            bool isWindows,
            string? localApplicationData,
            string? xdgRuntimeDir)
        {
            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(port),
                    port,
                    "The port must be between 1 and 65535.");
            }

            var fileName = string.Format(
                CultureInfo.InvariantCulture,
                "host-{0}.json",
                port);

            if (isWindows)
            {
                if (string.IsNullOrEmpty(localApplicationData))
                {
                    throw new InvalidOperationException(
                        "The local application data directory could not be resolved.");
                }

                var directory = localApplicationData + "\\SignalRouter\\hosts";
                return new HostDiscoveryLocation(directory, directory + "\\" + fileName);
            }

            if (string.IsNullOrEmpty(xdgRuntimeDir))
            {
                throw new InvalidOperationException(
                    "XDG_RUNTIME_DIR is not set; the host cannot publish a descriptor in a "
                    + "guaranteed owner-only location. (macOS is out of scope for item 9.)");
            }

            var unixDirectory = xdgRuntimeDir + "/signalrouter";
            return new HostDiscoveryLocation(unixDirectory, unixDirectory + "/" + fileName);
        }
    }
}
