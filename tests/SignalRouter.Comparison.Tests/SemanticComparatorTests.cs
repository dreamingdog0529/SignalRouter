using System;
using System.Collections.Generic;
using NUnit.Framework;
using SignalRouter.Comparison;
using SignalRouter.Contracts;

namespace SignalRouter.Comparison.Tests;

/// <summary>
/// The typed comparator (recording-replay.md §5.1–§5.2): exact equality over
/// the profile's field set with absence, null, unknown, and redaction as four
/// distinct inputs; Diverged carries a structured diff in comparison order;
/// Incomparable is an honest refusal, never a guess. Expectations are
/// transcribed from the spec, never derived from the comparator.
/// </summary>
public sealed class SemanticComparatorTests
{
    private static readonly ViewContractRef RecordView =
        new(new ViewContractId("record-standard"), new ContractVersion(1, 0));

    private static readonly SecurityDomainId RecordDomain = new("record-domain");

    private static readonly CapabilityContractRef Invoke =
        new(new CapabilityContractId("Invoke"), new ContractVersion(1, 0));

    private static readonly StateSourceContractRef InventoryContract =
        new(new StateSourceContractId("inventory"), new ContractVersion(1, 0));

    private static ObservationBasis Basis(string scope = "root") => new(
        new RuntimeIncarnationId("incarnation-1"),
        new SourceRevision(5),
        RecordView,
        RecordDomain,
        scope);

    private static MaterializedNode Node(
        string key,
        string role = "button",
        string? parent = null,
        int children = 0,
        MaterializedAttribute[]? attributes = null,
        MaterializedCapability[]? capabilities = null)
    {
        return new MaterializedNode(
            new AuthorKey(key),
            new NodeRole(role),
            parent == null ? null : new AuthorKey(parent),
            attributes == null
                ? ValueArray<MaterializedAttribute>.Empty
                : ValueArray<MaterializedAttribute>.From(attributes),
            capabilities == null
                ? ValueArray<MaterializedCapability>.Empty
                : ValueArray<MaterializedCapability>.From(capabilities),
            children);
    }

    private static MaterializedSource Source(
        string key,
        NamedField[]? fields = null,
        string[]? redacted = null,
        CompletenessReason? omission = null)
    {
        return new MaterializedSource(
            new StateSourceKey(key),
            InventoryContract,
            fields == null ? ValueArray<NamedField>.Empty : ValueArray<NamedField>.From(fields),
            redacted == null ? ValueArray<string>.Empty : ValueArray<string>.From(redacted),
            omission);
    }

    private static ObservationMaterialization State(
        MaterializedNode[]? nodes = null,
        MaterializedSource[]? sources = null,
        CompletenessMap? completeness = null)
    {
        return new ObservationMaterialization(
            Basis(),
            nodes == null ? ValueArray<MaterializedNode>.Empty : ValueArray<MaterializedNode>.From(nodes),
            sources == null
                ? ValueArray<MaterializedSource>.Empty
                : ValueArray<MaterializedSource>.From(sources),
            completeness ?? CompletenessMap.Complete);
    }

    private static ReplayComparisonProfile Profile(
        ComparedNodeRule[]? nodeRules = null,
        ComparedSourceRule[]? sourceRules = null,
        ItemKeyRule[]? itemKeyRules = null,
        CollectionRule[]? collectionRules = null,
        NormalizationRule[]? normalizationRules = null,
        bool requireCompleteForScope = true,
        ExtensionPolicy[]? extensions = null,
        string nodeMatching = ReplayComparisonProfile.MatchByAuthorKey,
        ContractVersion? version = null,
        ContractVersion[]? projectableFrom = null)
    {
        return new ReplayComparisonProfile(
            new ReplayComparisonProfileRef(
                new ReplayComparisonProfileId("strict"), version ?? new ContractVersion(1, 0)),
            RecordView,
            "root",
            new RedactionPolicyId("default-redaction"),
            nodeMatching,
            nodeRules == null
                ? ValueArray<ComparedNodeRule>.Empty
                : ValueArray<ComparedNodeRule>.From(nodeRules),
            sourceRules == null
                ? ValueArray<ComparedSourceRule>.Empty
                : ValueArray<ComparedSourceRule>.From(sourceRules),
            itemKeyRules == null
                ? ValueArray<ItemKeyRule>.Empty
                : ValueArray<ItemKeyRule>.From(itemKeyRules),
            collectionRules == null
                ? ValueArray<CollectionRule>.Empty
                : ValueArray<CollectionRule>.From(collectionRules),
            normalizationRules == null
                ? ValueArray<NormalizationRule>.Empty
                : ValueArray<NormalizationRule>.From(normalizationRules),
            requireCompleteForScope,
            extensions == null
                ? ValueArray<ExtensionPolicy>.Empty
                : ValueArray<ExtensionPolicy>.From(extensions),
            projectableFrom == null
                ? ValueArray<ContractVersion>.Empty
                : ValueArray<ContractVersion>.From(projectableFrom));
    }

    private static SemanticComparator Comparator() => new(new ComparisonVocabulary());

    private static ObservationMaterialization Representative() => State(
        nodes: new[]
        {
            Node(
                "save",
                attributes: new[]
                {
                    new MaterializedAttribute("label", FieldValue.Of("Save"), redacted: false),
                    new MaterializedAttribute("token", default, redacted: true),
                },
                capabilities: new[] { new MaterializedCapability(Invoke, available: true) },
                children: 2),
            Node("secret", role: "container", parent: "save"),
        },
        sources: new[]
        {
            Source(
                "inventory",
                fields: new[] { new NamedField("count", FieldValue.Of(5L)) },
                redacted: new[] { "secret" }),
        });

    // ── Equal ────────────────────────────────────────────────────────────────

    [Test]
    public void IdenticalCompleteInputsCompareEqual()
    {
        var result = Comparator().CompareState(Representative(), Representative(), Profile());
        Assert.That(result.Outcome, Is.EqualTo(ReplayComparisonOutcome.Equal));
        Assert.That(result.Diff, Is.Null, "a diff never exists for Equal (guarantees.md §3.3)");
    }

    [Test]
    public void IdenticalInputsCompareEqualUnderNarrowingRulesAndIdentityNormalization()
    {
        var profile = Profile(
            nodeRules: new[]
            {
                new ComparedNodeRule("button", ValueArray<string>.From(new[] { "label" })),
            },
            sourceRules: new[]
            {
                new ComparedSourceRule(
                    new StateSourceKey("inventory"), ValueArray<string>.From(new[] { "count" })),
            },
            normalizationRules: new[]
            {
                new NormalizationRule("nodes/save/attributes/label", NormalizationRule.Identity),
            },
            extensions: new[] { new ExtensionPolicy("futureext", mandatory: false) });
        var result = Comparator().CompareState(Representative(), Representative(), profile);
        Assert.That(result.Outcome, Is.EqualTo(ReplayComparisonOutcome.Equal));
    }

    // ── The four comparator inputs (recording-replay.md §5.2) ────────────────

    private static ObservationMaterialization WithLabelState(string state)
    {
        var attributes = state switch
        {
            "value" => new[] { new MaterializedAttribute("label", FieldValue.Of("Save"), false) },
            "null" => new[] { new MaterializedAttribute("label", FieldValue.Null, false) },
            "redacted" => new[] { new MaterializedAttribute("label", default, true) },
            _ => Array.Empty<MaterializedAttribute>(),
        };
        return State(nodes: new[] { Node("save", attributes: attributes) });
    }

    [Test]
    public void TheFourFieldStatesAreDistinctPairwise()
    {
        var states = new[] { "value", "null", "absent", "redacted" };
        foreach (var left in states)
        {
            foreach (var right in states)
            {
                var result = Comparator().CompareState(
                    WithLabelState(left), WithLabelState(right), Profile());
                if (left == right)
                {
                    Assert.That(
                        result.Outcome, Is.EqualTo(ReplayComparisonOutcome.Equal),
                        $"{left} vs {right}");
                }
                else
                {
                    Assert.That(
                        result.Outcome, Is.EqualTo(ReplayComparisonOutcome.Diverged),
                        $"{left} vs {right}");
                    Assert.That(result.Diff!.Entries[0].DetailCode, Is.EqualTo("StateMismatch"));
                    Assert.That(
                        result.Diff.Entries[0].Path, Is.EqualTo("nodes/save/attributes/label"));
                }
            }
        }
    }

    [Test]
    public void DifferingValuesDivergeWithRenderedValues()
    {
        var left = WithLabelState("value");
        var right = State(nodes: new[]
        {
            Node("save", attributes: new[]
            {
                new MaterializedAttribute("label", FieldValue.Of("Saved"), false),
            }),
        });
        var result = Comparator().CompareState(left, right, Profile());
        Assert.That(result.Outcome, Is.EqualTo(ReplayComparisonOutcome.Diverged));
        var entry = result.Diff!.Entries[0];
        Assert.That(entry.DetailCode, Is.EqualTo("ValueMismatch"));
        Assert.That(entry.Recorded, Is.EqualTo("value:Save"));
        Assert.That(entry.Actual, Is.EqualTo("value:Saved"));
    }

    // ── Structure ────────────────────────────────────────────────────────────

    [Test]
    public void MissingAndUnexpectedNodesDiverge()
    {
        var both = State(nodes: new[] { Node("alpha"), Node("omega") });
        var onlyAlpha = State(nodes: new[] { Node("alpha") });

        var missing = Comparator().CompareState(both, onlyAlpha, Profile());
        Assert.That(missing.Diff!.Entries[0].DetailCode, Is.EqualTo("NodeMissing"));
        Assert.That(missing.Diff.Entries[0].Path, Is.EqualTo("nodes/omega"));

        var unexpected = Comparator().CompareState(onlyAlpha, both, Profile());
        Assert.That(unexpected.Diff!.Entries[0].DetailCode, Is.EqualTo("NodeUnexpected"));
    }

    [Test]
    public void RoleParentAndChildCountDiverge()
    {
        var left = State(nodes: new[] { Node("a"), Node("save", parent: "a", children: 1) });
        var right = State(nodes: new[]
        {
            Node("a"), Node("save", role: "textbox", parent: null, children: 3),
        });
        var result = Comparator().CompareState(left, right, Profile());
        var codes = new List<string>();
        foreach (var entry in result.Diff!.Entries)
        {
            codes.Add(entry.Path + ":" + entry.DetailCode);
        }

        Assert.That(codes, Is.EqualTo(new[]
        {
            "nodes/save/role:ValueMismatch",
            "nodes/save/parent:ValueMismatch",
            "nodes/save/children:CountMismatch",
        }));
    }

    [Test]
    public void CapabilityVersionAndAvailabilityDiverge()
    {
        var left = State(nodes: new[]
        {
            Node("save", capabilities: new[] { new MaterializedCapability(Invoke, true) }),
        });
        var right = State(nodes: new[]
        {
            Node("save", capabilities: new[]
            {
                new MaterializedCapability(
                    new CapabilityContractRef(new CapabilityContractId("Invoke"), new ContractVersion(2, 0)),
                    available: false),
            }),
        });
        var result = Comparator().CompareState(left, right, Profile());
        Assert.That(result.Diff!.Entries[0].DetailCode, Is.EqualTo("VersionMismatch"));
        Assert.That(result.Diff.Entries[1].DetailCode, Is.EqualTo("AvailabilityMismatch"));
    }

    [Test]
    public void ANodeRuleNarrowsTheComparedAttributeSet()
    {
        var left = State(nodes: new[]
        {
            Node("save", attributes: new[]
            {
                new MaterializedAttribute("label", FieldValue.Of("Save"), false),
                new MaterializedAttribute("tooltip", FieldValue.Of("old"), false),
            }),
        });
        var right = State(nodes: new[]
        {
            Node("save", attributes: new[]
            {
                new MaterializedAttribute("label", FieldValue.Of("Save"), false),
                new MaterializedAttribute("tooltip", FieldValue.Of("new"), false),
            }),
        });
        var narrowed = Profile(nodeRules: new[]
        {
            new ComparedNodeRule("button", ValueArray<string>.From(new[] { "label" })),
        });

        Assert.That(
            Comparator().CompareState(left, right, narrowed).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Equal),
            "the rule excludes the differing tooltip");
        Assert.That(
            Comparator().CompareState(left, right, Profile()).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Diverged),
            "default-strict compares every field");
    }

    // ── Sources ──────────────────────────────────────────────────────────────

    [Test]
    public void SourceRulesSelectTheStrictScopeSourceSet()
    {
        var left = State(sources: new[]
        {
            Source("inventory", fields: new[] { new NamedField("count", FieldValue.Of(1L)) }),
            Source("noise", fields: new[] { new NamedField("count", FieldValue.Of(10L)) }),
        });
        var right = State(sources: new[]
        {
            Source("inventory", fields: new[] { new NamedField("count", FieldValue.Of(1L)) }),
            Source("noise", fields: new[] { new NamedField("count", FieldValue.Of(20L)) }),
        });
        var scoped = Profile(sourceRules: new[]
        {
            new ComparedSourceRule(
                new StateSourceKey("inventory"), ValueArray<string>.From(new[] { "count" })),
        });

        Assert.That(
            Comparator().CompareState(left, right, scoped).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Equal),
            "unlisted sources are outside strict scope");
        Assert.That(
            Comparator().CompareState(left, right, Profile()).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Diverged),
            "default-strict compares every source");
    }

    [Test]
    public void DeterministicSourceAbsenceComparesAsAState()
    {
        var unavailable = State(sources: new[]
        {
            Source("inventory", omission: CompletenessReason.SourceUnavailable),
        });
        var present = State(sources: new[]
        {
            Source("inventory", fields: new[] { new NamedField("count", FieldValue.Of(1L)) }),
        });

        Assert.That(
            Comparator().CompareState(unavailable, unavailable, Profile()).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Equal));
        var mixed = Comparator().CompareState(unavailable, present, Profile());
        Assert.That(mixed.Outcome, Is.EqualTo(ReplayComparisonOutcome.Diverged));
        Assert.That(mixed.Diff!.Entries[0].DetailCode, Is.EqualTo("StateMismatch"));
        Assert.That(mixed.Diff.Entries[0].Recorded, Is.EqualTo("unavailable"));
    }

    [Test]
    public void OutOfTierOmissionsAreIncomparable()
    {
        var stale = State(sources: new[] { Source("inventory", omission: CompletenessReason.Stale) });
        var fresh = State(sources: new[]
        {
            Source("inventory", fields: new[] { new NamedField("count", FieldValue.Of(1L)) }),
        });
        var result = Comparator().CompareState(stale, fresh, Profile());
        Assert.That(result.Outcome, Is.EqualTo(ReplayComparisonOutcome.Incomparable(new IncomparableReason("Stale"))));

        var unsupported = State(sources: new[]
        {
            Source("inventory", omission: CompletenessReason.UnsupportedContract),
        });
        Assert.That(
            Comparator().CompareState(unsupported, fresh, Profile()).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(new IncomparableReason("UnsupportedContract"))));
    }

    [Test]
    public void ASourceContractMismatchDiverges()
    {
        var left = State(sources: new[]
        {
            Source("inventory", fields: new[] { new NamedField("count", FieldValue.Of(1L)) }),
        });
        var right = State(sources: new[]
        {
            new MaterializedSource(
                new StateSourceKey("inventory"),
                new StateSourceContractRef(new StateSourceContractId("inventory"), new ContractVersion(2, 0)),
                ValueArray<NamedField>.From(new[] { new NamedField("count", FieldValue.Of(1L)) }),
                ValueArray<string>.Empty,
                omission: null),
        });
        var result = Comparator().CompareState(left, right, Profile());
        Assert.That(result.Diff!.Entries[0].DetailCode, Is.EqualTo("ContractMismatch"));
        Assert.That(result.Diff.Entries[0].Recorded, Is.EqualTo("inventory@1.0"));
        Assert.That(result.Diff.Entries[0].Actual, Is.EqualTo("inventory@2.0"));
    }

    // ── Normalization ────────────────────────────────────────────────────────

    private sealed class UppercaseNormalizer : IValueNormalizer
    {
        public FieldValue Normalize(FieldValue value) =>
            value.Kind == FieldValueKind.String
                ? FieldValue.Of(value.AsString.ToUpperInvariant())
                : value;
    }

    [Test]
    public void ARegisteredNormalizerAppliesBeforeEquality()
    {
        var vocabulary = new ComparisonVocabulary();
        vocabulary.RegisterNormalizer("Uppercase", new UppercaseNormalizer());
        var comparator = new SemanticComparator(vocabulary);
        var left = WithLabelState("value"); // "Save"
        var right = State(nodes: new[]
        {
            Node("save", attributes: new[]
            {
                new MaterializedAttribute("label", FieldValue.Of("SAVE"), false),
            }),
        });
        var normalized = Profile(normalizationRules: new[]
        {
            new NormalizationRule("nodes/save/attributes/label", "Uppercase"),
        });

        Assert.That(
            comparator.CompareState(left, right, normalized).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Equal));
        Assert.That(
            comparator.CompareState(left, right, Profile()).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Diverged),
            "without the rule the raw values differ");
    }

    [Test]
    public void AnUnknownNormalizerCodeRefusesTheComparison()
    {
        var profile = Profile(normalizationRules: new[]
        {
            new NormalizationRule("nodes/save/attributes/label", "Nope"),
        });
        var result = Comparator().CompareState(
            WithLabelState("value"), WithLabelState("value"), profile);
        Assert.That(result.Outcome, Is.EqualTo(ReplayComparisonOutcome.Incomparable(new IncomparableReason("UnknownNormalizer"))));
    }

    // ── Incomparable gates ───────────────────────────────────────────────────

    [Test]
    public void RequiredCompletenessRefusesIncompleteInput()
    {
        var incomplete = State(completeness: CompletenessMap.From(
            new[] { new CompletenessEntry(new FieldPath("nodes/save"), CompletenessReason.BudgetTruncated) },
            maxEntries: 8));
        var result = Comparator().CompareState(incomplete, State(), Profile());
        Assert.That(
            result.Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(IncomparableReason.Incompleteness)));
    }

    [Test]
    public void CoincidingUnknownRegionsCompareWithoutTheRequirement()
    {
        CompletenessMap Truncated() => CompletenessMap.From(
            new[] { new CompletenessEntry(new FieldPath("nodes/save"), CompletenessReason.BudgetTruncated) },
            maxEntries: 8);
        var relaxed = Profile(requireCompleteForScope: false);

        Assert.That(
            Comparator().CompareState(
                State(completeness: Truncated()), State(completeness: Truncated()), relaxed).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Equal),
            "unknown is the fourth input at region granularity — coinciding regions compare");
        Assert.That(
            Comparator().CompareState(
                State(completeness: Truncated()), State(), relaxed).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(IncomparableReason.Incompleteness)),
            "differing unknown regions cannot be told apart from divergence");
    }

    [Test]
    public void AMandatoryUnknownExtensionIsIncomparable()
    {
        var profile = Profile(extensions: new[] { new ExtensionPolicy("futureext", mandatory: true) });
        Assert.That(
            Comparator().CompareState(State(), State(), profile).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(IncomparableReason.UnknownMandatoryExtension)));
    }

    [Test]
    public void UnsupportedMatchingAndRulesAreRefused()
    {
        Assert.That(
            Comparator().CompareState(State(), State(), Profile(nodeMatching: "Locator")).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(new IncomparableReason("UnsupportedNodeMatching"))));
        Assert.That(
            Comparator().CompareState(State(), State(), Profile(itemKeyRules: new[]
            {
                new ItemKeyRule("nodes/list/children", "id"),
            })).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(new IncomparableReason("UnsupportedProfileRule"))));
        Assert.That(
            Comparator().CompareState(State(), State(), Profile(collectionRules: new[]
            {
                new CollectionRule("nodes/list/children", CollectionComparison.Ordered),
            })).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(new IncomparableReason("UnsupportedProfileRule"))),
            "v2.0 has no collection-valued fields: accepting Ordered as a no-op would lie");
        Assert.That(
            Comparator().CompareState(State(), State(), Profile(nodeRules: new[]
            {
                new ComparedNodeRule("button", ValueArray<string>.From(new[] { "attributes/label" })),
            })).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(new IncomparableReason("UnsupportedProfileRule"))),
            "a multi-segment rule path would silently select nothing — refused, never fail-open");
    }

    [Test]
    public void AListedSourceAbsentOnBothSidesIsEqualAbsence()
    {
        var scoped = Profile(sourceRules: new[]
        {
            new ComparedSourceRule(
                new StateSourceKey("inventory"), ValueArray<string>.From(new[] { "count" })),
        });
        Assert.That(
            Comparator().CompareState(State(), State(), scoped).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Equal));
    }

    [Test]
    public void MultipleVersionsOfOneCapabilityPairExactly()
    {
        var invoke2 = new CapabilityContractRef(
            new CapabilityContractId("Invoke"), new ContractVersion(2, 0));
        var left = State(nodes: new[]
        {
            Node("save", capabilities: new[]
            {
                new MaterializedCapability(Invoke, true),
                new MaterializedCapability(invoke2, true),
            }),
        });
        var right = State(nodes: new[]
        {
            Node("save", capabilities: new[] { new MaterializedCapability(invoke2, true) }),
        });
        var result = Comparator().CompareState(left, right, Profile());
        Assert.That(result.Outcome, Is.EqualTo(ReplayComparisonOutcome.Diverged));
        Assert.That(
            result.Diff!.Entries.Count, Is.EqualTo(1),
            "1.0 is missing and 2.0 pairs exactly — id-only pairing would mispair the run");
        Assert.That(result.Diff.Entries[0].DetailCode, Is.EqualTo("CapabilityMissing"));
        Assert.That(result.Diff.Entries[0].Recorded, Is.EqualTo("1.0"));
    }

    [Test]
    public void ResidualContentInsideCoincidingUnknownRegionsIsMasked()
    {
        CompletenessMap Truncated() => CompletenessMap.From(
            new[] { new CompletenessEntry(new FieldPath("nodes/save"), CompletenessReason.BudgetTruncated) },
            maxEntries: 8);
        var relaxed = Profile(requireCompleteForScope: false);

        // The residual under the truncated region differs — but a truncated
        // region's remainder is not comparison material.
        var left = State(
            nodes: new[]
            {
                Node("other"),
                Node("save", attributes: new[]
                {
                    new MaterializedAttribute("label", FieldValue.Of("partial-a"), false),
                }),
            },
            completeness: Truncated());
        var right = State(
            nodes: new[] { Node("other") },
            completeness: Truncated());

        Assert.That(
            Comparator().CompareState(left, right, relaxed).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Equal),
            "content at or under a coinciding unknown region is masked out of the walk");
    }

    [Test]
    public void CoincidingRootTruncationComparesEqual()
    {
        CompletenessMap Root() => CompletenessMap.From(
            System.Array.Empty<CompletenessEntry>(), maxEntries: 8, rootTruncated: true);
        var relaxed = Profile(requireCompleteForScope: false);
        var left = State(nodes: new[] { Node("alpha") }, completeness: Root());
        var right = State(nodes: new[] { Node("beta") }, completeness: Root());
        Assert.That(
            Comparator().CompareState(left, right, relaxed).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Equal),
            "the root marker covers every path");
    }

    [Test]
    public void AStaleSourceInsideACoincidingUnknownRegionStaysIncomparable()
    {
        // The projector records an omission region for a stale source; masking
        // it away would let two stale documents compare Equal.
        CompletenessMap WithStaleRegion() => CompletenessMap.From(
            new[] { new CompletenessEntry(new FieldPath("sources/inventory"), CompletenessReason.Stale) },
            maxEntries: 8);
        var relaxed = Profile(requireCompleteForScope: false);
        var stale = State(
            sources: new[] { Source("inventory", omission: CompletenessReason.Stale) },
            completeness: WithStaleRegion());

        var result = Comparator().CompareState(stale, stale, relaxed);
        Assert.That(
            result.Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(new IncomparableReason("Stale"))));
    }

    [Test]
    public void AMaximumLengthAuthorKeyStillProducesAReportableDivergence()
    {
        var longKey = new string('k', 1024);
        var left = State(nodes: new[] { Node(longKey) });
        var result = Comparator().CompareState(left, State(), Profile());
        Assert.That(result.Outcome, Is.EqualTo(ReplayComparisonOutcome.Diverged));
        Assert.That(result.Diff!.Entries[0].DetailCode, Is.EqualTo("NodeMissing"));
    }

    [Test]
    public void CapabilityIdsAroundTheCanonicalSeparatorMergeCorrectly()
    {
        // Canonical id@version order puts "a0@1.0" before "a@1.0" ('0' < '@'):
        // a raw-id walk over these lists would emit contradictory entries.
        var a = new CapabilityContractRef(new CapabilityContractId("a"), new ContractVersion(1, 0));
        var a0 = new CapabilityContractRef(new CapabilityContractId("a0"), new ContractVersion(1, 0));
        var left = State(nodes: new[]
        {
            Node("save", capabilities: new[]
            {
                new MaterializedCapability(a, true),
                new MaterializedCapability(a0, true),
            }),
        });
        var right = State(nodes: new[]
        {
            Node("save", capabilities: new[] { new MaterializedCapability(a, true) }),
        });

        var result = Comparator().CompareState(left, right, Profile());
        Assert.That(result.Diff!.Entries.Count, Is.EqualTo(1), "exactly the one absent capability");
        Assert.That(result.Diff.Entries[0].DetailCode, Is.EqualTo("CapabilityMissing"));
        Assert.That(result.Diff.Entries[0].Path, Is.EqualTo("nodes/save/capabilities/a0"));
    }

    [Test]
    public void ADomainMismatchBetweenTheSidesIsIncomparable()
    {
        var otherDomain = new ObservationMaterialization(
            new ObservationBasis(
                new RuntimeIncarnationId("incarnation-1"),
                new SourceRevision(5),
                RecordView,
                new SecurityDomainId("agent-domain"),
                "root"),
            ValueArray<MaterializedNode>.Empty,
            ValueArray<MaterializedSource>.Empty,
            CompletenessMap.Complete);
        Assert.That(
            Comparator().CompareState(State(), otherDomain, Profile()).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(new IncomparableReason("ViewMismatch"))),
            "different exposures are not the same observation surface");
    }

    [Test]
    public void ABasisOutsideTheProfileIsIncomparable()
    {
        var otherView = new ObservationMaterialization(
            new ObservationBasis(
                new RuntimeIncarnationId("incarnation-1"),
                new SourceRevision(5),
                new ViewContractRef(new ViewContractId("other"), new ContractVersion(1, 0)),
                RecordDomain,
                "root"),
            ValueArray<MaterializedNode>.Empty,
            ValueArray<MaterializedSource>.Empty,
            CompletenessMap.Complete);
        Assert.That(
            Comparator().CompareState(otherView, State(), Profile()).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Incomparable(new IncomparableReason("ViewMismatch"))));
    }

    [Test]
    public void TemporalLegsAreNotCompared()
    {
        // Same content at a different incarnation and revision compares Equal:
        // the temporal legs are provenance, never comparison material (ADR 0012).
        var later = new ObservationMaterialization(
            new ObservationBasis(
                new RuntimeIncarnationId("incarnation-2"),
                new SourceRevision(99),
                RecordView,
                RecordDomain,
                "root"),
            Representative().Nodes,
            Representative().Sources,
            CompletenessMap.Complete);
        Assert.That(
            Comparator().CompareState(Representative(), later, Profile()).Outcome,
            Is.EqualTo(ReplayComparisonOutcome.Equal));
    }

    [Test]
    public void DiffEntriesFollowComparisonOrder()
    {
        var left = State(
            nodes: new[]
            {
                Node("alpha", attributes: new[]
                {
                    new MaterializedAttribute("label", FieldValue.Of("a"), false),
                }),
                Node("beta"),
            },
            sources: new[]
            {
                Source("inventory", fields: new[] { new NamedField("count", FieldValue.Of(1L)) }),
            });
        var right = State(
            nodes: new[]
            {
                Node("alpha", attributes: new[]
                {
                    new MaterializedAttribute("label", FieldValue.Of("b"), false),
                }),
            },
            sources: new[]
            {
                Source("inventory", fields: new[] { new NamedField("count", FieldValue.Of(2L)) }),
            });
        var result = Comparator().CompareState(left, right, Profile());
        var paths = new List<string>();
        foreach (var entry in result.Diff!.Entries)
        {
            paths.Add(entry.Path);
        }

        Assert.That(paths, Is.EqualTo(new[]
        {
            "nodes/alpha/attributes/label",
            "nodes/beta",
            "sources/inventory/count",
        }));
    }
}
