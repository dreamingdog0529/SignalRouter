using System.Linq;
using NUnit.Framework;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel.Tests;

/// <summary>
/// kernel-execution.md §6 and observation-state.md §4 — truncation surfaces as
/// `BudgetTruncated` completeness, never silent omission; a request deferred under
/// mid-pump budget pressure restarts against the next pump's fresh budget before
/// newly adopted control work; internal evaluation reads are bounded and answer
/// `Unevaluable(Incompleteness)` on overflow.
/// </summary>
public sealed class ObservationBudgetTests
{
    private static readonly ViewContractRef AgentView =
        new(new ViewContractId("agent-standard"), new ContractVersion(1, 0));

    private static readonly ExposurePolicy VisibleToAgent =
        new(ValueArray<SecurityDomainId>.From(new[] { KernelFixture.AgentDomain }));

    private static void RegisterButtons(KernelFixture fixture, int count)
    {
        for (var i = 0; i < count; i++)
        {
            fixture.Runtime.Bootstrap.RegisterNode(new NodeRegistration(
                new AuthorKey("extra-" + i), NodeRole.Button, parent: null,
                ValueArray<NodeAttribute>.Empty, ValueArray<CapabilityDeclaration>.Empty,
                VisibleToAgent));
        }
    }

    [Test]
    public void AnOverBudgetSnapshotIsTruncatedHonestlyNeverSilently()
    {
        var fixture = new KernelFixture(observationBudgetNodes: 3, start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            AgentView, ViewFamily.Agent, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        RegisterButtons(fixture, 6);
        fixture.Runtime.Start(fixture.Executor);

        var observer = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(AgentView, KernelFixture.Agent, "root", observer);
        fixture.PumpUntilIdle();

        var snapshot = observer.Pinned.Single().Snapshot;
        Assert.That(snapshot.Materialization.Nodes.Count, Is.EqualTo(3), "deterministic ordinal cut");
        Assert.That(snapshot.Snapshot.Completeness.RootTruncated, Is.True);
        var missingKey = snapshot.Materialization.Nodes.Any(node => node.Key.Value == "save")
            ? "unreachable"
            : "save";
        Assert.That(
            snapshot.Lookup.Lookup(new FieldPath($"nodes/{missingKey}/attributes/label")),
            Is.EqualTo(FieldLookup.Incomplete(CompletenessReason.BudgetTruncated)),
            "an unmaterialized region answers its reason, never a fabricated absence");
    }

    [Test]
    public void MidPumpBudgetPressureDefersAndRestartsAgainstAFreshBudget()
    {
        // Budget of 4 nodes per pump; the world has 4 agent-visible nodes
        // ('save' + 3 extras). The first snapshot consumes the whole budget, so the
        // second defers and is served complete at the next pump — never truncated
        // by leftovers (the ADR 0011 restart policy).
        var fixture = new KernelFixture(observationBudgetNodes: 4, start: false);
        fixture.Runtime.Bootstrap.RegisterViewContract(new ViewContractDescriptor(
            AgentView, ViewFamily.Agent, "root",
            maxNodes: 256, maxFieldBytes: 4096, includeKeylessNodes: false));
        RegisterButtons(fixture, 3);
        fixture.Runtime.Start(fixture.Executor);
        fixture.PublishInventory(1);
        fixture.PumpUntilIdle();

        var first = new RecordingSnapshotObserver();
        var second = new RecordingSnapshotObserver();
        fixture.Runtime.Control.RequestSnapshot(AgentView, KernelFixture.Agent, "root", first);
        fixture.Runtime.Control.RequestSnapshot(AgentView, KernelFixture.Agent, "root", second);
        var report = fixture.Pump();

        Assert.That(first.Pinned, Has.Count.EqualTo(1));
        Assert.That(first.Pinned.Single().Snapshot.Snapshot.Completeness.IsComplete, Is.True);
        Assert.That(second.Pinned, Is.Empty, "deferred under mid-pump budget pressure");
        Assert.That(second.Refused, Is.Empty);
        Assert.That(report.WorkRemaining, Is.True, "the report reflects the deferred queue");

        fixture.Pump();
        Assert.That(second.Pinned, Has.Count.EqualTo(1), "served at the next pump's fresh budget");
        Assert.That(
            second.Pinned.Single().Snapshot.Snapshot.Completeness.IsComplete, Is.True,
            "the restart delivered the complete snapshot the leftover budget could not");
    }

    private sealed class ScriptedSampledReader : ISampledSourceReader
    {
        internal SampledDocument? Reading { get; set; }

        public SampledDocument? Read() => Reading;
    }

    private static KernelFixture BuildWithSampledSource(
        ScriptedSampledReader reader, int maxObservationFieldBytes = 4096)
    {
        var fixture = new KernelFixture(
            maxObservationFieldBytes: maxObservationFieldBytes, start: false);
        fixture.Runtime.Bootstrap.RegisterStateSource(new StateSourceRegistration(
            new StateSourceKey("sampled"),
            new StateSourceContractDescriptor(
                new StateSourceContractRef(
                    new StateSourceContractId("sampled"), new ContractVersion(1, 0)),
                ValueArray<SourceFieldSchema>.From(new[]
                {
                    new SourceFieldSchema("phase", FieldType.String, Sensitivity.Standard),
                }),
                agentVisible: true,
                recordVisible: true,
                maxDocumentBytes: 256),
            StateSourceClass.Sampled,
            reader,
            freshnessBoundLogicalTime: 50));
        var probe = new PredicateContractRef(
            new PredicateContractId("phaseProbe"), new ContractVersion(1, 0));
        fixture.Runtime.Bootstrap.RegisterPredicateContract(probe, new PredicateDefinition(
            ValueArray<PredicateClause>.From(new[]
            {
                new PredicateClause(new ClauseId("c0"), new ComparisonExpression(
                    new FieldPath("sources/sampled/phase"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of("loading"))),
            })));
        fixture.Runtime.Start(fixture.Executor);
        return fixture;
    }

    private static PredicateEvaluationOutcome ProbePhase(KernelFixture fixture)
    {
        var observer = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(new[]
            {
                new PredicateContractRef(
                    new PredicateContractId("phaseProbe"), new ContractVersion(1, 0)),
            }),
            KernelFixture.Agent,
            observer));
        fixture.PumpUntilIdle();
        return observer.Results!.Value.Single().Outcome;
    }

    [Test]
    public void ANonConformingSampledReadingIsNeverPartiallyExposed()
    {
        // codex review: sampled readings get the same contract validation an
        // adoption gets — an undeclared or mistyped field means no usable document.
        var reader = new ScriptedSampledReader
        {
            Reading = new SampledDocument(
                new SourceDocument(ValueArray<NamedField>.From(new[]
                {
                    new NamedField("phase", FieldValue.Of("loading")),
                    new NamedField("undeclared", FieldValue.Of(1L)),
                })),
                producedAtLogicalTime: 100),
        };
        var fixture = BuildWithSampledSource(reader);

        Assert.That(
            ProbePhase(fixture),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.SourceUnavailable)));

        reader.Reading = new SampledDocument(
            new SourceDocument(ValueArray<NamedField>.From(new[]
            {
                new NamedField("phase", FieldValue.Of(42L)),
            })),
            producedAtLogicalTime: 100);
        Assert.That(
            ProbePhase(fixture),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.SourceUnavailable)),
            "a mistyped field is a contract violation, not a value");

        reader.Reading = new SampledDocument(
            new SourceDocument(ValueArray<NamedField>.From(new[]
            {
                new NamedField("phase", FieldValue.Of("loading")),
            })),
            producedAtLogicalTime: 100);
        Assert.That(ProbePhase(fixture), Is.EqualTo(PredicateEvaluationOutcome.Satisfied));
    }

    [Test]
    public void AnOversizedSourceFieldFollowsTheSamePerFieldCeiling()
    {
        // codex review: the per-field ceiling applies to source values exactly as
        // to node attributes — omitted and marked, never retained oversized.
        var reader = new ScriptedSampledReader
        {
            Reading = new SampledDocument(
                new SourceDocument(ValueArray<NamedField>.From(new[]
                {
                    new NamedField("phase", FieldValue.Of("this-phase-name-exceeds-the-ceiling")),
                })),
                producedAtLogicalTime: 100),
        };
        var fixture = BuildWithSampledSource(reader, maxObservationFieldBytes: 8);

        Assert.That(
            ProbePhase(fixture),
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Incompleteness)));
    }

    [Test]
    public void AnOversizedFieldSurfacesAsCompletenessAndEvaluatesUnevaluable()
    {
        var fixture = new KernelFixture(maxObservationFieldBytes: 8, start: false);
        var longLabel = new PredicateContractRef(
            new PredicateContractId("longLabel"), new ContractVersion(1, 0));
        fixture.Runtime.Bootstrap.RegisterPredicateContract(longLabel, new PredicateDefinition(
            ValueArray<PredicateClause>.From(new[]
            {
                new PredicateClause(new ClauseId("c0"), new ComparisonExpression(
                    new FieldPath("nodes/save/attributes/label"),
                    ComparisonOperator.Eq,
                    PredicateOperand.Of("this-value-is-longer-than-eight-units"))),
            })));
        fixture.Runtime.Start(fixture.Executor);
        fixture.Runtime.Registry.UpdateAttributes(
            fixture.SaveNode,
            ValueArray<NodeAttribute>.From(new[]
            {
                new NodeAttribute(
                    "label", FieldValue.Of("this-value-is-longer-than-eight-units"), Sensitivity.Standard),
            }),
            observer: null);
        fixture.PumpUntilIdle();

        var observer = new RecordingAssertionObserver();
        fixture.Runtime.Control.EvaluateAssertions(new AssertionBatch(
            ValueArray<PredicateContractRef>.From(new[] { longLabel }),
            KernelFixture.Agent,
            observer));
        fixture.PumpUntilIdle();

        Assert.That(
            observer.Results!.Value.Single().Outcome,
            Is.EqualTo(PredicateEvaluationOutcome.Unevaluable(UnevaluableReason.Incompleteness)),
            "an over-ceiling value is BudgetTruncated completeness, never a silent False");
    }
}
