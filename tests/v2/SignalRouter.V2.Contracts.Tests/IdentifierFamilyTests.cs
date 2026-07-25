using System;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// semantic-model.md §3–§5: identifier value semantics — ordinal, never-normalized
/// equality; the ContentId triple; monotonic comparables; default/empty rejection.
/// </summary>
public sealed class IdentifierFamilyTests
{
    [Test]
    public void AuthorKeysCompareOrdinallyAndAreNeverNormalized()
    {
        Assert.That(new AuthorKey("Save"), Is.EqualTo(new AuthorKey("Save")));
        Assert.That(new AuthorKey("Save"), Is.Not.EqualTo(new AuthorKey("save")));
        Assert.That(new AuthorKey("Straße"), Is.Not.EqualTo(new AuthorKey("Strasse")));
    }

    [Test]
    public void IdentifiersRejectEmptyAndControlCharacterValues()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new RequestId(""));
        AssertEx.Throws<ArgumentException>(() => _ = new AuthorKey("line\nbreak"));
        AssertEx.Throws<ArgumentNullException>(() => _ = new OperationId(null!));
    }

    [Test]
    public void DefaultIdentifiersAreDetectableAndRefuseValueAccess()
    {
        var value = default(RequestId);
        Assert.That(value.IsDefault, Is.True);
        AssertEx.Throws<InvalidOperationException>(() => _ = value.Value);
    }

    [Test]
    public void ContentIdEqualityCoversTheWholeTriple()
    {
        var digest = DigestValue.From(new byte[] { 1, 2, 3 });
        var contentId = new ContentId("sha256", 1, digest);
        Assert.That(contentId, Is.EqualTo(new ContentId("sha256", 1, DigestValue.From(new byte[] { 1, 2, 3 }))));
        Assert.That(contentId, Is.Not.EqualTo(new ContentId("sha512", 1, digest)));
        Assert.That(contentId, Is.Not.EqualTo(new ContentId("sha256", 2, digest)));
        Assert.That(contentId, Is.Not.EqualTo(new ContentId("sha256", 1, DigestValue.From(new byte[] { 9 }))));
    }

    [Test]
    public void ContentIdRejectsDefaultComponents()
    {
        AssertEx.Throws<ArgumentException>(() => _ = new ContentId("sha256", 1, default));
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => _ = new ContentId("sha256", 0, DigestValue.From(new byte[] { 1 })));
    }

    [Test]
    public void DigestValueCopiesDefensivelyAndComparesStructurally()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var digest = DigestValue.From(bytes);
        bytes[0] = 99;
        Assert.That(digest, Is.EqualTo(DigestValue.From(new byte[] { 1, 2, 3 })));

        var exposed = digest.ToArray();
        exposed[0] = 77;
        Assert.That(digest, Is.EqualTo(DigestValue.From(new byte[] { 1, 2, 3 })));
    }

    [Test]
    public void OrderedIdentifiersCompareMonotonically()
    {
        Assert.That(new LogicalOrder(1), Is.LessThan(new LogicalOrder(2)));
        Assert.That(new SourceRevision(5), Is.GreaterThan(new SourceRevision(4)));
        Assert.That(new EvidenceSequence(7) < new EvidenceSequence(8), Is.True);
        Assert.That(new ViewSequence(3), Is.EqualTo(new ViewSequence(3)));
    }

    [Test]
    public void ContractVersionOrdersMajorThenMinor()
    {
        Assert.That(new ContractVersion(1, 9), Is.LessThan(new ContractVersion(2, 0)));
        Assert.That(new ContractVersion(1, 1), Is.GreaterThan(new ContractVersion(1, 0)));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new ContractVersion(-1, 0));
    }

    [Test]
    public void NodeRefIsScopedToItsIncarnation()
    {
        var reference = new NodeRef(TestData.Incarnation, 42);
        Assert.That(reference, Is.EqualTo(new NodeRef(TestData.Incarnation, 42)));
        Assert.That(reference, Is.Not.EqualTo(new NodeRef(new RuntimeIncarnationId("incarnation-2"), 42)));
        AssertEx.Throws<ArgumentException>(() => _ = new NodeRef(default, 42));
    }

    [Test]
    public void ContractRefsRequireANonDefaultId()
    {
        AssertEx.Throws<ArgumentException>(
            () => _ = new CapabilityContractRef(default, new ContractVersion(1, 0)));
    }
}
