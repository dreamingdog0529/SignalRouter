using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SignalRouter.Protocol.HostDiscovery;

namespace SignalRouter.McpHost;

// Writes and removes the host discovery descriptor with owner-only permissions on
// the directory, the temp file, and the final file, applied at creation rather than
// after the fact (ADR 0008). The token is written only into an already-restricted
// file, the publish is atomic (temp file + rename), and any inability to apply the
// restrictive permissions fails closed. The reader tolerates a concurrent rename by
// opening briefly with FileShare.ReadWrite | Delete.
internal sealed class HostDiscoveryStore
{
    public void Publish(HostDiscoveryLocation location, string descriptorJson)
    {
        if (descriptorJson == null)
        {
            throw new ArgumentNullException(nameof(descriptorJson));
        }

        EnsureOwnerOnlyDirectory(location.DirectoryPath);

        var tempPath = location.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteOwnerOnlyFile(tempPath, descriptorJson);
            VerifyOwnerOnly(tempPath);
            File.Move(tempPath, location.FilePath, overwrite: true);
            VerifyOwnerOnly(location.FilePath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    // Deletes the descriptor only when it still names this instance, so a successor
    // that republished on the same port is never removed. Best effort: a missing or
    // unreadable file is treated as already gone.
    public void DeleteIfOwnedBy(HostDiscoveryLocation location, Guid instanceId)
    {
        string json;
        try
        {
            json = ReadShared(location.FilePath);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        if (!HostDiscoveryDescriptor.TryParse(json, PortOf(location), out var descriptor)
            || descriptor!.InstanceId != instanceId)
        {
            return;
        }

        TryDelete(location.FilePath);
    }

    private static int PortOf(HostDiscoveryLocation location)
    {
        // The file name is host-<port>.json; recover the port for the strict parse.
        var name = Path.GetFileNameWithoutExtension(location.FilePath);
        var dash = name.LastIndexOf('-');
        if (dash >= 0
            && int.TryParse(
                name.Substring(dash + 1),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var port))
        {
            return port;
        }

        return -1;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteOwnerOnlyFile(string path, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        FileStream stream;
        if (OperatingSystem.IsWindows())
        {
            stream = CreateOwnerOnlyFileWindows(path);
        }
        else
        {
            stream = CreateOwnerOnlyFileUnix(path);
        }

        using (stream)
        {
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private static void EnsureOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureOwnerOnlyDirectoryWindows(path);
        }
        else
        {
            EnsureOwnerOnlyDirectoryUnix(path);
        }
    }

    private static void VerifyOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            VerifyOwnerOnlyWindows(path);
        }
        else
        {
            VerifyOwnerOnlyUnix(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureOwnerOnlyDirectoryWindows(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            RequireWindowsOwnerOnly(info.GetAccessControl(), path);
            return;
        }

        var security = new DirectorySecurity();
        var owner = CurrentUser();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.Create(security);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyFileWindows(string path)
    {
        var security = new FileSecurity();
        var owner = CurrentUser();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.Modify,
            FileShare.None,
            4096,
            FileOptions.None,
            security);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyOwnerOnlyWindows(string path)
    {
        RequireWindowsOwnerOnly(new FileInfo(path).GetAccessControl(), path);
    }

    // Owner-only means inheritance is disabled AND every explicit access rule
    // grants only the current user. AreAccessRulesProtected alone describes
    // inheritance, not who has access, so an inherited-off ACL that still grants
    // another SID would otherwise pass (ADR 0008).
    [SupportedOSPlatform("windows")]
    private static void RequireWindowsOwnerOnly(FileSystemSecurity security, string path)
    {
        RequireProtected(security.AreAccessRulesProtected, path);
        var owner = CurrentUser();
        foreach (FileSystemAccessRule rule in
            security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (!rule.IdentityReference.Equals(owner))
            {
                throw new InvalidOperationException(
                    "The descriptor path grants access to another identity: " + path);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IdentityReference CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
    }

    [UnsupportedOSPlatform("windows")]
    private static void EnsureOwnerOnlyDirectoryUnix(string path)
    {
        const UnixFileMode dirMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (Directory.Exists(path))
        {
            RequireUnixMode(File.GetUnixFileMode(path), dirMode, path);
            return;
        }

        Directory.CreateDirectory(path, dirMode);
        // A restrictive umask can strip the requested bits at creation, so verify
        // the resulting mode and fail closed rather than serving a directory the
        // owner cannot use.
        RequireUnixMode(File.GetUnixFileMode(path), dirMode, path);
    }

    [UnsupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyFileUnix(string path)
    {
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    [UnsupportedOSPlatform("windows")]
    private static void VerifyOwnerOnlyUnix(string path)
    {
        RequireUnixMode(
            File.GetUnixFileMode(path),
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            path);
    }

    private static void RequireUnixMode(UnixFileMode actual, UnixFileMode ownerBits, string path)
    {
        // Exact match: no group or other bits may be set, and the owner bits must
        // all be present. A restrictive umask that strips an owner bit (leaving a
        // write-only descriptor the runtime cannot read) must fail closed, not pass.
        if (actual != ownerBits)
        {
            throw new InvalidOperationException(
                "The descriptor path is not owner-only: " + path);
        }
    }

    private static void RequireProtected(bool isProtected, string path)
    {
        if (!isProtected)
        {
            throw new InvalidOperationException(
                "The descriptor path does not have an owner-only, inheritance-disabled ACL: "
                + path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
