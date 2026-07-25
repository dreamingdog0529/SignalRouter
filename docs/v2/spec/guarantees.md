# SignalRouter v2 Specification — Guarantees

> **Status:** v2 design draft — normative once the v2 set is accepted
> **Applies to:** SignalRouter v2 (clean-slate design; does not govern the v1 implementation)
> **Companion specs:** [semantic-model.md](semantic-model.md) · [kernel-execution.md](kernel-execution.md) ·
> [observation-state.md](observation-state.md) · [recording-replay.md](recording-replay.md) ·
> [protocol-topology.md](protocol-topology.md) · [adapter-conformance.md](adapter-conformance.md) ·
> [security-resources.md](security-resources.md) · [verification.md](verification.md)

This document is the anchor of the v2 specification set. It fixes, before any component
design, the answers SignalRouter v2 is allowed to give at every failure boundary, the
evidence a recording must contain, and the exact claim a verified replay makes. Every
other spec MUST be consistent with this one; on conflict, this document wins.

The key words MUST, MUST NOT, SHOULD, SHOULD NOT, and MAY are to be interpreted as
described in RFC 2119.

## 1. Scope

Covered here:

- the outcome taxonomies for interactions, recordings, replays, and queries;
- the determinism tiers v2 does and does not promise;
- the ReplayEvidence cut set (E1–E8) and its durability rules;
- terminal artifact shapes and the failure matrix;
- the separation of interaction outcomes from recording outcomes;
- capacity-exhaustion and overflow behavior;
- the precise claim of a verified strict replay.

Not covered here: the shape of the semantic tree ([semantic-model.md](semantic-model.md)),
kernel scheduling ([kernel-execution.md](kernel-execution.md)), store implementation
contracts ([recording-replay.md](recording-replay.md)), and wire/protocol behavior
([protocol-topology.md](protocol-topology.md)). This document references their vocabulary;
their normative definitions live there.

## 2. Honest uncertainty

SignalRouter v2 treats uncertainty as a first-class answer, never as something to paper
over:

- **`OutcomeUnknown`** — the system cannot prove what an interaction did. It MUST NOT be
  converted into `Faulted`, `Succeeded`, or any other invented terminal.
- **`Incomparable`** — a replay comparison cannot be evaluated (missing profile support,
  incomplete view, unknown mandatory extension, contamination). It is distinct from
  `Diverged` and MUST NOT be collapsed into it.
- **`Incomplete` / `Interrupted`** — a recording artifact that does not contain everything
  its contract promised, distinguished by whether the closing cut is present (§6.3).

Any component that would have to guess MUST answer with the applicable uncertainty value
instead.

## 3. Outcome taxonomies

### 3.1 Interaction outcomes

An admitted mutation interaction terminates in exactly one of:

| Outcome | Definition |
|---|---|
| `Succeeded` | The capability invocation completed per its completion profile |
| `Rejected` | Validation refused the invocation before any effect was permitted; zero effects |
| `Faulted` | An effect was permitted and execution stopped on a fault; partial effects possible |
| `Cancelled` | Cancellation was observed; `CancellationEvidence.phase` records whether any effect was permitted |
| `OutcomeUnknown` | The runtime cannot prove which of the above occurred |

`Rejected` MUST imply `effectPermitted = false` (no E3 cut was appended, §5.3).
One exception widens `Faulted`: a **pre-effect evidence failure** — the runtime is
alive but cannot make the E3 permit durable (§5.3) — terminates the interaction
`Faulted` with `effectPermitted = false`, zero effects, and the reserved fault code
`EvidenceUnavailable` (§3.5). This is the "pre-effect evidence failure" of §6.1's
first shape; apart from it, `Faulted` implies an effect was permitted.
An interaction whose effect window overlapped a contamination interval (§5.5) additionally
carries `contaminated = true` regardless of outcome.

### 3.2 Recording outcomes

A recording artifact is classified by the reader as exactly one of:

| Outcome | Definition |
|---|---|
| `Completed` | E7 present with `Completed`, closure verification passes, no unresolved commitments |
| `Incomplete(reason)` | E7 present and durable, declaring why the contract was not fully met (canonical codes: §3.5) |
| `Interrupted` | E7 absent; the reader infers interruption. Never self-declared |
| `OpenFailed` | E1 (or its base snapshot) never became durable; no artifact exists |

`Failed` is the outcome of the *recording control operation* as reported to the caller;
it does not name an artifact state, because a sink fault may make it impossible to write
anything further to the artifact (§7).

### 3.3 Replay comparison outcomes

Every comparison performed under a `ReplayComparisonProfile`
([recording-replay.md](recording-replay.md) §5) answers exactly one of:

| Outcome | Definition |
|---|---|
| `Equal` | Typed exact comparison over the profile's field set matched |
| `Diverged` | The comparison was evaluable and did not match |
| `Incomparable(reason)` | The comparison cannot be evaluated: unsupported profile version, incompleteness where the profile requires completeness, unknown mandatory extension, missing migration, contamination, cancellation timing (canonical codes: §3.5) |

A replay run stops at the first non-`Equal` comparison and reports it; `Incomparable`
MUST NOT be reported as `Diverged`. A live assertion's `Unevaluable(reason)` outcome
([verification.md](verification.md) §2.3) maps to `Incomparable(reason)` at replay —
`Incomparable` exists only on the replay side and is never a recorded expectation.

### 3.4 Query answers

Any status query (gateway → runtime, client → gateway) answers exactly one of
`Pending`, `Terminal(outcome)`, `RuntimeUnavailable`, or `OutcomeUnknown`. Fabricating a
terminal for an unreachable runtime is prohibited ([protocol-topology.md](protocol-topology.md) §6).

### 3.5 Canonical reason codes

Reason codes are stable, case-sensitive identifiers compared ordinally, never
normalized. The codes below are reserved: the described condition MUST answer with the
listed code, and no other meaning may be attached to it. The vocabularies are **open**
(§10): implementations and applications may carry additional codes, and a reader
encountering an unknown code MUST present it verbatim and MUST NOT branch execution on
a meaning it does not know.

**`Rejected(reason)`** ([kernel-execution.md](kernel-execution.md) §3, §5):

| Code | Condition |
|---|---|
| `RequestIdConflict` | Duplicate `RequestId` with a different semantic fingerprint |
| `CapacityExhausted` | Mutation-lane or `RecoveryIndex` capacity refused the admission (§8) |
| `ReentrantDispatch` | Nested submission from inside an effect handler |
| `TargetNotFound` | The target cannot be resolved to exactly one node **for this principal**: unregistered, ambiguous, or not exposed to the principal's security domain — deliberately indistinguishable |
| `CapabilityUnavailable` | The resolved node is visible to the principal, but the invoked capability is not invocable there: its **availability state** is off (disabled by the adapter/application), **or it is not declared / not exposed on this node for this principal** — merged for the same concealment reason |
| `PreconditionFailed` | A **validation precondition declared in the capability contract** ([semantic-model.md](semantic-model.md) §2.2) evaluated false during `Validating`. Disjoint from `CapabilityUnavailable` by source: availability is adapter/application-declared state, a precondition is a contract-declared predicate |
| `IncarnationMismatch` | The submission references a stale `RuntimeIncarnationId` ([kernel-execution.md](kernel-execution.md) §10) |
| `UnkeyedTarget` | The strict open policy of an active recording refused admission of an invocation resolving to a node without an `AuthorKey` (§5.2) |

**Existence concealment.** `TargetNotFound` and `CapabilityUnavailable` are
deliberately coarse: no code distinguishes "does not exist" from "exists but is hidden
from you", so a rejection can never become an existence oracle for hidden targets
([security-resources.md](security-resources.md) §4). The concealment is
**observational equivalence**, not just code choice: for an unauthorized target the
admission answer, dedup behavior, `RecoveryIndex` footprint, trace emission, query
answers, and `LogicalOrder` consumption MUST be indistinguishable from the
unregistered-target case (timing side channels are outside the promised tiers, §4).
The same rule extends to status queries: a `RequestId` outside the querying
principal's authority answers exactly as an unknown id.

**`Incomplete(reason)`** (§3.2, §5, §7):

| Code | Condition |
|---|---|
| `SizeLimit` | The recording reached a configured capacity bound (§8) |
| `ExternalMutation` | An E5 barrier terminated the artifact per the open policy (§5.5) |
| `SinkFault` | The recording sink faulted while the artifact was still writable (§7) |
| `ContractChanged` | A capability, state-source, or predicate contract was registered during the active recording (§5.1) |
| `UnkeyedTarget` | A permitted invocation resolved to a node without an `AuthorKey` under the strict open policy (§5.2) |
| `IncarnationChanged` | The runtime incarnation changed while the artifact was writable (§7) |

**`Incomparable(reason)`** (§3.3):

| Code | Condition |
|---|---|
| `UnsupportedProfileVersion` | The replayer does not support the pinned profile version |
| `Incompleteness` | The view is incomplete in a region the profile requires ([observation-state.md](observation-state.md) §3) |
| `UnknownMandatoryExtension` | The artifact carries an unknown extension the profile marks mandatory |
| `MissingMigration` | No projection exists onto a common comparison profile ([semantic-model.md](semantic-model.md) §5) |
| `Contamination` | The position lies at or beyond a contamination interval (§5.5) |
| `CancellationTiming` | A `DuringEffect` cancellation, or a `Cancelled` terminal with `phase = AfterEffect` (§5.7) |
| `TemporalPredicate` | A temporal predicate without interval evidence (§5.6) |
| `PredicateFault` | A recorded E6 resolution of `Faulted` (§5.6) |

Additionally, **every `Unevaluable` reason code is a valid `Incomparable` reason
code**: the §3.3 mapping of a live `Unevaluable(reason)` to replay-side
`Incomparable(reason)` preserves the reason verbatim.

**`Unevaluable(reason)`** ([verification.md](verification.md) §2.3). The vocabulary
deliberately mirrors the `CompletenessMap` reasons of
[observation-state.md](observation-state.md) §3 — an evaluation is unevaluable
precisely because of a completeness condition on its input:

| Code | Condition |
|---|---|
| `Redacted` | A referenced field is withheld by redaction policy |
| `OutOfScope` | A referenced field is outside the view's scope or exposure policy |
| `Incompleteness` | A referenced region is incomplete (e.g. `Virtualized`, `BudgetTruncated`) |
| `UnsupportedContract` | The predicate or source contract version is not supported |
| `SourceUnavailable` | A referenced state source produced no document |
| `Stale` | A sampled source's document is older than its freshness bound |

**Fault codes** (`Faulted`, §5.4) are an open, application/adapter-defined vocabulary
of stable codes — never exception types, messages, or stacks. Two codes are reserved:
`CompletionPostconditionNotSatisfied` (a capability-contract postcondition terminated
the interaction, with stable detail `False | TimedOut | Unknown`;
[verification.md](verification.md) §3.4) and `EvidenceUnavailable` (pre-effect
evidence failure, §3.1).

Non-taxonomy terms, for clarity: `NotAdmitted` (§7) describes the submitter-visible
condition "no admission happened" and is not a sixth interaction outcome; `Failed`
(§3.2) is the answer of the recording *control operation*, not an artifact state;
`HumanIntentBlocked` (§8) is a trace marker, not an outcome.

## 4. Determinism tiers

v2 specifies determinism in three tiers and promises only the first two:

1. **Serializable** — at execution time there exists a single total order of admitted
   mutation interactions, established at admission (the mailbox linearization point,
   [kernel-execution.md](kernel-execution.md) §4) and recorded as `LogicalOrder`.
2. **Replayable** — the recorded order and the recorded semantic cuts are sufficient to
   re-execute the same capability invocations in the same order and compare observations
   at the same cut points.
3. **Timing-deterministic** — reproduction of frame counts, wall-clock timing, delta batch
   boundaries, or mid-effect cancellation timing. **This is a non-goal.** No v2 mechanism
   may promise it, and no comparison may require it (hence §3.3's `Incomparable` for
   cancellation timing, and the prohibition on comparing intermediate delta sequences in
   [recording-replay.md](recording-replay.md) §5).

## 5. ReplayEvidence cut set

A recording consists of two lanes ([recording-replay.md](recording-replay.md) §3):
**ReplayEvidence** (non-droppable, defined here) and **TimelineTrack** (optional,
coalescible diagnostics). "Non-droppable" means exactly:

> Every event the recording contract designates as ReplayEvidence either becomes durable
> in the artifact, or the artifact ceases to be `Completed` — by a durable
> `Incomplete(reason)` close, or by reader-inferred `Interrupted`.

Losslessness of *capture* is not promised for uncontrollable external input; what is
promised is that loss is never silent (§8).

Durability obligations below apply only while a recording is active. Outside a recording,
the kernel emits the same semantic events to `KernelTrace` (lossy, bounded) without
durability obligations.

### 5.1 E1 `RecordingOpened`

The manifest header. Contains: the `ReplayComparisonProfile` (ID and version), the record
`ViewContract` ID/version, the redaction policy ID, the immutable table of
`CapabilityContractId → CompletionProfileId` bindings in force, the immutable tables of
registered state-source contracts and predicate contracts
([observation-state.md](observation-state.md) §7, [verification.md](verification.md) §2),
the `RuntimeIncarnationId`, and the `ContentId` of the base observation snapshot.

- E1 is a **linearization cut on the mutation lane**: opening drains in-flight mutation
  interactions to their E4, then materializes the base snapshot, then appends E1, then
  admits subsequent mutations into the recording (the open fence).
- Commit order MUST be StateStore-first: base snapshot blob durable and pinned → E1
  appended ([recording-replay.md](recording-replay.md) §4).
- The contract tables are immutable for the artifact's lifetime. Registration of a
  capability, state-source, or predicate contract during an active recording either is
  refused or terminates the recording as `Incomplete(ContractChanged)`; v2 does not
  define a `ContractCatalogExtended` cut.

### 5.2 E2 `AdmissionCut`

One per admitted mutation interaction. Contains: `RequestId`, semantic fingerprint, the
capability invocation (capability contract ID/version, the **resolved** target
`AuthorKey`, redacted arguments), and the identity envelope (`Principal`, `Ingress`,
`Provenance`, `Causality`). A strict recording requires an `AuthorKey`-resolvable
target: an invocation whose resolved node has none is either refused admission or
closes the artifact `Incomplete(UnkeyedTarget)`, per the open policy
([semantic-model.md](semantic-model.md) §3.2). For a continuation, `Causality` MUST
carry `ParentRequestId + ContinuationOrdinal + fingerprint` (§5.8).

E2 MUST be durable **before any UI effect of that interaction begins** (inherited from
v1's request-before-side-effect guarantee).

### 5.3 E3 `BeforeCut` (EffectPermit)

One per interaction whose effect is permitted. E3 is not a mere reference — it is the
**durable permit** that gates the adapter invocation:

```text
fix current observation revision (SourceRevision / ViewWatermark)
  → make the before record-view materialization durable and pinned in StateStore
  → append E3 (EffectPermit) durably
  → only then permit the adapter invocation
```

- E3 is appended fresh for every permitted interaction. **Blob reuse is permitted; cut
  reuse is not.** An E3 MAY reference the `ContentId` of a prior checkpoint blob only if
  all of the following hold: same `ViewContract`/version, comparison profile, and
  redaction/security domain; completeness sufficient for the pinned profile; the blob is
  pinned to this artifact; the current `SourceRevision`/`ViewWatermark` is recorded in the
  cut; and either a gap-free invalidation token proves `NoRelevantMutation` since that
  blob — where relevant mutations include node changes **and state-source publications**
  ([observation-state.md](observation-state.md) §7) — or re-materialization produced the
  same `ContentId`. Absence of an observed E5 is NOT acceptable proof.
- The first E3 after an `ExternalMutationBarrier` (§5.5) MUST use a fresh
  materialization; checkpoint reuse across a barrier is prohibited.
- E3 is taken immediately before `Invoking`, not at admission, so that effects of
  interactions executed while this one was queued are included in its before-state.
- Interactions that terminate without a permitted effect (rejection, pre-effect cancel)
  legitimately have no E3 (§6.1).

### 5.4 E4 `TerminalCut`

One per interaction that reaches a provable terminal while the recording is active.
Contains: the `RequestId`, the terminal outcome (§3.1), the completion evidence
required by the bound `CompletionProfileId` (e.g. `Applied`, `FrameCommitted`,
`PostconditionSatisfied`, `AdapterAcknowledged`), the stable application fault code for
`Faulted` (never exception type/message/stack), the after record-view `ContentId`, the
final evaluation of any capability postcondition (including `TimedOut` / `False` /
`Unknown` when that contributed to the terminal), `CancellationEvidence` when
cancellation was involved (§5.7), and the ordered `ContinuationCommitment[]` (§5.8).

Field presence follows the terminal: the after record-view `ContentId` is present in
**every** E4. For an effect-permitted terminal it is the fresh after-effect
materialization; for a zero-effect terminal (§6.1 first shape) it records the
observation state the terminal was decided against — not the admission state, since
interactions executed while this one was queued may have advanced the revision. Blob
reuse is permitted under the §5.3 conditions; the cut is always fresh. This keeps
`afterRequestId` bindings ([verification.md](verification.md) §3.2) and R4's
after-semantics comparison valid for every terminal. The completion evidence is the
**evidence itself** — the material the bound profile demands — not merely the profile
reference, and is required exactly for `Succeeded`.

### 5.5 E5 `ExternalMutationBarrier`

Appended when an `ObservedExternal` effect ([adapter-conformance.md](adapter-conformance.md) §6)
— including a state-source mutation whose causation is external to the controlled work
([observation-state.md](observation-state.md) §7) — intersects the recording. E5 records
a **contamination interval**, not a point:
`lastKnownCleanCut .. firstObservedCut` — both endpoints are `EvidenceSequence`
positions in this artifact's evidence stream
([recording-replay.md](recording-replay.md) §2) — plus the `SourceRevision` at
detection, a source hint, and the `RequestId`s of any interactions whose effect window
overlaps the interval (marked `contaminated`).

Strict replay MUST pre-scan the artifact and stop **before permitting the effect** of the
first contaminated interaction — not upon reaching E5's position in the stream.

Depending on recording policy, E5 either continues the artifact (with the barrier and the
fresh-materialization rule of §5.3) or terminates it as `Incomplete(ExternalMutation)`.

### 5.6 E6 `PredicateArmed` / `PredicateResolved`

For explicit waits (`wait_for`), both cuts are ReplayEvidence:

- `PredicateArmed`: `OperationId`, predicate contract ID/version, redacted operands +
  semantic fingerprint, `ViewContract`/scope, `Causality`, armed evidence sequence.
- `PredicateResolved`: outcome ∈ `Satisfied | TimedOut | Cancelled | Faulted | Unknown`,
  the witness (for `Satisfied`) or final-observation `ContentId`, resolved evidence
  sequence.

Capability postconditions are NOT separate E6 cuts; their final evaluation is embedded in
E4 (§5.4).

Replay semantics: only a `Satisfied` resolution of a snapshot-local pure predicate is
re-executed — the replayer re-evaluates the predicate at that position and requires it to
become true; it does not require bytewise equality with the recorded witness. `TimedOut`,
`Cancelled`, `Faulted`, and `Unknown` resolutions stop strict replay before execution of
the wait (`TimedOut`/`Cancelled`/`Unknown` because timing is out of tier, §4; `Faulted`
because a predicate-evaluation fault is an infrastructure condition, not a reproducible
observation — the stop reports `Incomparable(PredicateFault)`, §3.5). Temporal predicates (e.g. "remains true for N frames")
require interval evidence, which v2 defers; a recording containing one is
`Incomparable(TemporalPredicate)` under strict replay.

Unsatisfied polling observations during the wait belong to TimelineTrack.

### 5.7 CancellationEvidence

Whenever cancellation contributes to a terminal, E4 embeds:
`requestedOrder` (logical order at which cancel was requested), `observedOrder` (at which
it was observed), `phase ∈ BeforeEffect | DuringEffect | AfterEffect`, the disposition,
and the `effectPermitted` / `effectStarted` flags.

Replay: `BeforeEffect` cancellations are replayed deterministically with a synthetic
pre-cancelled token; `DuringEffect` cancellations stop strict replay as
`Incomparable(CancellationTiming)`. `AfterEffect` splits on the terminal it is attached
to: evidence on a non-`Cancelled` terminal (a cancel that arrived too late to matter)
replays as a normal terminal, with the `CancellationEvidence` excluded from strict
comparison as timing metadata (§4) — a live run that observes no cancellation still
compares `Equal`; a terminal that is itself `Cancelled` with `phase = AfterEffect`
(cancellation terminated post-effect work) stops strict replay as
`Incomparable(CancellationTiming)`, like `DuringEffect`.

### 5.8 Continuations

- The parent's E4 carries an ordered `ContinuationCommitment[]` naming each child it
  committed to spawn.
- A child is admitted only after its parent's E4 is durable; its E2 `Causality` carries
  `ParentRequestId + ContinuationOrdinal + fingerprint`.
- Replay binds live continuations to recorded children by
  `(ParentRequestId, ContinuationOrdinal)` and executes each exactly once within the
  replay run.
- An artifact with unresolved commitments (a commitment with no matching child E2/E4
  chain) MUST NOT be closed as `Completed`.

### 5.9 E7 `RecordingClosed`

The close fence, also a linearization cut on the mutation lane: interactions admitted
before the membership fence are drained to E4, **armed predicates still open at the
fence are resolved** (as `Cancelled` when nothing else resolves them first, §5.6), the
final snapshot is materialized and pinned, then E7 is appended. Contains: the close reason (`Completed` or
`Incomplete(reason)`), the event count, the final checkpoint `ContentId`, and closure
material the reader can **recompute** — at minimum the event count and the manifest root
/ reachable-`ContentId` set. The recomputation targets are defined exactly: the **event
count** is the number of ReplayEvidence cuts in the artifact, E1 and E7 themselves
included; the **reachable-`ContentId` set** is every `ContentId` referenced by any
ReplayEvidence cut (the E1 base snapshot, E3 before views, E4 after views, E6
witnesses/final observations, E8 evaluated snapshots, and the E7 final checkpoint). A
self-declared boolean is not sufficient; readers MUST verify closure themselves.

Absence of E7 is meaningful: the reader classifies the artifact `Interrupted` (§3.2).

### 5.10 E8 `AssertionEvaluated`

One per standalone assertion ([verification.md](verification.md) §3) evaluated while the
recording is active. E8 is an **atomic single cut**: it opens no commitment, the close
fence neither waits for nor cancels it, and it imposes no closure obligation (§6.2 R5).

Contains the full evaluation identification: `RuntimeIncarnationId`, the
`SourceRevision`/`ViewWatermark` of the evaluated materialization
([observation-state.md](observation-state.md) §4), `ViewContractId@version`, the state-source contract table version,
scope and security domain, the evaluated snapshot's `ContentId`, its completeness, the
predicate contract ID/version with redacted operands (secret operands as secret
references), stable clause IDs with expected/actual evaluations, the outcome
(`Satisfied | False | Unevaluable(reason)`), and bounded, redacted witness paths.
Per-clause structured explanations are diagnostic material and are never part of strict
comparison.

Replay semantics: the replayer re-evaluates the predicate at the same position against
the corresponding materialization and requires the **same outcome** — a recorded
`Satisfied` must re-evaluate `Satisfied`, a recorded `False` must re-evaluate `False`
(replay *fidelity*; whether a `False` fails the *case* is the separate verdict axis,
§9). A recorded `Unevaluable(reason)`, or an evaluation that cannot be performed at
replay, answers `Incomparable(reason)` (§3.3).

## 6. Terminal artifact shapes

### 6.1 Per-interaction shapes

| Shape | Meaning |
|---|---|
| E2 + E4 with `effectPermitted = false` | Rejection, pre-effect cancellation, or pre-effect evidence failure; zero effects |
| E2 + E3 + E4 | Effect permitted, terminal known — the normal replayable shape |
| E2 only | No effect was permitted (E3 is the permit), but the artifact is not `Completed`; reader treats the interaction as evidence-incomplete |
| E2 + E3, no E4 | Effect may or may not have occurred: `OutcomeUnknown`. Strict replay stops before permitting this interaction's effect |

### 6.2 Rules

- **R1 (shape completeness):** every admitted interaction inside a `Completed` artifact
  MUST have one of the first two shapes, and every `PredicateArmed` MUST have a matching
  `PredicateResolved`. Any other shape — including an unmatched armed predicate —
  forces the artifact to close as `Incomplete` or be read as `Interrupted`. R1 also
  fails on structurally invalid evidence: duplicate cuts for one `RequestId` (a second
  E2, E3, or E4), an E3 or E4 whose `RequestId` has no preceding E2, interaction cuts
  out of order (E4 before E3 where both exist, or either before E2), or a
  `PredicateResolved` without a matching `PredicateArmed`.
- **R2 (control lane):** control-lane operations (cancel requests, queries) are not
  ReplayEvidence; their influence surfaces only through `CancellationEvidence` in E4.
- **R3 (continuations):** as §5.8; unresolved commitments block `Completed`.
- **R4 (comparison targets):** strict replay compares evidence from **all** cuts — E1
  (initial base semantics), E2 (AuthorKey resolution, contract/version, argument and
  secret resolution), E3 (before semantics), E4 (terminal + after semantics), E6
  (predicate re-evaluation), E7 (final semantics and artifact closure), E8 (assertion
  re-evaluation, §5.10). Restricting comparison to E3/E4 would leave zero-mutation
  recordings, wait-only recordings, and the final reached state unverified.
- **R5 (assertions are closure-free):** E8 cuts are self-contained. An artifact may
  close `Completed` with any number of E8 cuts and any mix of E8 outcomes; whether a
  `False` required assertion disqualifies the artifact as a *verification case* is a
  seal condition ([verification.md](verification.md) §5), never an artifact-completeness
  condition.

### 6.3 Artifact-level shapes

| Artifact state | Reader classification |
|---|---|
| Base blob or E1 not durable | `OpenFailed` (orphan blobs are GC-eligible) |
| E1 present, E7 with `Completed`, closure verifies, R1/R3 hold | `Completed` |
| E1 present, E7 with `Incomplete(reason)` | `Incomplete(reason)` |
| E1 present, no E7 | `Interrupted` |
| E7 present but closure verification fails | `Interrupted` (tampered or torn; never `Completed`) |

Classification precedence: reader verification overrides the writer's self-declaration.
An E7 declaring `Completed` over evidence that violates R1 or R3 reads `Interrupted`
(the declaration is untrustworthy), and an E7 declaring `Incomplete(reason)` whose
closure verification fails also reads `Interrupted`. `Incomplete(reason)` is honored
only when E7 is durable **and** its closure material verifies.

## 7. Failure matrix

Interaction outcome and recording outcome are **separate columns**; a recording failure
never rewrites an interaction's real result. In particular: if E4 cannot be appended
after an effect completed, the interaction's true terminal is still committed to the
`RecoveryIndex` (so resubmission does not re-execute the effect) while the recording
alone fails. This preserves v1's recorder-poisoning stance.

| Failure point | Interaction answer | Recording/replay answer |
|---|---|---|
| Before base blob / E1 durable | not affected (recording not yet active) | `OpenFailed`; orphan blobs GC-eligible |
| Before E2 (admission refused or crash pre-admission) | `NotAdmitted` (submitter sees `Rejected` or no admission ack) | no evidence required; TimelineTrack at most |
| After E2, before E3 | no effect occurred (E3 is the permit) | artifact `Incomplete`/`Interrupted`; re-execution policy is the caller's, guarded by fingerprint dedup |
| After E3, before E4 | `OutcomeUnknown` | strict replay stops before this effect |
| Effect done, E4 append fails (sink fault) | true terminal preserved via `RecoveryIndex` | recording control op reports `Failed`; artifact `Incomplete(SinkFault)` if writable, else `Interrupted` |
| After E4, before E7 | terminal known and queryable | artifact `Interrupted` (unclosed) |
| E7 write fault | terminals known | not `Completed`; reader infers `Interrupted` |
| Pre-effect cancel | `Cancelled` with `phase = BeforeEffect` | replayable with synthetic cancelled token |
| Mid-effect cancel | terminal may be known (`Cancelled`, `phase = DuringEffect`) | `Incomparable(CancellationTiming)` at that entry |
| External mutation during active interaction | interaction marked `contaminated`; outcome per its own evidence | E5 interval; strict replay stops before the contaminated effect |
| Runtime crash | pending → `OutcomeUnknown` after retention expiry; incarnation changes | artifact `Interrupted` unless E7 was durable |
| Gateway crash / disconnect | unaffected — runtime is the authority; caller re-queries | unaffected |
| Incarnation change | stranded requests are never auto re-executed; queries answer `OutcomeUnknown` after retention | recording bound to old incarnation closes `Incomplete(IncarnationChanged)` if writable, else `Interrupted` |
| Revision-bound source publish refused (mailbox overflow) | publisher receives an explicit refusal; no partial document swap ever occurs | no evidence obligation (nothing was observed) |
| Assertion evaluated, crash before E8 append | the live caller may have received the answer; the artifact holds no E8 | artifact `Incomplete`/`Interrupted`; replay treats the position as having no assertion |
| Case seal fails (conditions unmet) | not an interaction concern | artifact remains a diagnostic recording; no `VerificationCaseManifest` is produced ([verification.md](verification.md) §5) |
| Capacity exhaustion | new admissions refused (`Rejected(CapacityExhausted)`); active work unaffected | per recording policy: `Incomplete(SizeLimit)`, chunk rollover, or normal close at configured bounds (§8) |

Cells not listed (e.g. sink fault while no recording is active) reduce to the
interaction column alone; `KernelTrace` loss is always permitted and never an error.

## 8. Capacity, overflow, and the no-silent-drop rule

- Every store is bounded; bounds are configuration with specified defaults
  ([security-resources.md](security-resources.md) §5).
- `RecoveryIndex`: pending entries are never evicted; terminal entries expire after a
  retention window; at capacity, **new admissions are refused**, never existing entries
  dropped.
- `RecordingSink`: capacity policy is declared at open time — stop admitting new
  mutations, close `Incomplete(SizeLimit)`, chunk rollover, or bounded normal
  completion. The policy MUST be chosen before overflow can occur, not improvised at it.
- Human/external input cannot be backpressured. Therefore **complete capture is not
  promised**; what is promised is: an event designated ReplayEvidence is never silently
  dropped — overflow visibly degrades the artifact (`Incomplete`) or refuses admission.
  Dropped human intents MUST be recorded as `HumanIntentBlocked` in the trace
  ([kernel-execution.md](kernel-execution.md) §7); silent drop is prohibited everywhere.

## 9. What a verified replay proves

A strict replay that completes with all comparisons `Equal` proves:

> **Observational equivalence relative to the pinned `ReplayComparisonProfile`** — the
> same admitted capability invocations, in the same order, produced record-view
> observations and terminal evidence that compare exactly equal under the profile's
> typed comparison rules, on a runtime whose contract table is compatible with the
> artifact's.

It does **not** prove application equivalence. Side effects outside the observed
profile — database writes, audio, network traffic, analytics — are unverified. Specs and
user documentation MUST NOT use the phrase "application equivalence" for this guarantee.

Replay fidelity is also **not a test verdict**. A diagnostic recording that faithfully
reproduces a `False` assertion is entirely `Equal` and still not a passing test; a
sealed case whose required assertion re-evaluates `False` is an E8 fidelity divergence
that the verdict axis reports distinctly as `FailedAssertion`. Case verdicts
(`Passed | Diverged | FailedAssertion | Unevaluable | InfrastructureFailed`) are a
separate, independently versioned taxonomy defined in
[verification.md](verification.md) §6; the two axes MUST NOT be merged in any report or
tool answer.

Replay artifacts are executable input and sit on a trust boundary: before execution the
replayer MUST enforce artifact integrity (ContentId verification), size/depth/node-count
limits, the capability contract allowlist, and in-memory secret resolution; execution of
untrusted artifacts is refused ([security-resources.md](security-resources.md) §6).

## 10. Versioning

This spec is versioned with the v2 set. Any change that alters an outcome taxonomy, a
cut's durability rule, a terminal shape, or the failure matrix is a breaking change to
recorded artifacts and requires a new recording schema major version plus an ADR.

Reason-code vocabularies (§3.5) are the one deliberately open surface: **reserving an
additional code** for a previously unnamed condition is a minor revision of this spec —
readers MUST present unknown codes rather than reject them, so new codes do not break
recorded artifacts. **Renaming or removing a reserved code, or changing the condition
it names**, alters an outcome taxonomy and falls under the breaking rule above
([adr 0009](../adr/0009-evidence-ordering-and-open-reason-vocabularies.md)).
