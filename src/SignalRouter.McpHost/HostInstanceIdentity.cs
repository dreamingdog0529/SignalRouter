using System;
using System.Diagnostics;
using System.Security.Cryptography;

namespace SignalRouter.McpHost;

// The per-instance identity a host mints once at startup (design §19, ADR 0008):
// a fresh 256-bit token published in the discovery descriptor and, from phase 6,
// verified in the hello handshake. Minting it here — rather than separately for the
// descriptor and for the auth policy — guarantees the token a runtime reads is the
// token the host will later expect.
//
// This is deliberately a plain class, not a record: a record's generated ToString()
// would print the token, and the token must never reach a log or error surface.
internal sealed class HostInstanceIdentity
{
    private HostInstanceIdentity(
        Guid instanceId,
        string tokenHex,
        int processId,
        DateTimeOffset startedAt)
    {
        InstanceId = instanceId;
        TokenHex = tokenHex;
        ProcessId = processId;
        StartedAt = startedAt;
    }

    public Guid InstanceId { get; }

    // The token as 64 lower-case hex characters, the exact shape the descriptor and
    // the hello authentication policy require (a secret — never log or echo it).
    public string TokenHex { get; }

    public int ProcessId { get; }

    // The current process's start time in UTC, matched against Process.StartTime by
    // ProcessHostLiveness to defend against pid reuse.
    public DateTimeOffset StartedAt { get; }

    public static HostInstanceIdentity Create()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(HostInstanceTokenByteLength);
        var tokenHex = Convert.ToHexStringLower(tokenBytes);

        using var self = Process.GetCurrentProcess();
        var startedAt = new DateTimeOffset(self.StartTime.ToUniversalTime());

        return new HostInstanceIdentity(
            Guid.NewGuid(),
            tokenHex,
            Environment.ProcessId,
            startedAt);
    }

    private const int HostInstanceTokenByteLength = 32;
}
