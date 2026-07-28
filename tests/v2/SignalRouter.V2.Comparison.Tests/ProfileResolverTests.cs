using NUnit.Framework;
using SignalRouter.V2.Comparison;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Comparison.Tests;

/// <summary>
/// Profile resolution (recording-replay.md §5; semantic-model.md §5): the
/// artifact's embedded document is authoritative for its own version; only a
/// declared-projectable older version migrates, and every refusal is one of the
/// reserved reasons (guarantees.md §3.5).
/// </summary>
public sealed class ProfileResolverTests
{
    private static readonly ViewContractRef RecordView =
        new(new ViewContractId("record-standard"), new ContractVersion(1, 0));

    private static ReplayComparisonProfile Profile(
        string id, ContractVersion version, ContractVersion[]? projectableFrom = null)
    {
        return new ReplayComparisonProfile(
            new ReplayComparisonProfileRef(new ReplayComparisonProfileId(id), version),
            RecordView,
            "root",
            new RedactionPolicyId("default-redaction"),
            ReplayComparisonProfile.MatchByAuthorKey,
            ValueArray<ComparedNodeRule>.Empty,
            ValueArray<ComparedSourceRule>.Empty,
            ValueArray<ItemKeyRule>.Empty,
            ValueArray<CollectionRule>.Empty,
            ValueArray<NormalizationRule>.Empty,
            requireCompleteForScope: true,
            ValueArray<ExtensionPolicy>.Empty,
            projectableFrom == null
                ? ValueArray<ContractVersion>.Empty
                : ValueArray<ContractVersion>.From(projectableFrom));
    }

    private sealed class ProjectToSupported : IProfileMigration
    {
        private readonly ReplayComparisonProfile target;

        internal ProjectToSupported(ReplayComparisonProfile target)
        {
            this.target = target;
        }

        public ReplayComparisonProfile Project(ReplayComparisonProfile recorded) => target;
    }

    private sealed class ProjectToWrongVersion : IProfileMigration
    {
        public ReplayComparisonProfile Project(ReplayComparisonProfile recorded) => recorded;
    }

    [Test]
    public void TheSameVersionResolvesToTheArtifactDocument()
    {
        var recorded = Profile("strict", new ContractVersion(1, 0));
        var supported = Profile("strict", new ContractVersion(1, 0));
        var resolution = ProfileResolver.Resolve(recorded, supported, new ComparisonVocabulary());
        Assert.That(resolution.IsResolved, Is.True);
        Assert.That(resolution.IncomparableReason, Is.Null);
        Assert.That(
            resolution.Effective, Is.SameAs(recorded),
            "the embedded document is authoritative — registry drift never rewrites a recording");
    }

    [Test]
    public void ADifferentProfileIdIsUnsupported()
    {
        var resolution = ProfileResolver.Resolve(
            Profile("other", new ContractVersion(1, 0)),
            Profile("strict", new ContractVersion(1, 0)),
            new ComparisonVocabulary());
        Assert.That(resolution.IsResolved, Is.False);
        Assert.That(
            resolution.IncomparableReason,
            Is.EqualTo(IncomparableReason.UnsupportedProfileVersion));
        Assert.That(resolution.Effective, Is.Null);
    }

    [Test]
    public void ANewerRecordedVersionIsUnsupported()
    {
        var resolution = ProfileResolver.Resolve(
            Profile("strict", new ContractVersion(3, 0)),
            Profile("strict", new ContractVersion(2, 0), new[] { new ContractVersion(1, 0) }),
            new ComparisonVocabulary());
        Assert.That(
            resolution.IncomparableReason,
            Is.EqualTo(IncomparableReason.UnsupportedProfileVersion));
    }

    [Test]
    public void AnUndeclaredOlderVersionIsUnsupported()
    {
        var resolution = ProfileResolver.Resolve(
            Profile("strict", new ContractVersion(1, 0)),
            Profile("strict", new ContractVersion(2, 0)),
            new ComparisonVocabulary());
        Assert.That(
            resolution.IncomparableReason,
            Is.EqualTo(IncomparableReason.UnsupportedProfileVersion));
    }

    [Test]
    public void ADeclaredVersionWithoutARegisteredMigrationIsMissingMigration()
    {
        var resolution = ProfileResolver.Resolve(
            Profile("strict", new ContractVersion(1, 0)),
            Profile("strict", new ContractVersion(2, 0), new[] { new ContractVersion(1, 0) }),
            new ComparisonVocabulary());
        Assert.That(
            resolution.IncomparableReason, Is.EqualTo(IncomparableReason.MissingMigration));
    }

    [Test]
    public void ARegisteredMigrationProjectsOntoTheSupportedVersion()
    {
        var recorded = Profile("strict", new ContractVersion(1, 0));
        var supported = Profile("strict", new ContractVersion(2, 0), new[] { new ContractVersion(1, 0) });
        var vocabulary = new ComparisonVocabulary();
        vocabulary.RegisterMigration(recorded.Reference, new ProjectToSupported(supported));

        var resolution = ProfileResolver.Resolve(recorded, supported, vocabulary);
        Assert.That(resolution.IsResolved, Is.True);
        Assert.That(resolution.Effective!.Reference, Is.EqualTo(supported.Reference));
    }

    [Test]
    public void AMigrationAnsweringTheWrongVersionIsMissingMigration()
    {
        var recorded = Profile("strict", new ContractVersion(1, 0));
        var supported = Profile("strict", new ContractVersion(2, 0), new[] { new ContractVersion(1, 0) });
        var vocabulary = new ComparisonVocabulary();
        vocabulary.RegisterMigration(recorded.Reference, new ProjectToWrongVersion());

        var resolution = ProfileResolver.Resolve(recorded, supported, vocabulary);
        Assert.That(
            resolution.IncomparableReason, Is.EqualTo(IncomparableReason.MissingMigration));
    }
}
