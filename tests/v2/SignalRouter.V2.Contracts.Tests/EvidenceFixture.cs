using System;
using System.Collections.Generic;
using System.Linq;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// Builds evidence-cut sequences for the guarantees.md oracle tests. The fixture is
/// deliberately independent of <see cref="EvidenceSemantics"/>: expected results in
/// tests are literal transcriptions of the spec, never derived from the production
/// classifier, so later layers (kernel, recording, TCK) can reuse these fixtures as
/// black-box inputs.
/// </summary>
internal sealed class EvidenceFixture
{
    private readonly List<EvidenceCut> cuts = new();
    private readonly Dictionary<string, LogicalOrder> logicalOrders = new();
    private readonly Dictionary<string, bool> permits = new();
    private ulong nextSequence = 1;
    private ulong nextLogicalOrder = 1;

    internal EvidenceSequence LastSequence => new(nextSequence - 1);

    internal EvidenceFixture Open()
    {
        cuts.Add(new RecordingOpened(
            NextSequence(),
            TestData.ComparisonProfile,
            TestData.RecordView,
            new RedactionPolicyId("default-redaction"),
            ValueArray<CompletionBinding>.From(new[]
            {
                new CompletionBinding(TestData.Capability, TestData.CompletionProfile),
            }),
            ValueArray<StateSourceBinding>.Empty,
            ValueArray<PredicateContractRef>.From(new[] { TestData.Predicate }),
            TestData.Incarnation,
            TestData.Content("base")));
        return this;
    }

    internal EvidenceFixture Admit(string request, Causality? causality = null, ulong? order = null)
    {
        var logicalOrder = order.HasValue
            ? new LogicalOrder(order.Value)
            : new LogicalOrder(nextLogicalOrder++);
        logicalOrders[request] = logicalOrder;
        cuts.Add(new AdmissionCut(
            NextSequence(),
            TestData.Request(request),
            logicalOrder,
            TestData.Fingerprint(request),
            TestData.Invocation(request),
            TestData.Recorded(request),
            TestData.KeyedTarget($"key-{request}"),
            TestData.Envelope(causality)));
        return this;
    }

    internal EvidenceFixture Permit(string request)
    {
        permits[request] = true;
        cuts.Add(new EffectPermit(
            NextSequence(),
            TestData.Request(request),
            OrderOf(request),
            new SourceRevision(nextSequence),
            TestData.Content($"before-{request}"),
            reusedCheckpointBlob: false));
        return this;
    }

    internal EvidenceFixture Terminal(
        string request,
        InteractionOutcome outcome,
        RejectionReason? rejectionReason = null,
        FaultCode? faultCode = null,
        CancellationPhase? cancellationPhase = null,
        ValueArray<ContinuationCommitment>? continuations = null)
    {
        var effectPermitted = permits.ContainsKey(request);
        var cancellation = cancellationPhase.HasValue
            ? TestData.Cancellation(cancellationPhase.Value)
            : outcome == InteractionOutcome.Cancelled
                ? TestData.Cancellation(effectPermitted ? CancellationPhase.DuringEffect : CancellationPhase.BeforeEffect)
                : null;
        cuts.Add(new TerminalCut(
            NextSequence(),
            TestData.Request(request),
            OrderOf(request),
            outcome,
            effectPermitted,
            TestData.Content($"after-{request}"),
            rejectionReason ?? (outcome == InteractionOutcome.Rejected ? RejectionReason.RequestIdConflict : null),
            faultCode ?? (outcome == InteractionOutcome.Faulted && effectPermitted ? new FaultCode("AppFault") : null),
            outcome == InteractionOutcome.Succeeded ? TestData.Completion() : null,
            postcondition: null,
            cancellation,
            continuations));
        return this;
    }

    internal EvidenceFixture Barrier(ulong lastKnownCleanCut, ulong firstObservedCut, params string[] contaminatedRequests)
    {
        cuts.Add(new ExternalMutationBarrier(
            NextSequence(),
            new EvidenceSequence(lastKnownCleanCut),
            new EvidenceSequence(firstObservedCut),
            new SourceRevision(nextSequence),
            "external-source",
            ValueArray<RequestId>.From(contaminatedRequests.Select(TestData.Request))));
        return this;
    }

    internal EvidenceFixture Arm(string operation)
    {
        cuts.Add(new PredicateArmed(
            NextSequence(),
            TestData.Operation(operation),
            TestData.Predicate,
            TestData.Arguments(operation),
            TestData.Fingerprint(operation),
            TestData.RecordView,
            "root",
            Causality.Root(),
            new ViewSequence(1)));
        return this;
    }

    internal EvidenceFixture Resolve(string operation, PredicateResolution resolution)
    {
        cuts.Add(new PredicateResolved(
            NextSequence(),
            TestData.Operation(operation),
            resolution,
            TestData.Content($"resolved-{operation}"),
            new ViewSequence(2)));
        return this;
    }

    internal EvidenceFixture Assertion(PredicateEvaluationOutcome outcome, string seed = "assertion")
    {
        cuts.Add(new AssertionEvaluated(
            NextSequence(),
            TestData.Incarnation,
            new SourceRevision(nextSequence),
            TestData.RecordView,
            stateSourceTableVersion: 1,
            scope: "root",
            new SecurityDomainId("record"),
            TestData.Content($"snapshot-{seed}"),
            completeForScope: true,
            TestData.Predicate,
            TestData.Arguments(seed),
            ValueArray<ClauseEvaluation>.From(new[]
            {
                new ClauseEvaluation("clause-1", "true", outcome.Kind == PredicateEvaluationKind.Satisfied ? "true" : "false"),
            }),
            outcome,
            ValueArray<string>.Empty));
        return this;
    }

    internal EvidenceFixture Close(
        RecordingCloseReason? reason = null,
        long? declaredEventCountOverride = null,
        bool omitReachableContentId = false,
        bool extraDeclaredContentId = false)
    {
        var finalCheckpoint = TestData.Content("final");
        var reachable = new List<ContentId> { finalCheckpoint };
        foreach (var cut in cuts)
        {
            switch (cut)
            {
                case RecordingOpened opened:
                    reachable.Add(opened.BaseSnapshot);
                    break;
                case EffectPermit permit:
                    reachable.Add(permit.BeforeView);
                    break;
                case TerminalCut terminal:
                    reachable.Add(terminal.AfterView);
                    break;
                case PredicateResolved resolved:
                    reachable.Add(resolved.WitnessOrFinalObservation);
                    break;
                case AssertionEvaluated assertion:
                    reachable.Add(assertion.Snapshot);
                    break;
            }
        }

        if (omitReachableContentId)
        {
            reachable.RemoveAt(reachable.Count - 1);
        }

        if (extraDeclaredContentId)
        {
            reachable.Add(TestData.Content("surplus-never-referenced"));
        }

        cuts.Add(new RecordingClosed(
            NextSequence(),
            reason ?? RecordingCloseReason.Completed,
            declaredEventCountOverride ?? cuts.Count + 1,
            finalCheckpoint,
            ValueArray<ContentId>.From(reachable.Distinct())));
        return this;
    }

    /// <summary>Appends an arbitrary pre-built cut (for malformed-stream fixtures).</summary>
    internal EvidenceFixture Append(Func<EvidenceSequence, EvidenceCut> factory)
    {
        cuts.Add(factory(NextSequence()));
        return this;
    }

    internal LogicalOrder OrderOf(string request) =>
        logicalOrders.TryGetValue(request, out var order) ? order : new LogicalOrder(nextLogicalOrder++);

    internal EvidenceSequence SequenceOfPermit(string request) =>
        cuts.OfType<EffectPermit>().First(cut => cut.RequestId == TestData.Request(request)).Sequence;

    internal EvidenceSequence SequenceOfArmed(string operation) =>
        cuts.OfType<PredicateArmed>().First(cut => cut.OperationId == TestData.Operation(operation)).Sequence;

    internal ArtifactFacts Build(bool baseSnapshotDurable = true, bool externalIntegrityFailure = false) =>
        new(baseSnapshotDurable, ValueArray<EvidenceCut>.From(cuts), externalIntegrityFailure);

    private EvidenceSequence NextSequence() => new(nextSequence++);
}
