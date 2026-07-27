using System;
using System.Collections.Generic;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// Order parity for the piecewise capability comparer (plan P3c): the canonical
/// capability order inside a node participates in the canonical bytes, so
/// <see cref="MaterializedCapability.CompareCanonical"/> must order exactly as
/// ordinal comparison of the historical <c>id@major.minor</c> string key —
/// including identifiers that themselves contain '@', '.', and digits, and
/// version numbers whose decimal rendering interleaves with identifier
/// characters.
/// </summary>
public sealed class CapabilityOrderParityTests
{
    private static MaterializedCapability Capability(string id, int major, int minor) => new(
        new CapabilityContractRef(new CapabilityContractId(id), new ContractVersion(major, minor)),
        available: true);

    private static string LegacyKey(MaterializedCapability capability) =>
        capability.Contract.Id.Value + "@" + capability.Contract.Version;

    [Test]
    public void PiecewiseComparisonMatchesTheLegacyStringKeyOverTheCorpus()
    {
        // Adversarial identifiers: '@' inside ids, ids that are prefixes of one
        // another, digits adjoining the version boundary, multi-digit versions.
        var ids = new[]
        {
            "Invoke", "Invok", "Invoke2", "Invoke@x", "Invoke@", "a", "a@1", "a@1.0",
            "Cap", "Cap2", "Cap@", "z", "0", "@", "@@", "com.example.app:Rotate",
        };
        var versions = new[] { (0, 0), (1, 0), (1, 9), (1, 10), (2, 3), (9, 0), (10, 0), (123, 456) };
        var corpus = new List<MaterializedCapability>();
        foreach (var id in ids)
        {
            foreach (var (major, minor) in versions)
            {
                corpus.Add(Capability(id, major, minor));
            }
        }

        for (var i = 0; i < corpus.Count; i++)
        {
            for (var j = 0; j < corpus.Count; j++)
            {
                var expected = Math.Sign(string.CompareOrdinal(LegacyKey(corpus[i]), LegacyKey(corpus[j])));
                var actual = Math.Sign(MaterializedCapability.CompareCanonical(corpus[i], corpus[j]));
                Assert.That(
                    actual, Is.EqualTo(expected),
                    $"order diverged for '{LegacyKey(corpus[i])}' vs '{LegacyKey(corpus[j])}'");
            }
        }
    }

    [Test]
    public void ConstructionKeepsAlreadySortedImmutableInputWithoutReordering()
    {
        var sorted = ValueArray<MaterializedCapability>.From(new[]
        {
            Capability("Cap2", 1, 0), // "Cap2@1.0" < "Cap@1.0" ('2' < '@')
            Capability("Cap", 1, 0),
        });
        var node = new MaterializedNode(
            new AuthorKey("n"), NodeRole.Button, null,
            ValueArray<MaterializedAttribute>.Empty, sorted, 0);

        Assert.That(node.Capabilities, Is.EqualTo(sorted), "canonical order verified, input kept");

        var reversed = ValueArray<MaterializedCapability>.From(new[]
        {
            Capability("Cap", 1, 0),
            Capability("Cap2", 1, 0),
        });
        var normalized = new MaterializedNode(
            new AuthorKey("n"), NodeRole.Button, null,
            ValueArray<MaterializedAttribute>.Empty, reversed, 0);

        Assert.That(
            normalized.Capabilities[0].Contract.Id.Value, Is.EqualTo("Cap2"),
            "unordered input is normalized to the same canonical order");
    }
}
