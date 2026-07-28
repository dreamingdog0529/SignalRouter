using System;
using System.Collections.Generic;
using SignalRouter.V2.Codec.Recording;
using SignalRouter.V2.Comparison;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Replay
{
    /// <summary>
    /// The trust boundary and stop-point planner (recording-replay.md §6–§7): a
    /// replay artifact is executable input, so before any execution the scan
    /// enforces provenance, resource limits, integrity, the contract allowlist,
    /// and secret resolvability — then reuses the reader-authoritative
    /// <see cref="EvidenceSemantics"/> classification and its static hazard
    /// scan to plan the earliest strict-replay stop. Pure and side-effect free:
    /// no execution, no environment, no secret materialization. Decode-time
    /// budgets (bytes, records, blobs, strings) are enforced here through
    /// <see cref="ArtifactReadLimits"/>; the semantic ceilings of
    /// security-resources.md §5 (nodes, depth) bind the twin runtime's own
    /// ResourceProfile at execution, not the artifact decode.
    /// </summary>
    public static class ReplayPreScan
    {
        public static PreScanResult Scan(
            byte[] artifact,
            ArtifactReadLimits limits,
            ReplayAllowlist allowlist,
            ComparisonVocabulary vocabulary,
            ISecretReferenceResolver? secretResolver,
            ReplayTrustOptions trust)
        {
            if (artifact == null)
            {
                throw new ArgumentNullException(nameof(artifact));
            }

            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            if (allowlist == null)
            {
                throw new ArgumentNullException(nameof(allowlist));
            }

            if (vocabulary == null)
            {
                throw new ArgumentNullException(nameof(vocabulary));
            }

            if (trust == null)
            {
                throw new ArgumentNullException(nameof(trust));
            }

            // Provenance gates even parsing: anything that is not exactly
            // Trusted — including an undefined enum value from deserialized
            // configuration — is refused unless explicitly opted in
            // (security-resources.md §6; fail closed).
            if (trust.Provenance != ArtifactProvenance.Trusted && !trust.AcceptUntrustedArtifacts)
            {
                return PreScanResult.Refused(new ReplayRefusal(ReplayRefusalCodes.UntrustedProvenance));
            }

            ArtifactReadResult reading;
            try
            {
                reading = ArtifactReader.Read(artifact, limits);
            }
            catch (RecordingFormatException exception)
            {
                // The bounded reader throws for over-budget input and malformed
                // framing the torn-tail rules cannot absorb; the stable code —
                // never exception internals — decides the refusal.
                return PreScanResult.Refused(new ReplayRefusal(
                    string.Equals(exception.Code, "OverBudget", StringComparison.Ordinal)
                        ? ReplayRefusalCodes.ResourceLimit
                        : ReplayRefusalCodes.ArtifactIntegrity));
            }

            if (reading.IntegrityFailure)
            {
                return PreScanResult.Refused(new ReplayRefusal(ReplayRefusalCodes.ArtifactIntegrity));
            }

            var classification = EvidenceSemantics.ClassifyArtifact(reading.Facts);
            if (classification.Outcome.Kind == RecordingOutcomeKind.OpenFailed)
            {
                // No artifact exists to replay against (guarantees.md §3.2).
                return PreScanResult.Refused(new ReplayRefusal(ReplayRefusalCodes.OpenFailed));
            }

            if (reading.Profile == null)
            {
                return PreScanResult.Refused(new ReplayRefusal(ReplayRefusalCodes.ArtifactIntegrity));
            }

            // The reader's own verdicts join the trust boundary (§7 "integrity"
            // covers closure and structure, not just bytes): a rule violation or
            // a failed closure recomputation makes the artifact non-input.
            // Honest incompleteness stays plannable — a missing close, and the
            // missing-tail shapes the failure matrix explicitly plans stops for
            // (an unterminated chain, an unresolved arm) are truncation facts,
            // not tampering.
            for (var index = 0; index < classification.Violations.Count; index++)
            {
                if (!IsHonestIncompleteness(classification.Violations[index]))
                {
                    return PreScanResult.Refused(new ReplayRefusal(ReplayRefusalCodes.ArtifactIntegrity));
                }
            }

            if (classification.Closure != ClosureCheckResult.Verified &&
                classification.Closure != ClosureCheckResult.MissingClose)
            {
                return PreScanResult.Refused(new ReplayRefusal(ReplayRefusalCodes.ArtifactIntegrity));
            }

            var resolution = ProfileResolver.Resolve(
                reading.Profile, allowlist.SupportedProfile, vocabulary);
            if (!resolution.IsResolved)
            {
                return PreScanResult.Incomparable(resolution.IncomparableReason!.Value);
            }

            var opened = FindOpened(reading.Cuts);
            if (opened == null)
            {
                return PreScanResult.Refused(new ReplayRefusal(ReplayRefusalCodes.ArtifactIntegrity));
            }

            var allowlistRefusal = CheckAllowlist(opened, reading.Cuts, allowlist);
            if (allowlistRefusal != null)
            {
                return PreScanResult.Refused(allowlistRefusal);
            }

            var entries = BuildEntries(reading.Cuts, out var structural);
            if (structural)
            {
                return PreScanResult.Refused(new ReplayRefusal(ReplayRefusalCodes.ArtifactIntegrity));
            }

            var stop = PlanStop(reading.Cuts, classification, entries, secretResolver);
            return PreScanResult.Planned(new ReplayPlan(
                opened, resolution.Effective!, classification, entries, stop, reading));
        }

        /// <summary>
        /// The reader-rule violations that are truncation shapes rather than
        /// malformation: the failure matrix plans stops for them (guarantees.md
        /// §7) instead of rejecting the artifact. The descriptions are the
        /// frozen public vocabulary of <see cref="EvidenceSemantics"/> —
        /// matched exactly, never by substring.
        /// </summary>
        private static bool IsHonestIncompleteness(RuleViolation violation) =>
            string.Equals(
                violation.Description, "Permitted interaction without terminal",
                StringComparison.Ordinal) ||
            string.Equals(
                violation.Description, "Admitted interaction without terminal",
                StringComparison.Ordinal) ||
            string.Equals(
                violation.Description, "PredicateArmed without a matching PredicateResolved",
                StringComparison.Ordinal);

        private static RecordingOpened? FindOpened(ValueArray<EvidenceCut> cuts)
        {
            for (var index = 0; index < cuts.Count; index++)
            {
                if (cuts[index] is RecordingOpened opened)
                {
                    return opened;
                }
            }

            return null;
        }

        // ── The contract allowlist (recording-replay.md §7) ──────────────────

        private static ReplayRefusal? CheckAllowlist(
            RecordingOpened opened, ValueArray<EvidenceCut> cuts, ReplayAllowlist allowlist)
        {
            // The E1-pinned tables must be reproducible in the target runtime:
            // twin bootstrap equivalence is the precondition for every later
            // materialization comparison, so the whole catalog is checked, not
            // just the referenced subset.
            for (var index = 0; index < opened.CompletionBindings.Count; index++)
            {
                if (!ContainsBinding(allowlist.Capabilities, opened.CompletionBindings[index]))
                {
                    return new ReplayRefusal(ReplayRefusalCodes.ContractAllowlist);
                }
            }

            for (var index = 0; index < opened.StateSourceContracts.Count; index++)
            {
                if (!ContainsSource(allowlist.StateSources, opened.StateSourceContracts[index]))
                {
                    return new ReplayRefusal(ReplayRefusalCodes.ContractAllowlist);
                }
            }

            for (var index = 0; index < opened.PredicateContracts.Count; index++)
            {
                if (FindPredicate(allowlist, opened.PredicateContracts[index]) == null)
                {
                    return new ReplayRefusal(ReplayRefusalCodes.ContractAllowlist);
                }
            }

            // Registered definitions are pinned by E1 through their digest
            // (ADR 0015): every armed wait and assertion must re-derive its
            // recorded operand digest from the allowlisted definition, or the
            // target runtime's contract is not the recorded one.
            for (var index = 0; index < cuts.Count; index++)
            {
                switch (cuts[index])
                {
                    case PredicateArmed armed:
                    {
                        var entry = FindPredicate(allowlist, armed.Predicate);
                        if (entry == null)
                        {
                            return new ReplayRefusal(ReplayRefusalCodes.ContractAllowlist);
                        }

                        if (!PredicateCanonicalizer.DigestOf(entry.Definition).Equals(armed.Operands))
                        {
                            return new ReplayRefusal(ReplayRefusalCodes.PredicateDigestMismatch);
                        }

                        break;
                    }

                    case AssertionEvaluated assertion:
                    {
                        var entry = FindPredicate(allowlist, assertion.Predicate);
                        if (entry == null)
                        {
                            return new ReplayRefusal(ReplayRefusalCodes.ContractAllowlist);
                        }

                        if (!PredicateCanonicalizer.DigestOf(entry.Definition).Equals(assertion.Operands))
                        {
                            return new ReplayRefusal(ReplayRefusalCodes.PredicateDigestMismatch);
                        }

                        break;
                    }

                    case AdmissionCut admission:
                        if (!ContainsCapability(allowlist.Capabilities, admission.Invocation.Contract))
                        {
                            return new ReplayRefusal(ReplayRefusalCodes.ContractAllowlist);
                        }

                        break;
                }
            }

            return null;
        }

        private static bool ContainsBinding(
            ValueArray<CompletionBinding> bindings, CompletionBinding candidate)
        {
            for (var index = 0; index < bindings.Count; index++)
            {
                if (bindings[index].Equals(candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsCapability(
            ValueArray<CompletionBinding> bindings, CapabilityContractRef capability)
        {
            for (var index = 0; index < bindings.Count; index++)
            {
                if (bindings[index].Capability.Equals(capability))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsSource(
            ValueArray<StateSourceBinding> sources, StateSourceBinding candidate)
        {
            for (var index = 0; index < sources.Count; index++)
            {
                if (sources[index].Equals(candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static PredicateAllowlistEntry? FindPredicate(
            ReplayAllowlist allowlist, PredicateContractRef reference)
        {
            for (var index = 0; index < allowlist.Predicates.Count; index++)
            {
                if (allowlist.Predicates[index].Reference.Equals(reference))
                {
                    return allowlist.Predicates[index];
                }
            }

            return null;
        }

        // ── Entries (guarantees.md §6.1 shapes) ──────────────────────────────

        private static ValueArray<ReplayEntry> BuildEntries(
            ValueArray<EvidenceCut> cuts, out bool structuralViolation)
        {
            structuralViolation = false;
            var order = new List<RequestId>();
            var admissions = new Dictionary<RequestId, AdmissionCut>();
            var permits = new Dictionary<RequestId, EffectPermit>();
            var terminals = new Dictionary<RequestId, TerminalCut>();
            for (var index = 0; index < cuts.Count; index++)
            {
                switch (cuts[index])
                {
                    case AdmissionCut admission:
                        if (admissions.ContainsKey(admission.RequestId))
                        {
                            structuralViolation = true;
                            return ValueArray<ReplayEntry>.Empty;
                        }

                        order.Add(admission.RequestId);
                        admissions.Add(admission.RequestId, admission);
                        break;
                    case EffectPermit permit:
                        // A permit after its terminal is an out-of-order chain
                        // (R1: one interaction's E2 → E3 → E4 in that order).
                        if (!admissions.ContainsKey(permit.RequestId) ||
                            permits.ContainsKey(permit.RequestId) ||
                            terminals.ContainsKey(permit.RequestId))
                        {
                            structuralViolation = true;
                            return ValueArray<ReplayEntry>.Empty;
                        }

                        permits.Add(permit.RequestId, permit);
                        break;
                    case TerminalCut terminal:
                        if (!admissions.ContainsKey(terminal.RequestId) ||
                            terminals.ContainsKey(terminal.RequestId))
                        {
                            structuralViolation = true;
                            return ValueArray<ReplayEntry>.Empty;
                        }

                        terminals.Add(terminal.RequestId, terminal);
                        break;
                }
            }

            var entries = new ReplayEntry[order.Count];
            for (var index = 0; index < order.Count; index++)
            {
                var request = order[index];
                var admission = admissions[request];
                permits.TryGetValue(request, out var permit);
                terminals.TryGetValue(request, out var terminal);

                // A BeforeEffect cancellation with a permit contradicts itself
                // (the permit IS the effect boundary): treating it as
                // PreCancelled would route a permitted effect through the
                // synthetic no-effect path. The permitted flag must also agree
                // with the permit's existence (R1).
                if (terminal != null &&
                    (terminal.EffectPermitted != (permit != null) ||
                        (permit != null && terminal.Cancellation != null &&
                            terminal.Cancellation.Phase == CancellationPhase.BeforeEffect)))
                {
                    structuralViolation = true;
                    return ValueArray<ReplayEntry>.Empty;
                }

                var kind = ClassifyEntry(permit, terminal, out var shapeViolation);
                if (shapeViolation)
                {
                    structuralViolation = true;
                    return ValueArray<ReplayEntry>.Empty;
                }

                entries[index] = new ReplayEntry(request, kind, admission, permit, terminal);
            }

            return ValueArray<ReplayEntry>.From(entries);
        }

        private static ReplayEntryKind ClassifyEntry(
            EffectPermit? permit, TerminalCut? terminal, out bool shapeViolation)
        {
            shapeViolation = false;
            if (terminal == null)
            {
                return permit == null ? ReplayEntryKind.AdmittedOnly : ReplayEntryKind.OutcomeUnknown;
            }

            if (permit != null)
            {
                return ReplayEntryKind.Completed;
            }

            if (terminal.Cancellation != null &&
                terminal.Cancellation.Phase == CancellationPhase.BeforeEffect)
            {
                return ReplayEntryKind.PreCancelled;
            }

            if (terminal.Outcome == InteractionOutcome.Rejected)
            {
                return ReplayEntryKind.Rejected;
            }

            if (terminal.Outcome == InteractionOutcome.Faulted)
            {
                return ReplayEntryKind.PreEffectFault;
            }

            // A Succeeded (or otherwise effectful) terminal without a permit is
            // not a §6.1 shape at all.
            shapeViolation = true;
            return ReplayEntryKind.Rejected;
        }

        // ── Stop planning (guarantees.md §5.5–§5.10, §7) ─────────────────────

        private static PlannedStop? PlanStop(
            ValueArray<EvidenceCut> cuts,
            ArtifactClassification classification,
            ValueArray<ReplayEntry> entries,
            ISecretReferenceResolver? secretResolver)
        {
            var candidates = new List<PlannedStop>();

            // The shared static hazard scan is the authority for the spec-named
            // stop shapes; the pre-scan only translates them into stop plans.
            for (var index = 0; index < classification.ReplayHazards.Count; index++)
            {
                var hazard = classification.ReplayHazards[index];
                candidates.Add(new PlannedStop(
                    hazard.Position, StopKindOf(hazard), hazard.Reason));
            }

            // Replay-specific candidates the shared scan does not own:
            // positions at or beyond the first contamination interval are
            // incomparable even when no effect window overlapped it
            // (guarantees.md §3.5 "at or beyond") — the interval's own recorded
            // endpoint is the boundary, not the barrier cut's stream position.
            // Temporal predicates would join here (Incomparable(TemporalPredicate),
            // §5.6), but the v2 predicate grammar cannot express one — the
            // reason stays reserved, not detected.
            var barrier = FirstBarrier(cuts);
            if (barrier != null)
            {
                var position = FirstComparedAtOrBeyond(cuts, barrier.FirstObservedCut);
                if (position != null)
                {
                    candidates.Add(new PlannedStop(
                        position.Value, ReplayStopKind.Contamination, IncomparableReason.Contamination));
                }
            }

            // A recorded Unevaluable assertion answers Incomparable(reason)
            // verbatim at its position (guarantees.md §5.10, §3.3).
            for (var index = 0; index < cuts.Count; index++)
            {
                if (cuts[index] is AssertionEvaluated assertion &&
                    assertion.Outcome.Kind == PredicateEvaluationKind.Unevaluable)
                {
                    candidates.Add(new PlannedStop(
                        assertion.Sequence,
                        ReplayStopKind.RecordedUnevaluable,
                        IncomparableReason.FromUnevaluable(assertion.Outcome.Reason)));
                }
            }

            // A pre-effect infrastructure fault stops before its entry: a
            // healthy replay environment would not reproduce the fault, and
            // re-dispatch could perform the never-permitted effect.
            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index].Kind == ReplayEntryKind.PreEffectFault)
                {
                    candidates.Add(new PlannedStop(
                        entries[index].Admission.Sequence,
                        ReplayStopKind.PreEffectFault,
                        incomparability: null));
                }
            }

            // An unresolvable secret reference stops before the affected entry
            // (recording-replay.md §7).
            for (var index = 0; index < entries.Count; index++)
            {
                var arguments = entries[index].Admission.Arguments.Fields;
                for (var field = 0; field < arguments.Count; field++)
                {
                    if (!arguments[field].IsSecret)
                    {
                        continue;
                    }

                    if (secretResolver == null || !secretResolver.CanResolve(arguments[field].Secret))
                    {
                        candidates.Add(new PlannedStop(
                            entries[index].Admission.Sequence,
                            ReplayStopKind.SecretUnresolvable,
                            incomparability: null));
                        break;
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            // Earliest position wins; the kind order is the deterministic tie-break.
            var earliest = candidates[0];
            for (var index = 1; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (candidate.Position < earliest.Position ||
                    (candidate.Position.Equals(earliest.Position) && candidate.Kind < earliest.Kind))
                {
                    earliest = candidate;
                }
            }

            return earliest;
        }

        private static ReplayStopKind StopKindOf(StaticReplayHazard hazard) => hazard.Kind switch
        {
            StaticReplayHazardKind.OutcomeUnknownShape => ReplayStopKind.OutcomeUnknown,
            StaticReplayHazardKind.ContaminatedEffect => ReplayStopKind.Contamination,
            StaticReplayHazardKind.DuringEffectCancellation => ReplayStopKind.CancellationTiming,
            StaticReplayHazardKind.CancelledAfterEffectTerminal => ReplayStopKind.CancellationTiming,
            StaticReplayHazardKind.PredicateResolutionNotSatisfied =>
                hazard.Reason.HasValue &&
                hazard.Reason.Value.Equals(IncomparableReason.PredicateFault)
                    ? ReplayStopKind.PredicateFault
                    : ReplayStopKind.WaitTiming,

            // Fail closed: a hazard kind this scanner does not know cannot be
            // silently downgraded to a milder stop.
            _ => throw new InvalidOperationException(
                "Unknown static replay hazard kind: " + hazard.Kind),
        };

        private static ExternalMutationBarrier? FirstBarrier(ValueArray<EvidenceCut> cuts)
        {
            for (var index = 0; index < cuts.Count; index++)
            {
                if (cuts[index] is ExternalMutationBarrier barrier)
                {
                    return barrier;
                }
            }

            return null;
        }

        /// <summary>
        /// The first compared position at or beyond the interval boundary:
        /// every R4-compared cut counts — an admission, a terminal, and the
        /// close's final checkpoint are as incomparable beyond the interval as
        /// an effect would be. Only the barriers themselves are not comparison
        /// positions.
        /// </summary>
        private static EvidenceSequence? FirstComparedAtOrBeyond(
            ValueArray<EvidenceCut> cuts, EvidenceSequence boundary)
        {
            for (var index = 0; index < cuts.Count; index++)
            {
                if (cuts[index].Sequence < boundary)
                {
                    continue;
                }

                if (cuts[index].Kind == EvidenceCutKind.ExternalMutationBarrier)
                {
                    continue;
                }

                return cuts[index].Sequence;
            }

            return null;
        }
    }
}
