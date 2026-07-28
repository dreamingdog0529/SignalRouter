using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace SignalRouter.V2.ApiSurface.Tests;

/// <summary>
/// Pins the exported surface of every v2 production assembly against a
/// checked-in baseline. A failing diff here is the review gate for public-API
/// changes: the baseline may only be regenerated together with the ADR (or PR
/// review) that approves the break — never as an incidental fixup. Regenerate
/// with the environment variable SIGNALROUTER_API_BASELINE_REGENERATE=1 and a
/// normal `dotnet test` run of this project, then commit the diff.
/// </summary>
[TestFixture]
public sealed class ApiSurfaceSnapshotTests
{
    private static readonly string[] AssemblyNames =
    {
        "SignalRouter.V2.AdapterSdk",
        "SignalRouter.V2.Codec.CanonicalState",
        "SignalRouter.V2.Codec.Recording",
        "SignalRouter.V2.Comparison",
        "SignalRouter.V2.Recording",
        "SignalRouter.V2.Replay",
        "SignalRouter.V2.Contracts",
        "SignalRouter.V2.Kernel",
        "SignalRouter.V2.ReferenceAdapter",
        "SignalRouter.V2.Tck",
    };

    [TestCaseSource(nameof(AssemblyNames))]
    public void TheExportedSurfaceMatchesTheBaseline(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var rendered = ApiSurfaceRenderer.Render(assembly);
        var baselinePath = Path.Combine(BaselineDirectory(), assemblyName + ".txt");

        if (Environment.GetEnvironmentVariable("SIGNALROUTER_API_BASELINE_REGENERATE") == "1")
        {
            File.WriteAllText(baselinePath, rendered);
            Assert.Pass($"Baseline regenerated at {baselinePath}; review and commit the diff.");
        }

        Assert.That(
            File.Exists(baselinePath), Is.True,
            $"Missing baseline {baselinePath}. Regenerate with SIGNALROUTER_API_BASELINE_REGENERATE=1.");
        var baseline = File.ReadAllText(baselinePath).Replace("\r\n", "\n");
        Assert.That(
            rendered, Is.EqualTo(baseline),
            $"The exported surface of {assemblyName} changed. If the change is an approved break, " +
            "regenerate the baseline with SIGNALROUTER_API_BASELINE_REGENERATE=1 and commit the diff " +
            "alongside the approving review; otherwise revert the surface change.");
    }

    /// <summary>Locates the checked-in baseline directory by walking up to the repository root.</summary>
    private static string BaselineDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SignalRouter.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "Could not locate the repository root from the test base directory.");
        var baselines = Path.Combine(
            directory!.FullName, "tests", "v2", "SignalRouter.V2.ApiSurface.Tests", "Baselines");
        Directory.CreateDirectory(baselines);
        return baselines;
    }
}
