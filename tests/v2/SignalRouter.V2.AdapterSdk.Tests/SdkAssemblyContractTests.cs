using System;
using System.Linq;
using System.Runtime.Versioning;
using NUnit.Framework;

namespace SignalRouter.V2.AdapterSdk.Tests;

/// <summary>
/// ADR 0007 / ADR 0010 axioms on the built shape assembly: netstandard2.1,
/// references = BCL + Contracts only, and an immutable public surface.
/// </summary>
public sealed class SdkAssemblyContractTests
{
    private static readonly System.Reflection.Assembly SdkAssembly =
        typeof(IEffectExecutor).Assembly;

    [Test]
    public void AssemblyTargetsNetStandard21()
    {
        var framework = SdkAssembly
            .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
            .Cast<TargetFrameworkAttribute>()
            .Single();
        Assert.That(framework.FrameworkName, Is.EqualTo(".NETStandard,Version=v2.1"));
    }

    [Test]
    public void AssemblyReferencesOnlyTheStandardLibraryAndContracts()
    {
        var references = SdkAssembly.GetReferencedAssemblies()
            .Select(name => name.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.That(references, Is.EqualTo(new[] { "SignalRouter.V2.Contracts", "netstandard" }));
    }

    [Test]
    public void PublicSurfaceHasNoSetters()
    {
        var setters = SdkAssembly.GetExportedTypes()
            .SelectMany(type => type.GetProperties())
            .Where(property => property.SetMethod != null && property.SetMethod.IsPublic)
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ToArray();
        Assert.That(setters, Is.Empty);
    }

    [Test]
    public void TheSpeccedInterfacesExist()
    {
        // adapter-conformance.md §2: the shape assembly is the normative referent.
        var names = SdkAssembly.GetExportedTypes().Select(type => type.Name).ToArray();
        Assert.That(names, Is.SupersetOf(new[]
        {
            "INodeSource", "IEffectExecutor", "IIngressSource", "IPumpHost",
            "IBootstrapRegistry", "INodeRegistry", "IIngressSink",
            "IEffectCompletionSink", "IPumpable", "IMonotonicClock",
        }));
        Assert.That(
            names, Does.Not.Contain("IReplayEnvironmentFactory"),
            "declared with the recording module, not in the initial SDK surface");
    }
}
