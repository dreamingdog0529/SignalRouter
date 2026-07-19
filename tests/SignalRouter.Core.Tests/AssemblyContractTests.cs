using System.Reflection;
using System.Runtime.Versioning;
using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class AssemblyContractTests
{
    [Test]
    public void CoreAssemblyHasExpectedIdentityAndTargetFramework()
    {
        var assembly = Assembly.Load(new AssemblyName("SignalRouter.Core"));

        Assert.That(assembly.GetName().Name, Is.EqualTo("SignalRouter.Core"));
        Assert.That(
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName,
            Is.EqualTo(".NETStandard,Version=v2.1"));
    }
}
