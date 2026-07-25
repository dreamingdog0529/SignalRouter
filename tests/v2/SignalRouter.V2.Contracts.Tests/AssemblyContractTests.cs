using System;
using System.Linq;
using System.Runtime.Versioning;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// Pins the axioms of ADR 0007 on the built assembly: netstandard2.1, BCL-only
/// references (dependency zero), and an immutable public surface (no setters that
/// would let evidence mutate after construction).
/// </summary>
public sealed class AssemblyContractTests
{
    private static readonly System.Reflection.Assembly ContractsAssembly =
        typeof(EvidenceCut).Assembly;

    [Test]
    public void AssemblyTargetsNetStandard21()
    {
        var framework = ContractsAssembly
            .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
            .Cast<TargetFrameworkAttribute>()
            .Single();
        Assert.That(framework.FrameworkName, Is.EqualTo(".NETStandard,Version=v2.1"));
    }

    [Test]
    public void AssemblyHasExpectedIdentity()
    {
        Assert.That(ContractsAssembly.GetName().Name, Is.EqualTo("SignalRouter.V2.Contracts"));
    }

    [Test]
    public void AssemblyReferencesOnlyTheStandardLibrary()
    {
        var references = ContractsAssembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();
        Assert.That(references, Is.EqualTo(new[] { "netstandard" }));
    }

    [Test]
    public void PublicSurfaceHasNoSetters()
    {
        var setters = ContractsAssembly.GetExportedTypes()
            .SelectMany(type => type.GetProperties())
            .Where(property => property.SetMethod != null && property.SetMethod.IsPublic)
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ToArray();
        Assert.That(setters, Is.Empty);
    }

    [Test]
    public void PublicSurfaceContainsNoSerializerTypes()
    {
        var suspicious = ContractsAssembly.GetExportedTypes()
            .Where(type =>
                type.FullName!.Contains("Json", StringComparison.OrdinalIgnoreCase) ||
                type.FullName.Contains("Serializ", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.That(suspicious, Is.Empty);
    }
}
