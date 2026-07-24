using System.Globalization;
using NUnit.Framework;
using SignalRouter.Protocol.HostDiscovery;

namespace SignalRouter.Protocol.Tests;

public sealed class HostDiscoveryDescriptorTests
{
    private const int Port = 8017;

    private const string Token =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public void AValidDescriptorRoundTrips()
    {
        var instanceId = Guid.NewGuid();
        var startedAt = new DateTimeOffset(2026, 7, 25, 1, 2, 3, TimeSpan.Zero);
        var json = HostDiscoveryDescriptor.Serialize(
            instanceId,
            new Uri("ws://127.0.0.1:8017/"),
            Token,
            4242,
            startedAt);

        Assert.That(HostDiscoveryDescriptor.TryParse(json, Port, out var descriptor), Is.True);
        Assert.That(descriptor!.InstanceId, Is.EqualTo(instanceId));
        Assert.That(descriptor.Token, Is.EqualTo(Token));
        Assert.That(descriptor.ProcessId, Is.EqualTo(4242));
        Assert.That(descriptor.StartedAt, Is.EqualTo(startedAt));
        Assert.That(descriptor.Endpoint.Port, Is.EqualTo(Port));
    }

    [Test]
    public void AnIpv6LoopbackEndpointIsAccepted()
    {
        var json = Build(endpoint: "ws://[::1]:8017/");

        Assert.That(HostDiscoveryDescriptor.TryParse(json, Port, out _), Is.True);
    }

    [Test]
    public void AnUnknownMemberIsIgnored()
    {
        var json = "{\"schemaVersion\":1,\"instanceId\":\"" + Guid.NewGuid().ToString("D")
            + "\",\"endpoint\":\"ws://127.0.0.1:8017/\",\"token\":\"" + Token
            + "\",\"pid\":10,\"startedAt\":\"2026-07-25T00:00:00.0000000+00:00\",\"future\":true}";

        Assert.That(HostDiscoveryDescriptor.TryParse(json, Port, out _), Is.True);
    }

    [Test]
    public void MalformedDescriptorsAreRejected()
    {
        // Wrong schema version.
        Reject(Build(schemaVersion: 2), "schema version");
        // Token shape.
        Reject(Build(token: "abc"), "short token");
        Reject(Build(token: Token.ToUpperInvariant()), "uppercase token");
        // Non-canonical GUID.
        Reject(Build(instanceId: "not-a-guid"), "bad guid");
        Reject(Build(instanceId: Guid.NewGuid().ToString("N")), "non-canonical guid");
        // Non-positive pid.
        Reject(Build(pid: 0), "zero pid");
        Reject(Build(pid: -1), "negative pid");
        // Timestamp not UTC / malformed.
        Reject(Build(startedAt: "2026-07-25T00:00:00.0000000+02:00"), "non-utc");
        Reject(Build(startedAt: "not-a-date"), "bad date");
        // Endpoint deviations.
        Reject(Build(endpoint: "wss://127.0.0.1:8017/"), "wrong scheme");
        Reject(Build(endpoint: "ws://127.0.0.1:9999/"), "port mismatch");
        Reject(Build(endpoint: "ws://10.0.0.1:8017/"), "non-loopback");
        Reject(Build(endpoint: "ws://127.0.0.2:8017/"), "other 127/8 address");
        Reject(Build(endpoint: "ws://user@127.0.0.1:8017/"), "userinfo");
        Reject(Build(endpoint: "ws://127.0.0.1:8017/?x=1"), "query");
        Reject(Build(endpoint: "ws://127.0.0.1:8017/#f"), "fragment");
        Reject(Build(endpoint: "ws://127.0.0.1:8017/path"), "non-root path");
    }

    [Test]
    public void ADuplicateMemberIsRejected()
    {
        var json = "{\"schemaVersion\":1,\"schemaVersion\":1,\"instanceId\":\""
            + Guid.NewGuid().ToString("D")
            + "\",\"endpoint\":\"ws://127.0.0.1:8017/\",\"token\":\"" + Token
            + "\",\"pid\":10,\"startedAt\":\"2026-07-25T00:00:00.0000000+00:00\"}";

        Assert.That(HostDiscoveryDescriptor.TryParse(json, Port, out _), Is.False);
    }

    [Test]
    public void AMissingMemberIsRejected()
    {
        var json = "{\"schemaVersion\":1,\"endpoint\":\"ws://127.0.0.1:8017/\",\"token\":\""
            + Token + "\",\"pid\":10,\"startedAt\":\"2026-07-25T00:00:00.0000000+00:00\"}";

        Assert.That(HostDiscoveryDescriptor.TryParse(json, Port, out _), Is.False);
    }

    [Test]
    public void AnOversizeDescriptorIsRejected()
    {
        var json = Build() + new string(' ', HostDiscoveryDescriptor.MaxDescriptorBytes);

        Assert.That(HostDiscoveryDescriptor.TryParse(json, Port, out _), Is.False);
    }

    [Test]
    public void EmptyAndGarbageInputIsRejected()
    {
        Assert.That(HostDiscoveryDescriptor.TryParse("", Port, out _), Is.False);
        Assert.That(HostDiscoveryDescriptor.TryParse("not json", Port, out _), Is.False);
        Assert.That(HostDiscoveryDescriptor.TryParse("[]", Port, out _), Is.False);
    }

    [Test]
    public void ContentAfterTheDescriptorObjectIsRejected()
    {
        // A valid object followed by a second value or garbage is a corrupted or
        // concatenated file.
        Assert.That(HostDiscoveryDescriptor.TryParse(Build() + "{}", Port, out _), Is.False);
        Assert.That(HostDiscoveryDescriptor.TryParse(Build() + " garbage", Port, out _), Is.False);
        // Trailing whitespace alone is fine.
        Assert.That(HostDiscoveryDescriptor.TryParse(Build() + "\n  ", Port, out _), Is.True);
    }

    private static void Reject(string json, string because)
    {
        Assert.That(
            HostDiscoveryDescriptor.TryParse(json, Port, out _),
            Is.False,
            because);
    }

    private static string Build(
        int schemaVersion = 1,
        string? instanceId = null,
        string endpoint = "ws://127.0.0.1:8017/",
        string token = Token,
        int pid = 10,
        string startedAt = "2026-07-25T00:00:00.0000000+00:00")
    {
        instanceId ??= Guid.NewGuid().ToString("D");
        return string.Format(
            CultureInfo.InvariantCulture,
            "{{\"schemaVersion\":{0},\"instanceId\":\"{1}\",\"endpoint\":\"{2}\",\"token\":\"{3}\",\"pid\":{4},\"startedAt\":\"{5}\"}}",
            schemaVersion,
            instanceId,
            endpoint,
            token,
            pid,
            startedAt);
    }
}
