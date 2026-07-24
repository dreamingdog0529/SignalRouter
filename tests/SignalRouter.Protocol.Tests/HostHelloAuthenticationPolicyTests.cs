using NUnit.Framework;

namespace SignalRouter.Protocol.Tests;

public sealed class HostHelloAuthenticationPolicyTests
{
    private const string ValidToken =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public void VerifyAcceptsOnlyTheExactToken()
    {
        var policy = HostHelloAuthenticationPolicy.FromHex(ValidToken);

        Assert.That(policy.Verify(ValidToken), Is.True);
        Assert.That(policy.Verify(null), Is.False);
        Assert.That(
            policy.Verify("0000000000000000000000000000000000000000000000000000000000000000"),
            Is.False);
        // Wrong length, non-hex, and uppercase all decode to null → false.
        Assert.That(policy.Verify("abc"), Is.False);
        Assert.That(policy.Verify(ValidToken + "00"), Is.False);
        Assert.That(policy.Verify("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"), Is.False);
        Assert.That(policy.Verify(ValidToken.ToUpperInvariant()), Is.False);
    }

    [Test]
    public void FromHexFailsFastOnAMalformedExpectedToken()
    {
        NUnitCompat.Throws<ArgumentNullException>(() => HostHelloAuthenticationPolicy.FromHex(null!));
        NUnitCompat.Throws<ArgumentException>(() => HostHelloAuthenticationPolicy.FromHex("abc"));
        NUnitCompat.Throws<ArgumentException>(
            () => HostHelloAuthenticationPolicy.FromHex(ValidToken.ToUpperInvariant()));
    }

    [Test]
    public void ConstructorFailsFastOnAWrongLengthKey()
    {
        NUnitCompat.Throws<ArgumentNullException>(
            () => new HostHelloAuthenticationPolicy(null!));
        NUnitCompat.Throws<ArgumentException>(
            () => new HostHelloAuthenticationPolicy(new byte[31]));
        NUnitCompat.Throws<ArgumentException>(
            () => new HostHelloAuthenticationPolicy(new byte[33]));
    }

    [Test]
    public void TheExpectedKeyIsDefensivelyCopied()
    {
        var key = new byte[HostHelloAuthenticationPolicy.TokenByteLength];
        for (var index = 0; index < key.Length; index++)
        {
            key[index] = (byte)index;
        }

        var policy = new HostHelloAuthenticationPolicy(key);
        var hex = ToHex(key);
        // Mutating the caller's array after construction must not change verification.
        key[0] ^= 0xFF;

        Assert.That(policy.Verify(hex), Is.True);
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
