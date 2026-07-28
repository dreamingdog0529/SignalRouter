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
    /// no execution, no environment, no secret materialization.
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

            // Provenance gates even parsing: an untrusted artifact is refused
            // before its bytes are interpreted (security-resources.md §6).
            if (trust.Provenance == ArtifactProvenance.Untrusted && !trust.AcceptUntrustedArtifacts)
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
            if (classification.Outcome.Kind == RecordingOutcomeKind.OpenFailed ||
                reading.Profile == null)
            {
                // No usable manifest: nothing exists to replay against.
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
                        if (!admissions.ContainsKey(permit.RequestId) ||
                            permits.ContainsKey(permit.RequestId))
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
                entries[index] = new ReplayEntry(
                    request, ClassifyEntry(permit, terminal), admission, permit, terminal);
            }

            return ValueArray<ReplayEntry>.From(entries);
        }

        private static ReplayEntryKind ClassifyEntry(EffectPermit? permit, TerminalCut? terminal)
        {
            if (terminal == null)
            {
                return permit == null ? ReplayEntryKind.AdmittedOnly : ReplayEntryKind.OutcomeUnknown;
            }

            if (terminal.Cancellation != null &&
                terminal.Cancellation.Phase == CancellationPhase.BeforeEffect)
            {
                return ReplayEntryKind.PreCancelled;
            }

            return permit == null ? ReplayEntryKind.Rejected : ReplayEntryKind.Completed;
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
            // (guarantees.md §3.5 "at or beyond").
            var barrier = FirstBarrier(cuts);
            if (barrier != null)
            {
                var position = FirstExecutionBearingAfter(cuts, barrier.Sequence);
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
            _ => hazard.Reason.HasValue &&
                hazard.Reason.Value.Equals(IncomparableReason.PredicateFault)
                ? ReplayStopKind.PredicateFault
                : ReplayStopKind.WaitTiming,
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

        private static EvidenceSequence? FirstExecutionBearingAfter(
            ValueArray<EvidenceCut> cuts, EvidenceSequence barrier)
        {
            for (var index = 0; index < cuts.Count; index++)
            {
                if (cuts[index].Sequence <= barrier)
                {
                    continue;
                }

                switch (cuts[index].Kind)
                {
                    case EvidenceCutKind.EffectPermit:
                    case EvidenceCutKind.PredicateResolved:
                    case EvidenceCutKind.AssertionEvaluated:
                        return cuts[index].Sequence;
                }
            }

            return null;
        }
    }
}
