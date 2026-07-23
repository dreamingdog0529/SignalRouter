using NUnit.Framework;

namespace SignalRouter.Protocol.Tests;

public sealed class ProtocolHandshakeTests
{
    private const string Epoch = "epoch-1";

    [Test]
    public void MatchingVersionsNegotiateTheSharedSession()
    {
        var local = CreateOptions(
            "SignalRouter.McpHost 0.1.0",
            new[] { "capability-a", "capability-b" },
            2 * 1024 * 1024);
        var hello = CreateHello(
            capabilities: new[] { "capability-b", "capability-c" },
            maxReceiveMessageBytes: 1024 * 1024);

        var decision = ProtocolHandshake.EvaluateHello(local, hello);

        Assert.That(decision.Accepted, Is.True);
        var session = decision.Session!;
        Assert.That(session.Version, Is.EqualTo(ProtocolVersion.Current));
        Assert.That(session.SessionEpoch, Is.EqualTo(Epoch));
        Assert.That(session.RemotePeerVersion, Is.EqualTo("SignalRouter.Unity 0.1.0"));
        Assert.That(session.Capabilities, Is.EqualTo(new[] { "capability-b" }));
        Assert.That(session.MaxSendMessageBytes, Is.EqualTo(1024 * 1024));
        Assert.That(session.MaxReceiveMessageBytes, Is.EqualTo(2 * 1024 * 1024));
    }

    [Test]
    public void MajorMismatchRejectsTheHelloInBothDirections()
    {
        var local = CreateOptions();
        var newerMajor = CreateHello(
            protocol: new ProtocolVersion(ProtocolVersion.CurrentMajor + 1, 0));
        var olderMajor = CreateHello(protocol: new ProtocolVersion(0, 9));

        var newer = ProtocolHandshake.EvaluateHello(local, newerMajor);
        var older = ProtocolHandshake.EvaluateHello(local, olderMajor);

        Assert.That(newer.Accepted, Is.False);
        Assert.That(newer.ErrorCode, Is.EqualTo(ProtocolErrorCodes.ProtocolVersionIncompatible));
        Assert.That(newer.Session, Is.Null);
        Assert.That(older.Accepted, Is.False);
        Assert.That(older.ErrorCode, Is.EqualTo(ProtocolErrorCodes.ProtocolVersionIncompatible));
    }

    [Test]
    public void MinorSkewSelectsTheLowerMinor()
    {
        var local = CreateOptions();
        var newerMinor = CreateHello(
            protocol: new ProtocolVersion(ProtocolVersion.CurrentMajor, ProtocolVersion.CurrentMinor + 5));

        var decision = ProtocolHandshake.EvaluateHello(local, newerMinor);

        Assert.That(decision.Accepted, Is.True);
        Assert.That(decision.Session!.Version, Is.EqualTo(ProtocolVersion.Current));
    }

    [Test]
    public void UnknownCapabilitiesDegradeSilently()
    {
        var local = CreateOptions(capabilities: new[] { "known" });
        var hello = CreateHello(capabilities: new[] { "known", "from-the-future" });

        var decision = ProtocolHandshake.EvaluateHello(local, hello);

        Assert.That(decision.Session!.Capabilities, Is.EqualTo(new[] { "known" }));
    }

    [Test]
    public void EmptyCapabilitySetsNegotiateAnEmptyIntersection()
    {
        var decision = ProtocolHandshake.EvaluateHello(CreateOptions(), CreateHello());

        Assert.That(decision.Accepted, Is.True);
        Assert.That(decision.Session!.Capabilities, Is.Empty);
    }

    [Test]
    public void WelcomeMustAnswerTheSentHello()
    {
        var hello = CreateHello();
        var wrongReply = CreateWelcome(hello, inReplyTo: "someone-elses-hello");

        var decision = ProtocolHandshake.EvaluateWelcome(hello, wrongReply);

        Assert.That(decision.Accepted, Is.False);
        Assert.That(decision.ErrorCode, Is.EqualTo(ProtocolErrorCodes.MalformedMessage));
    }

    [Test]
    public void WelcomeMustEchoTheHellosSessionEpoch()
    {
        var hello = CreateHello();
        var wrongEpoch = CreateWelcome(hello, sessionEpoch: "epoch-2");

        var decision = ProtocolHandshake.EvaluateWelcome(hello, wrongEpoch);

        Assert.That(decision.Accepted, Is.False);
        Assert.That(decision.ErrorCode, Is.EqualTo(ProtocolErrorCodes.SessionEpochMismatch));
    }

    [Test]
    public void WelcomeMayNotSelectBeyondTheHellosOffer()
    {
        var hello = CreateHello();
        var beyondOffer = CreateWelcome(
            hello,
            protocol: new ProtocolVersion(
                ProtocolVersion.CurrentMajor,
                hello.Protocol.Minor + 1));
        var wrongMajor = CreateWelcome(
            hello,
            protocol: new ProtocolVersion(ProtocolVersion.CurrentMajor + 1, 0));

        Assert.That(ProtocolHandshake.EvaluateWelcome(hello, beyondOffer).Accepted, Is.False);
        Assert.That(
            ProtocolHandshake.EvaluateWelcome(hello, wrongMajor).ErrorCode,
            Is.EqualTo(ProtocolErrorCodes.ProtocolVersionIncompatible));
    }

    [Test]
    public void AcceptedWelcomeEstablishesTheRuntimeSideSession()
    {
        var hello = CreateHello(
            capabilities: new[] { "capability-a", "capability-b" },
            maxReceiveMessageBytes: 1024 * 1024);
        var welcome = CreateWelcome(
            hello,
            capabilities: new[] { "capability-b", "capability-c" },
            maxReceiveMessageBytes: 4 * 1024 * 1024);

        var decision = ProtocolHandshake.EvaluateWelcome(hello, welcome);

        Assert.That(decision.Accepted, Is.True);
        var session = decision.Session!;
        Assert.That(session.Version, Is.EqualTo(welcome.Protocol));
        Assert.That(session.SessionEpoch, Is.EqualTo(Epoch));
        Assert.That(session.Capabilities, Is.EqualTo(new[] { "capability-b" }));
        Assert.That(session.MaxSendMessageBytes, Is.EqualTo(4 * 1024 * 1024));
        Assert.That(session.MaxReceiveMessageBytes, Is.EqualTo(1024 * 1024));
    }

    [Test]
    public void TheRecoveryWindowPropagatesToBothSessionSides()
    {
        var hello = new HelloMessage(
            "hello-1",
            Epoch,
            "SignalRouter.Unity 0.1.0",
            Array.Empty<string>(),
            ProtocolLimits.DefaultMaxReceiveMessageBytes,
            null,
            recoveryWindowMs: 120_000);

        var hostSide = ProtocolHandshake.EvaluateHello(CreateOptions(), hello);
        var runtimeSide = ProtocolHandshake.EvaluateWelcome(hello, CreateWelcome(hello));

        Assert.That(
            hostSide.Session!.RecoveryWindow,
            Is.EqualTo(TimeSpan.FromMilliseconds(120_000)));
        Assert.That(
            runtimeSide.Session!.RecoveryWindow,
            Is.EqualTo(TimeSpan.FromMilliseconds(120_000)));
    }

    [Test]
    public void EvaluationIsPure()
    {
        var local = CreateOptions();
        var hello = CreateHello();

        var first = ProtocolHandshake.EvaluateHello(local, hello);
        var second = ProtocolHandshake.EvaluateHello(local, hello);

        Assert.That(first.Accepted, Is.True);
        Assert.That(second.Accepted, Is.True);
        Assert.That(second.Session!.Version, Is.EqualTo(first.Session!.Version));
        Assert.That(second.Session.Capabilities, Is.EqualTo(first.Session.Capabilities));
    }

    internal static ProtocolPeerOptions CreateOptions(
        string peerVersion = "SignalRouter.McpHost 0.1.0",
        IEnumerable<string>? capabilities = null,
        int maxReceiveMessageBytes = ProtocolLimits.DefaultMaxReceiveMessageBytes)
    {
        return new ProtocolPeerOptions(
            peerVersion,
            capabilities ?? Array.Empty<string>(),
            maxReceiveMessageBytes);
    }

    internal static HelloMessage CreateHello(
        IEnumerable<string>? capabilities = null,
        int maxReceiveMessageBytes = ProtocolLimits.DefaultMaxReceiveMessageBytes,
        ProtocolVersion? protocol = null)
    {
        return new HelloMessage(
            "hello-1",
            Epoch,
            "SignalRouter.Unity 0.1.0",
            capabilities ?? Array.Empty<string>(),
            maxReceiveMessageBytes,
            null,
            protocol: protocol);
    }

    internal static WelcomeMessage CreateWelcome(
        HelloMessage hello,
        string? sessionEpoch = null,
        string? inReplyTo = null,
        IEnumerable<string>? capabilities = null,
        int maxReceiveMessageBytes = ProtocolLimits.DefaultMaxReceiveMessageBytes,
        ProtocolVersion? protocol = null)
    {
        return new WelcomeMessage(
            "welcome-1",
            sessionEpoch ?? hello.SessionEpoch!,
            inReplyTo ?? hello.MessageId,
            "SignalRouter.McpHost 0.1.0",
            capabilities ?? Array.Empty<string>(),
            maxReceiveMessageBytes,
            protocol);
    }
}
