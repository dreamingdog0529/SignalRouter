using System.Reflection;
using System.Runtime.Versioning;
using NUnit.Framework;

namespace SignalRouter.Protocol.Tests;

public sealed class AssemblyContractTests
{
    [Test]
    public void ProtocolAssemblyHasExpectedIdentityAndTargetFramework()
    {
        var assembly = Assembly.Load(new AssemblyName("SignalRouter.Protocol"));

        Assert.That(assembly.GetName().Name, Is.EqualTo("SignalRouter.Protocol"));
        Assert.That(
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName,
            Is.EqualTo(".NETStandard,Version=v2.1"));
    }
}
