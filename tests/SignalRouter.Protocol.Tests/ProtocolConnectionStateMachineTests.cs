using NUnit.Framework;

namespace SignalRouter.Protocol.Tests;

public sealed class ProtocolConnectionStateMachineTests
{
    private const string Epoch = "epoch-1";

    [Test]
    public void HostAcceptsAValidHelloAndBecomesReady()
    {
        var host = CreateHost();

        var decision = host.OnMessageReceived(ProtocolHandshakeTests.CreateHello());

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.Accept));
        Assert.That(host.Phase, Is.EqualTo(ProtocolConnectionPhase.Ready));
        Assert.That(host.Session!.SessionEpoch, Is.EqualTo(Epoch));
    }

    [Test]
    public void HostRejectsAndClosesOnAnIncompatibleHello()
    {
        var host = CreateHost();

        var decision = host.OnMessageReceived(ProtocolHandshakeTests.CreateHello(
            protocol: new ProtocolVersion(ProtocolVersion.CurrentMajor + 1, 0)));

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.RejectAndClose));
        Assert.That(decision.ErrorCode, Is.EqualTo(ProtocolErrorCodes.ProtocolVersionIncompatible));
        Assert.That(host.Phase, Is.EqualTo(ProtocolConnectionPhase.Closed));
        Assert.That(host.Session, Is.Null);
    }

    [Test]
    public void RuntimeCompletesTheHandshakeThroughItsSentHello()
    {
        var runtime = CreateRuntime();
        var hello = ProtocolHandshakeTests.CreateHello();
        runtime.OnHelloSent(hello);

        var decision = runtime.OnMessageReceived(ProtocolHandshakeTests.CreateWelcome(hello));

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.Accept));
        Assert.That(runtime.Phase, Is.EqualTo(ProtocolConnectionPhase.Ready));
    }

    [Test]
    public void RuntimeClosesOnAWelcomeThatAnswersNoHello()
    {
        var runtime = CreateRuntime();
        var hello = ProtocolHandshakeTests.CreateHello();

        var decision = runtime.OnMessageReceived(ProtocolHandshakeTests.CreateWelcome(hello));

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.RejectAndClose));
        Assert.That(runtime.Phase, Is.EqualTo(ProtocolConnectionPhase.Closed));
    }

    [Test]
    public void HandshakeRoleViolationsClose()
    {
        var host = CreateHost();
        var runtime = CreateRuntime();
        var hello = ProtocolHandshakeTests.CreateHello();

        var welcomeToHost = host.OnMessageReceived(ProtocolHandshakeTests.CreateWelcome(hello));
        var helloToRuntime = runtime.OnMessageReceived(hello);

        Assert.That(welcomeToHost.Verdict, Is.EqualTo(ProtocolConnectionVerdict.RejectAndClose));
        Assert.That(helloToRuntime.Verdict, Is.EqualTo(ProtocolConnectionVerdict.RejectAndClose));
    }

    [Test]
    public void SubstantiveMessagesBeforeTheHandshakeAreRejectedWithoutClosing()
    {
        var runtime = CreateRuntime();

        var decision = runtime.OnMessageReceived(CreateExecute());

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.Reject));
        Assert.That(decision.ErrorCode, Is.EqualTo(ProtocolErrorCodes.HandshakeRequired));
        Assert.That(runtime.Phase, Is.EqualTo(ProtocolConnectionPhase.Handshaking));
    }

    [Test]
    public void AnErrorDuringTheHandshakeIsAcceptedAndCloses()
    {
        var runtime = CreateRuntime();

        var decision = runtime.OnMessageReceived(new ErrorMessage(
            "m-1",
            ProtocolErrorCodes.ProtocolVersionIncompatible,
            "The runtime speaks an incompatible major protocol version."));

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.Accept));
        Assert.That(runtime.Phase, Is.EqualTo(ProtocolConnectionPhase.Closed));
    }

    [Test]
    public void ReadyRuntimeAcceptsHostToRuntimeTraffic()
    {
        var runtime = CreateReadyRuntime();

        Assert.That(
            runtime.OnMessageReceived(CreateExecute()).Verdict,
            Is.EqualTo(ProtocolConnectionVerdict.Accept));
        Assert.That(
            runtime.OnMessageReceived(
                new GetInteractionResultMessage("m-2", Epoch, "r-1")).Verdict,
            Is.EqualTo(ProtocolConnectionVerdict.Accept));
        Assert.That(
            runtime.OnMessageReceived(
                new CancelInteractionMessage("m-3", Epoch, "r-1")).Verdict,
            Is.EqualTo(ProtocolConnectionVerdict.Accept));
        Assert.That(
            runtime.OnMessageReceived(
                new GetRegistrySnapshotMessage("m-4", Epoch)).Verdict,
            Is.EqualTo(ProtocolConnectionVerdict.Accept));
        Assert.That(
            runtime.OnMessageReceived(new PingMessage("m-5", Epoch)).Verdict,
            Is.EqualTo(ProtocolConnectionVerdict.Accept));
    }

    [Test]
    public void ReadyRuntimeRejectsRuntimeToHostTraffic()
    {
        var runtime = CreateReadyRuntime();

        var decision = runtime.OnMessageReceived(
            new InteractionAcceptedMessage("m-2", Epoch, "r-1", "m-1", 1));

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.Reject));
        Assert.That(runtime.Phase, Is.EqualTo(ProtocolConnectionPhase.Ready));
    }

    [Test]
    public void ReadyHostAcceptsRuntimeToHostTraffic()
    {
        var host = CreateReadyHost();

        Assert.That(
            host.OnMessageReceived(
                new InteractionAcceptedMessage("m-2", Epoch, "r-1", "m-1", 1)).Verdict,
            Is.EqualTo(ProtocolConnectionVerdict.Accept));
        Assert.That(
            host.OnMessageReceived(new InteractionResultMessage(
                "m-3",
                Epoch,
                ProtocolInteractionOutcomeTests.CreateSucceededOutcome("r-1"))).Verdict,
            Is.EqualTo(ProtocolConnectionVerdict.Accept));
        Assert.That(
            host.OnMessageReceived(new RegistrySnapshotMessage(
                "m-4",
                Epoch,
                "m-1",
                1,
                "{}")).Verdict,
            Is.EqualTo(ProtocolConnectionVerdict.Accept));
    }

    [Test]
    public void ReadyHostRejectsHostToRuntimeTraffic()
    {
        var host = CreateReadyHost();

        Assert.That(
            host.OnMessageReceived(CreateExecute()).Verdict,
            Is.EqualTo(ProtocolConnectionVerdict.Reject));
    }

    [Test]
    public void RepeatedHandshakeMessagesAfterReadyAreRejected()
    {
        var host = CreateReadyHost();

        var decision = host.OnMessageReceived(ProtocolHandshakeTests.CreateHello());

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.Reject));
        Assert.That(host.Phase, Is.EqualTo(ProtocolConnectionPhase.Ready));
    }

    [Test]
    public void MessagesBeyondTheNegotiatedVersionAreRejected()
    {
        var runtime = CreateReadyRuntime();

        var decision = runtime.OnMessageReceived(new ExecuteInteractionMessage(
            "m-2",
            Epoch,
            "r-1",
            "click",
            1,
            "target-1",
            "{}",
            null,
            null,
            new ProtocolVersion(
                ProtocolVersion.CurrentMajor,
                ProtocolVersion.CurrentMinor + 1)));

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.Reject));
        Assert.That(
            decision.ErrorCode,
            Is.EqualTo(ProtocolErrorCodes.ProtocolVersionIncompatible));
        Assert.That(runtime.Phase, Is.EqualTo(ProtocolConnectionPhase.Ready));
    }

    [Test]
    public void AForeignSessionEpochClosesTheConnection()
    {
        var runtime = CreateReadyRuntime();

        var decision = runtime.OnMessageReceived(new ExecuteInteractionMessage(
            "m-2",
            "epoch-2",
            "r-1",
            "click",
            1,
            "target-1",
            "{}"));

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.RejectAndClose));
        Assert.That(decision.ErrorCode, Is.EqualTo(ProtocolErrorCodes.SessionEpochMismatch));
        Assert.That(runtime.Phase, Is.EqualTo(ProtocolConnectionPhase.Closed));
    }

    [Test]
    public void EpochFreeLivenessMessagesSkipTheEpochCheck()
    {
        var runtime = CreateReadyRuntime();

        Assert.That(
            runtime.OnMessageReceived(new PingMessage("m-2")).Verdict,
            Is.EqualTo(ProtocolConnectionVerdict.Accept));
    }

    [Test]
    public void ClosedConnectionsRejectEverything()
    {
        var runtime = CreateReadyRuntime();
        runtime.Close();

        var decision = runtime.OnMessageReceived(new PingMessage("m-2"));

        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.RejectAndClose));
    }

    [Test]
    public void HelloSentBookkeepingFailsFastOnLocalMisuse()
    {
        var host = CreateHost();
        var runtime = CreateRuntime();
        var hello = ProtocolHandshakeTests.CreateHello();
        runtime.OnHelloSent(hello);

        NUnitCompat.Throws<InvalidOperationException>(() => host.OnHelloSent(hello));
        NUnitCompat.Throws<InvalidOperationException>(() => runtime.OnHelloSent(hello));
    }

    private static ProtocolConnectionStateMachine CreateHost()
    {
        return new ProtocolConnectionStateMachine(
            ProtocolConnectionRole.Host,
            ProtocolHandshakeTests.CreateOptions());
    }

    private static ProtocolConnectionStateMachine CreateRuntime()
    {
        return new ProtocolConnectionStateMachine(
            ProtocolConnectionRole.Runtime,
            ProtocolHandshakeTests.CreateOptions("SignalRouter.Unity 0.1.0"));
    }

    private static ProtocolConnectionStateMachine CreateReadyRuntime()
    {
        var runtime = CreateRuntime();
        var hello = ProtocolHandshakeTests.CreateHello();
        runtime.OnHelloSent(hello);
        var decision = runtime.OnMessageReceived(ProtocolHandshakeTests.CreateWelcome(hello));
        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.Accept));
        return runtime;
    }

    private static ProtocolConnectionStateMachine CreateReadyHost()
    {
        var host = CreateHost();
        var decision = host.OnMessageReceived(ProtocolHandshakeTests.CreateHello());
        Assert.That(decision.Verdict, Is.EqualTo(ProtocolConnectionVerdict.Accept));
        return host;
    }

    private static ExecuteInteractionMessage CreateExecute()
    {
        return new ExecuteInteractionMessage(
            "m-1",
            Epoch,
            "r-1",
            "click",
            1,
            "target-1",
            "{}");
    }
}
