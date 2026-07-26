using System;
using NUnit.Framework;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Codec.CanonicalState.Tests;

/// <summary>Disambiguates NUnit's Assert.Throws overloads for lambda arguments.</summary>
internal static class AssertEx
{
    internal static TException Throws<TException>(Action action)
        where TException : Exception =>
        Assert.Throws<TException>(action)!;
}

/// <summary>
/// Shared construction helpers for the codec suite. The golden-vector
/// expectations themselves live only as literals transcribed from
/// GoldenVectors.md — these helpers build inputs, never expected outputs.
/// </summary>
internal static class CodecFixtures
{
    internal static readonly RuntimeIncarnationId Incarnation = new("incarnation-1");

    internal static readonly SourceRevision Revision = new(7);

    internal static ObservationBasis Basis(
        string view = "v", string domain = "d", string scope = "root",
        RuntimeIncarnationId? incarnation = null, SourceRevision? revision = null) => new(
        incarnation ?? Incarnation,
        revision ?? Revision,
        new ViewContractRef(new ViewContractId(view), new ContractVersion(1, 0)),
        new SecurityDomainId(domain),
        scope);

    internal static ObservationMaterialization Minimal(
        string view = "v", string domain = "d", string scope = "root",
        RuntimeIncarnationId? incarnation = null, SourceRevision? revision = null) => new(
        Basis(view, domain, scope, incarnation, revision),
        ValueList<MaterializedNode>.Empty,
        ValueList<MaterializedSource>.Empty,
        CompletenessMap.Complete);

    /// <summary>The GoldenVectors.md vector-2 world, built in permutable input order.</summary>
    internal static ObservationMaterialization Representative(bool permuteInputOrder = false)
    {
        var attributes = new[]
        {
            new MaterializedAttribute("label", FieldValue.Of("Save"), redacted: false),
            new MaterializedAttribute("secret", default, redacted: true),
        };
        if (permuteInputOrder)
        {
            Array.Reverse(attributes);
        }

        var node = new MaterializedNode(
            new AuthorKey("save"),
            NodeRole.Button,
            new AuthorKey("panel"),
            ValueList<MaterializedAttribute>.From(attributes),
            ValueList<MaterializedCapability>.From(new[]
            {
                new MaterializedCapability(
                    new CapabilityContractRef(new CapabilityContractId("Invoke"), new ContractVersion(1, 0)),
                    available: true),
            }),
            visibleChildCount: 2);
        var source = new MaterializedSource(
            new StateSourceKey("inventory"),
            new StateSourceContractRef(new StateSourceContractId("inventory"), new ContractVersion(1, 0)),
            ValueList<NamedField>.From(new[] { new NamedField("count", FieldValue.Of(5L)) }),
            ValueList<string>.From(new[] { "secret" }),
            omission: null);
        return new ObservationMaterialization(
            Basis("agent-standard", "agent-domain"),
            ValueList<MaterializedNode>.From(new[] { node }),
            ValueList<MaterializedSource>.From(new[] { source }),
            CompletenessMap.From(
                new[]
                {
                    new CompletenessEntry(new FieldPath("nodes/cut"), CompletenessReason.BudgetTruncated),
                },
                maxEntries: 2,
                rootTruncated: true));
    }

    internal static byte[] FromHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    internal static string ToHex(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
