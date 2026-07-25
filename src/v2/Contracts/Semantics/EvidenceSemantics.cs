using System;
using System.Collections.Generic;
using System.Linq;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The pure reader semantics of guarantees.md: per-interaction shape
    /// classification (§6.1), structural rule checks (§6.2), recomputable closure
    /// verification (§5.9), static replay hazards (§5.5–§5.7), and the artifact
    /// decision table (§6.3). Everything here recomputes from durable evidence —
    /// cross-cut spec violations are results, never exceptions, because a reader must
    /// classify malformed artifacts honestly. Storage-level facts (durability,
    /// blob/digest integrity) arrive through <see cref="ArtifactFacts"/>.
    /// </summary>
    public static class EvidenceSemantics
    {
        /// <summary>
        /// R4 (guarantees.md §6.2): strict replay compares evidence from all cuts.
        /// This declaration is the comparison surface later layers must cover; a
        /// structural property of the cut set, pinned by test.
        /// </summary>
        public static ValueList<EvidenceCutKind> ComparisonBearingCutKinds { get; } =
            ValueList<EvidenceCutKind>.From(new[]
            {
                EvidenceCutKind.RecordingOpened,
                EvidenceCutKind.AdmissionCut,
                EvidenceCutKind.EffectPermit,
                EvidenceCutKind.TerminalCut,
                EvidenceCutKind.ExternalMutationBarrier,
                EvidenceCutKind.PredicateArmed,
                EvidenceCutKind.PredicateResolved,
                EvidenceCutKind.RecordingClosed,
                EvidenceCutKind.AssertionEvaluated,
            });

        /// <summary>
        /// Classifies every admitted interaction (one per E2, in stream order) into
        /// its §6.1 shape and reader outcome.
        /// </summary>
        public static ValueList<InteractionClassification> ClassifyInteractions(ArtifactFacts facts)
        {
            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            var contaminated = CollectContaminatedRequests(facts);
            var results = new List<InteractionClassification>();
            foreach (var chain in CollectChains(facts))
            {
                results.Add(ClassifyChain(chain, contaminated.Contains(chain.RequestId)));
            }

            return ValueList<InteractionClassification>.From(results);
        }

        /// <summary>
        /// Checks the structural rules R1 and R3 (guarantees.md §6.2). R2 and R4 are
        /// properties of the type system (no control-lane cut kind exists; every kind
        /// is comparison-bearing) and are pinned by tests over declarations. R5 is
        /// honored by construction: no rule below ever inspects E8 cuts.
        /// </summary>
        public static ValueList<RuleViolation> CheckStructure(ArtifactFacts facts)
        {
            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            var violations = new List<RuleViolation>();
            CheckInteractionStructure(facts, violations);
            CheckPredicatePairing(facts, violations);
            CheckContinuations(facts, violations);
            return ValueList<RuleViolation>.From(violations);
        }

        /// <summary>
        /// Recomputes the closure material an E7 declares (guarantees.md §5.9): the
        /// ReplayEvidence cut count (E1 and E7 included) and the reachable-ContentId
        /// set (every ContentId referenced by any cut). Blob existence and digest
        /// verification are codec facts outside this module.
        /// </summary>
        public static ClosureCheckResult VerifyRecomputableClosure(ArtifactFacts facts)
        {
            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            var close = facts.Cuts.OfType<RecordingClosed>().LastOrDefault();
            if (close == null)
            {
                return ClosureCheckResult.MissingClose;
            }

            if (close.DeclaredEventCount != facts.Cuts.Count)
            {
                return ClosureCheckResult.EventCountMismatch;
            }

            var declared = new HashSet<ContentId>(close.DeclaredReachableContentIds);
            foreach (var referenced in CollectReferencedContentIds(facts))
            {
                if (!declared.Contains(referenced))
                {
                    return ClosureCheckResult.UnreachableContentId;
                }
            }

            return ClosureCheckResult.Verified;
        }

        /// <summary>
        /// Derives the strict-replay stop candidates a pre-scan can compute from the
        /// evidence alone, ordered by stream position (guarantees.md §5.5–§5.7,
        /// §6.1). Stops requiring contract knowledge (temporal predicates) and the
        /// actual stop decision belong to the replay layer.
        /// </summary>
        public static ValueList<StaticReplayHazard> ScanStaticReplayHazards(ArtifactFacts facts)
        {
            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            var hazards = new List<StaticReplayHazard>();
            var contaminated = CollectContaminatedRequests(facts);

            foreach (var chain in CollectChains(facts))
            {
                if (chain.Permit != null && chain.Terminal == null)
                {
                    hazards.Add(new StaticReplayHazard(
                        StaticReplayHazardKind.OutcomeUnknownShape,
                        chain.Permit.Sequence,
                        chain.RequestId));
                }

                if (chain.Permit != null && contaminated.Contains(chain.RequestId))
                {
                    hazards.Add(new StaticReplayHazard(
                        StaticReplayHazardKind.ContaminatedEffect,
                        chain.Permit.Sequence,
                        chain.RequestId,
                        reason: IncomparableReason.Contamination));
                }

                if (chain.Terminal?.Cancellation != null)
                {
                    var entryPosition = chain.Permit?.Sequence ?? chain.Terminal.Sequence;
                    if (chain.Terminal.Cancellation.Phase == CancellationPhase.DuringEffect)
                    {
                        hazards.Add(new StaticReplayHazard(
                            StaticReplayHazardKind.DuringEffectCancellation,
                            entryPosition,
                            chain.RequestId,
                            reason: IncomparableReason.CancellationTiming));
                    }
                    else if (chain.Terminal.Cancellation.Phase == CancellationPhase.AfterEffect &&
                        chain.Terminal.Outcome == InteractionOutcome.Cancelled)
                    {
                        hazards.Add(new StaticReplayHazard(
                            StaticReplayHazardKind.CancelledAfterEffectTerminal,
                            entryPosition,
                            chain.RequestId,
                            reason: IncomparableReason.CancellationTiming));
                    }
                }
            }

            var armedByOperation = facts.Cuts.OfType<PredicateArmed>()
                .GroupBy(cut => cut.OperationId)
                .ToDictionary(group => group.Key, group => group.First());
            foreach (var resolved in facts.Cuts.OfType<PredicateResolved>())
            {
                if (resolved.Outcome == PredicateResolution.Satisfied)
                {
                    continue;
                }

                var position = armedByOperation.TryGetValue(resolved.OperationId, out var armed)
                    ? armed.Sequence
                    : resolved.Sequence;
                hazards.Add(new StaticReplayHazard(
                    StaticReplayHazardKind.PredicateResolutionNotSatisfied,
                    position,
                    operation: resolved.OperationId,
                    reason: resolved.Outcome == PredicateResolution.Faulted
                        ? IncomparableReason.PredicateFault
                        : (IncomparableReason?)null));
            }

            hazards.Sort((left, right) => left.Position.CompareTo(right.Position));
            return ValueList<StaticReplayHazard>.From(hazards);
        }

        /// <summary>
        /// The §6.3 decision table with its classification precedence: reader
        /// verification overrides the writer's self-declaration. An E7 declaring
        /// Completed over evidence violating R1/R3 reads Interrupted; an E7 whose
        /// closure fails verification reads Interrupted; Incomplete(reason) is
        /// honored only when E7 is durable and its closure verifies.
        /// </summary>
        public static ArtifactClassification ClassifyArtifact(ArtifactFacts facts)
        {
            if (facts == null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            var interactions = ClassifyInteractions(facts);
            var violations = CheckStructure(facts);
            var closure = VerifyRecomputableClosure(facts);
            var hazards = ScanStaticReplayHazards(facts);

            var outcome = DecideOutcome(facts, violations, closure);
            return new ArtifactClassification(outcome, interactions, violations, closure, hazards);
        }

        private static RecordingOutcome DecideOutcome(
            ArtifactFacts facts,
            ValueList<RuleViolation> violations,
            ClosureCheckResult closure)
        {
            var opens = facts.Cuts.OfType<RecordingOpened>().ToList();
            if (!facts.BaseSnapshotDurable || opens.Count == 0)
            {
                return RecordingOutcome.OpenFailed;
            }

            var closes = facts.Cuts.OfType<RecordingClosed>().ToList();
            if (closes.Count == 0)
            {
                return RecordingOutcome.Interrupted;
            }

            if (!IsStreamSound(facts, opens.Count, closes.Count))
            {
                return RecordingOutcome.Interrupted;
            }

            if (closure != ClosureCheckResult.Verified || facts.ExternalIntegrityFailure)
            {
                return RecordingOutcome.Interrupted;
            }

            var close = closes[0];
            if (!close.Reason.IsCompleted)
            {
                return RecordingOutcome.Incomplete(close.Reason.Reason);
            }

            return violations.Count > 0
                ? RecordingOutcome.Interrupted
                : RecordingOutcome.Completed;
        }

        private static bool IsStreamSound(ArtifactFacts facts, int openCount, int closeCount)
        {
            if (openCount != 1 || closeCount > 1)
            {
                return false;
            }

            if (facts.Cuts.Count == 0 || facts.Cuts[0].Kind != EvidenceCutKind.RecordingOpened)
            {
                return false;
            }

            if (closeCount == 1 && facts.Cuts[facts.Cuts.Count - 1].Kind != EvidenceCutKind.RecordingClosed)
            {
                return false;
            }

            for (var i = 1; i < facts.Cuts.Count; i++)
            {
                if (facts.Cuts[i].Sequence <= facts.Cuts[i - 1].Sequence)
                {
                    return false;
                }
            }

            return true;
        }

        private static InteractionClassification ClassifyChain(InteractionChain chain, bool contaminated)
        {
            InteractionShape shape;
            InteractionOutcome readerOutcome;
            var evidenceIncomplete = false;
            var stopsBeforeEffect = false;

            if (chain.Terminal != null)
            {
                shape = chain.Permit != null
                    ? InteractionShape.TerminalWithEffect
                    : InteractionShape.TerminalWithoutEffect;
                readerOutcome = chain.Terminal.Outcome;
                if (chain.Terminal.Cancellation != null &&
                    (chain.Terminal.Cancellation.Phase == CancellationPhase.DuringEffect ||
                        (chain.Terminal.Cancellation.Phase == CancellationPhase.AfterEffect &&
                            chain.Terminal.Outcome == InteractionOutcome.Cancelled)))
                {
                    stopsBeforeEffect = true;
                }
            }
            else if (chain.Permit != null)
            {
                shape = InteractionShape.PermittedWithoutTerminal;
                readerOutcome = InteractionOutcome.OutcomeUnknown;
                evidenceIncomplete = true;
                stopsBeforeEffect = true;
            }
            else
            {
                shape = InteractionShape.AdmittedOnly;
                readerOutcome = InteractionOutcome.OutcomeUnknown;
                evidenceIncomplete = true;
            }

            if (contaminated && chain.Permit != null)
            {
                stopsBeforeEffect = true;
            }

            return new InteractionClassification(
                chain.RequestId, shape, readerOutcome, evidenceIncomplete, contaminated, stopsBeforeEffect);
        }

        private static void CheckInteractionStructure(ArtifactFacts facts, List<RuleViolation> violations)
        {
            var admittedRequests = new HashSet<RequestId>(
                facts.Cuts.OfType<AdmissionCut>().Select(cut => cut.RequestId));

            foreach (var orphan in facts.Cuts.OfType<EffectPermit>()
                .Where(cut => !admittedRequests.Contains(cut.RequestId)))
            {
                violations.Add(new RuleViolation(
                    ShapeRule.R1, "E3 without a preceding E2 for its RequestId", orphan.RequestId));
            }

            foreach (var orphan in facts.Cuts.OfType<TerminalCut>()
                .Where(cut => !admittedRequests.Contains(cut.RequestId)))
            {
                violations.Add(new RuleViolation(
                    ShapeRule.R1, "E4 without a preceding E2 for its RequestId", orphan.RequestId));
            }

            foreach (var chain in CollectChains(facts))
            {
                if (chain.DuplicateCuts)
                {
                    violations.Add(new RuleViolation(
                        ShapeRule.R1, "Duplicate interaction cut for one RequestId", chain.RequestId));
                }

                if (chain.OutOfOrder)
                {
                    violations.Add(new RuleViolation(
                        ShapeRule.R1, "Interaction cuts out of order", chain.RequestId));
                }

                if (chain.Terminal != null && chain.Terminal.EffectPermitted != (chain.Permit != null))
                {
                    violations.Add(new RuleViolation(
                        ShapeRule.R1,
                        "Terminal permit flag disagrees with E3 presence",
                        chain.RequestId));
                }

                if (chain.Terminal == null)
                {
                    violations.Add(new RuleViolation(
                        ShapeRule.R1,
                        chain.Permit != null
                            ? "Permitted interaction without terminal"
                            : "Admitted interaction without terminal",
                        chain.RequestId));
                }
            }
        }

        private static void CheckPredicatePairing(ArtifactFacts facts, List<RuleViolation> violations)
        {
            var armedGroups = facts.Cuts.OfType<PredicateArmed>()
                .GroupBy(cut => cut.OperationId).ToList();
            var resolvedGroups = facts.Cuts.OfType<PredicateResolved>()
                .GroupBy(cut => cut.OperationId).ToList();
            var armedOperations = new HashSet<OperationId>(armedGroups.Select(group => group.Key));
            var resolvedOperations = new HashSet<OperationId>(resolvedGroups.Select(group => group.Key));

            foreach (var group in armedGroups.Where(group => group.Count() > 1))
            {
                violations.Add(new RuleViolation(
                    ShapeRule.R1, "Duplicate PredicateArmed for one OperationId", operation: group.Key));
            }

            foreach (var group in resolvedGroups.Where(group => group.Count() > 1))
            {
                violations.Add(new RuleViolation(
                    ShapeRule.R1, "Duplicate PredicateResolved for one OperationId", operation: group.Key));
            }

            foreach (var operation in armedOperations.Where(operation => !resolvedOperations.Contains(operation)))
            {
                violations.Add(new RuleViolation(
                    ShapeRule.R1, "PredicateArmed without a matching PredicateResolved", operation: operation));
            }

            foreach (var operation in resolvedOperations.Where(operation => !armedOperations.Contains(operation)))
            {
                violations.Add(new RuleViolation(
                    ShapeRule.R1, "PredicateResolved without a matching PredicateArmed", operation: operation));
            }
        }

        private static void CheckContinuations(ArtifactFacts facts, List<RuleViolation> violations)
        {
            var chains = CollectChains(facts).ToDictionary(chain => chain.RequestId);
            var childrenByLink = new Dictionary<(RequestId Parent, int Ordinal), List<AdmissionCut>>();
            foreach (var admission in facts.Cuts.OfType<AdmissionCut>())
            {
                var link = admission.Envelope.Causality.Continuation;
                if (link == null)
                {
                    continue;
                }

                var key = (link.Value.ParentRequestId, link.Value.ContinuationOrdinal);
                if (!childrenByLink.TryGetValue(key, out var list))
                {
                    list = new List<AdmissionCut>();
                    childrenByLink[key] = list;
                }

                list.Add(admission);
            }

            var committedLinks = new HashSet<(RequestId Parent, int Ordinal)>();
            foreach (var parent in facts.Cuts.OfType<TerminalCut>())
            {
                foreach (var commitment in parent.Continuations)
                {
                    var key = (parent.RequestId, commitment.Ordinal);
                    committedLinks.Add(key);
                    if (!childrenByLink.TryGetValue(key, out var children) || children.Count == 0)
                    {
                        violations.Add(new RuleViolation(
                            ShapeRule.R3, "Unresolved continuation commitment", parent.RequestId));
                        continue;
                    }

                    if (children.Count > 1)
                    {
                        violations.Add(new RuleViolation(
                            ShapeRule.R3, "Duplicate children for one continuation commitment", parent.RequestId));
                    }

                    var child = children[0];
                    if (!child.Envelope.Causality.Continuation!.Value.Fingerprint.Equals(commitment.Fingerprint))
                    {
                        violations.Add(new RuleViolation(
                            ShapeRule.R3, "Continuation fingerprint mismatch", child.RequestId));
                    }

                    if (!chains.TryGetValue(child.RequestId, out var childChain) || childChain.Terminal == null)
                    {
                        violations.Add(new RuleViolation(
                            ShapeRule.R3, "Continuation child without a terminal", child.RequestId));
                    }
                }
            }

            foreach (var entry in childrenByLink.Where(entry => !committedLinks.Contains(entry.Key)))
            {
                foreach (var child in entry.Value)
                {
                    violations.Add(new RuleViolation(
                        ShapeRule.R3, "Continuation child without a matching commitment", child.RequestId));
                }
            }
        }

        private static HashSet<RequestId> CollectContaminatedRequests(ArtifactFacts facts)
        {
            var contaminated = new HashSet<RequestId>();
            foreach (var barrier in facts.Cuts.OfType<ExternalMutationBarrier>())
            {
                foreach (var request in barrier.ContaminatedRequests)
                {
                    contaminated.Add(request);
                }
            }

            return contaminated;
        }

        private static IEnumerable<ContentId> CollectReferencedContentIds(ArtifactFacts facts)
        {
            foreach (var cut in facts.Cuts)
            {
                switch (cut)
                {
                    case RecordingOpened opened:
                        yield return opened.BaseSnapshot;
                        break;
                    case EffectPermit permit:
                        yield return permit.BeforeView;
                        break;
                    case TerminalCut terminal:
                        yield return terminal.AfterView;
                        break;
                    case PredicateResolved resolved:
                        yield return resolved.WitnessOrFinalObservation;
                        break;
                    case AssertionEvaluated assertion:
                        yield return assertion.Snapshot;
                        break;
                    case RecordingClosed closed:
                        yield return closed.FinalCheckpoint;
                        break;
                }
            }
        }

        private static IEnumerable<InteractionChain> CollectChains(ArtifactFacts facts)
        {
            var chains = new Dictionary<RequestId, InteractionChain>();
            var order = new List<InteractionChain>();
            foreach (var cut in facts.Cuts)
            {
                RequestId requestId;
                switch (cut)
                {
                    case AdmissionCut admission:
                        requestId = admission.RequestId;
                        break;
                    case EffectPermit permit:
                        requestId = permit.RequestId;
                        break;
                    case TerminalCut terminal:
                        requestId = terminal.RequestId;
                        break;
                    default:
                        continue;
                }

                if (!chains.TryGetValue(requestId, out var chain))
                {
                    chain = new InteractionChain(requestId);
                    chains[requestId] = chain;
                    order.Add(chain);
                }

                chain.Accept(cut);
            }

            return order;
        }

        /// <summary>The E2/E3/E4 cuts of one interaction, in stream order.</summary>
        private sealed class InteractionChain
        {
            internal InteractionChain(RequestId requestId)
            {
                RequestId = requestId;
            }

            internal RequestId RequestId { get; }

            internal AdmissionCut? Admission { get; private set; }

            internal EffectPermit? Permit { get; private set; }

            internal TerminalCut? Terminal { get; private set; }

            internal bool DuplicateCuts { get; private set; }

            internal bool OutOfOrder { get; private set; }

            internal void Accept(EvidenceCut cut)
            {
                switch (cut)
                {
                    case AdmissionCut admission:
                        if (Admission != null)
                        {
                            DuplicateCuts = true;
                        }
                        else
                        {
                            Admission = admission;
                            if (Permit != null || Terminal != null)
                            {
                                OutOfOrder = true;
                            }
                        }

                        break;
                    case EffectPermit permit:
                        if (Permit != null)
                        {
                            DuplicateCuts = true;
                        }
                        else
                        {
                            Permit = permit;
                            if (Terminal != null)
                            {
                                OutOfOrder = true;
                            }
                        }

                        break;
                    case TerminalCut terminal:
                        if (Terminal != null)
                        {
                            DuplicateCuts = true;
                        }
                        else
                        {
                            Terminal = terminal;
                        }

                        break;
                }
            }
        }
    }
}
