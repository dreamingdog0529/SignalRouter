using System;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// semantic-model.md §1–§2, §7–§8 and security-resources.md §4 — descriptor
/// constructor invariants: default-deny exposure, uniqueness rules, and the
/// sensitivity model.
/// </summary>
public sealed class CatalogDescriptorTests
{
    [Test]
    public void NothingIsVisibleByDefault()
    {
        Assert.That(ExposurePolicy.Hidden.IsVisibleTo(new SecurityDomainId("agent")), Is.False);
        var exposed = new ExposurePolicy(ValueList<SecurityDomainId>.From(new[]
        {
            new SecurityDomainId("agent"),
        }));
        Assert.That(exposed.IsVisibleTo(new SecurityDomainId("agent")), Is.True);
        Assert.That(exposed.IsVisibleTo(new SecurityDomainId("record")), Is.False);
    }

    [Test]
    public void ArgumentSchemaRejectsDuplicateAndCollectionFields()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new ArgumentSchema(
            ValueList<ArgumentField>.From(new[]
            {
                new ArgumentField("a", FieldType.String, true, Sensitivity.Standard),
                new ArgumentField("a", FieldType.Integer, false, Sensitivity.Standard),
            })));
        AssertEx.Throws<ArgumentException>(() => _ = new ArgumentField(
            "items", FieldType.KeyedCollection, true, Sensitivity.Standard));
    }

    [Test]
    public void NodeRegistrationEnforcesUniquenessAndNonDefaults()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new NodeRegistration(
            authorKey: null,
            NodeRole.Button,
            parent: null,
            ValueList<NodeAttribute>.From(new[]
            {
                new NodeAttribute("label", FieldValue.Of("Save"), Sensitivity.Standard),
                new NodeAttribute("label", FieldValue.Of("Other"), Sensitivity.Standard),
            }),
            ValueList<CapabilityDeclaration>.Empty,
            ExposurePolicy.Hidden));

        AssertEx.Throws<ArgumentException>(() => _ = new NodeRegistration(
            authorKey: null,
            NodeRole.Button,
            parent: null,
            ValueList<NodeAttribute>.Empty,
            ValueList<CapabilityDeclaration>.From(new[]
            {
                new CapabilityDeclaration(TestData.Capability, true),
                new CapabilityDeclaration(TestData.Capability, false),
            }),
            ExposurePolicy.Hidden));
    }

    [Test]
    public void CapabilityDescriptorBindsContractArgumentsAndProfile()
    {
        var descriptor = new CapabilityContractDescriptor(
            TestData.Capability,
            ArgumentSchema.Empty,
            precondition: null,
            TestData.CompletionProfile);
        Assert.That(descriptor.Precondition, Is.Null);
        AssertEx.Throws<ArgumentException>(() => _ = new CapabilityContractDescriptor(
            default, ArgumentSchema.Empty, null, TestData.CompletionProfile));
    }

    [Test]
    public void StateSourceDescriptorEnforcesUniqueFieldsAndPositiveCeiling()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new StateSourceContractDescriptor(
            new StateSourceContractRef(new StateSourceContractId("inventory"), new ContractVersion(1, 0)),
            ValueList<SourceFieldSchema>.From(new[]
            {
                new SourceFieldSchema("count", FieldType.Integer, Sensitivity.Standard),
                new SourceFieldSchema("count", FieldType.Integer, Sensitivity.Standard),
            }),
            agentVisible: true,
            recordVisible: true,
            maxDocumentBytes: 1024));

        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new StateSourceContractDescriptor(
            new StateSourceContractRef(new StateSourceContractId("inventory"), new ContractVersion(1, 0)),
            ValueList<SourceFieldSchema>.Empty,
            agentVisible: false,
            recordVisible: true,
            maxDocumentBytes: 0));
    }
}
