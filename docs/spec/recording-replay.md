# SignalRouter v2 Specification — Recording and Replay

> **Status:** v2 design draft — normative once the v2 set is accepted
> **Applies to:** SignalRouter v2 (clean-slate design)
> **Companion specs:** [guarantees.md](guarantees.md) · [semantic-model.md](semantic-model.md) ·
> [observation-state.md](observation-state.md) · [kernel-execution.md](kernel-execution.md) ·
> [security-resources.md](security-resources.md)

This spec defines the recording artifact (lanes, commit order, manifest), the
`ReplayComparisonProfile`, the three replay modes, and the replay trust boundary. The
evidence cut set (E1–E8), durability rules, terminal shapes, and failure matrix are
normative in [guarantees.md](guarantees.md) and are not restated here. The key words
MUST, MUST NOT, SHOULD, and MAY follow RFC 2119.

## 1. Recording as a first-class operation

Recording is a control operation on the live runtime — no runtime recreation, no
incarnation change, no adapter rebinding. Opening and closing are linearization fences
on the mutation lane ([guarantees.md](guarantees.md) §5.1, §5.9). A recording is
identified by an `OperationId` and produces one artifact. Promoting an artifact into a
CI verification case is a separate, condition-gated step
([verification.md](verification.md) §5).

## 2. Artifact structure

An artifact consists of:

- a **manifest** — E1 header plus the closure material E7 references;
- the **evidence stream** — append-only ReplayEvidence cuts, each carrying its
  artifact-local **`EvidenceSequence`** (the monotonic append position,
  [semantic-model.md](semantic-model.md) §4). Interaction cuts (E2/E3/E4) additionally
  carry their interaction's `LogicalOrder`. Stream order is constrained **per cut
  kind**: E2 cuts appear in `LogicalOrder` (admission order is append order); one
  interaction's E2 → E3 → E4 appear in that order; and, because mutation execution is
  serialized ([kernel-execution.md](kernel-execution.md) §4), the E3/E4 cuts of
  different interactions also appear in their interactions' `LogicalOrder`. Cross-kind
  interleaving is normal — a queued interaction's E2 legitimately precedes an active
  interaction's E3/E4. Non-interaction cuts (E1, E5, E6, E7, E8) have no `LogicalOrder`
  and are positioned by `EvidenceSequence` alone;
- the optional **timeline stream** — TimelineTrack events (§3);
- the **blob set** — the `StateStore` materializations pinned by the evidence.

The artifact format is `RecordingEventSchema@major.minor`
([observation-state.md](observation-state.md) §6). Readers MUST reject an unsupported
major version rather than guess. Append durability uses an explicit commit marker so a
torn final record is detectable and discarded (the v1 newline-commit lesson is retained
as a format obligation, not a JSON-Lines commitment).

## 3. Two lanes

| Lane | Contract |
|---|---|
| `ReplayEvidence` | Non-droppable ([guarantees.md](guarantees.md) §5); commits directly from kernel authoritative transitions to the durable sink |
| `TimelineTrack` | Optional diagnostics: intermediate deltas, unsatisfied wait polls, animation-scale changes. Subject to per-profile coalescing, sampling, and byte-rate caps; loss is permitted and marked |

Strict replay never depends on TimelineTrack. Comparing intermediate delta sequences is
prohibited — count, batching, and arrival order are timing artifacts outside the
guaranteed tiers ([guarantees.md](guarantees.md) §4).

## 4. Commit order and blob lifecycle

- **StateStore-first:** blob durable and pinned → evidence cut appended → manifest
  reachability updated. A crash can orphan a blob (GC-eligible) but never a dangling
  evidence reference.
- Every delta blob records `baseContentId → resultContentId`; chains between
  checkpoints are bounded by the recording's declared chain bound capped at
  `StateStore.MaxChainLength` ([security-resources.md](security-resources.md) §5) —
  chain length is storage encoding, never comparison semantics, so it is declared
  with the recording sink bounds, not in the comparison profile; periodic checkpoints
  are full materializations.
- On close, the artifact either becomes **self-contained** (blobs embedded/copied) or
  explicitly declares its external `StateStore` dependency; a reader MUST be able to
  verify manifest closure — every referenced `ContentId` resolvable and verifiable —
  without trusting the writer ([guarantees.md](guarantees.md) §5.9).
- Secret values never enter blobs (redaction precedes materialization); sensitive
  arguments in E2 are stored as secret references resolved only in memory at replay
  ([security-resources.md](security-resources.md) §3).

## 5. ReplayComparisonProfile

The profile pinned in E1 defines the entire comparison. It is versioned and contains at
minimum:

- record `ViewContractId@version` and scope;
- redaction policy ID;
- node matching rules (AuthorKey-based; item-key rules for dynamic collections);
- the compared field set per node kind and capability;
- the state sources in strict scope, with per-source compared field sets under their
  contract's stable paths ([observation-state.md](observation-state.md) §7);
- collection comparison rules (ordered / set / multiset per field);
- value normalization rules;
- completeness requirements ([observation-state.md](observation-state.md) §3);
- unknown-extension policy (which unknown fields are ignorable vs. mandatory);
- schema migration rules (which older profile versions are projectable).

### 5.1 Comparison semantics

Comparison is **typed exact equality over the profile's field set** — not hash equality
and not fuzzy matching. `ContentId` equality is only a fast path; inequality routes to
the typed comparator ([semantic-model.md](semantic-model.md) §5). Every comparison
answers `Equal | Diverged | Incomparable(reason)` ([guarantees.md](guarantees.md) §3.3).

### 5.2 Compared material

Per matched node: hierarchy relation (and order where the profile says ordered), role,
label, value, visibility, enablement, focus, capability contract versions, argument
schemas, availability/preconditions, and completion profile bindings. Per in-scope
state source: the document fields the profile names, compared under the source
contract's typing and collection rules. Terminal comparison covers outcome, fault
code, and completion evidence. The comparator distinguishes `absent`, `null`,
`unknown`, and `redacted` as four different inputs.

### 5.3 Modes

| Mode | Definition | Use |
|---|---|---|
| `StrictSemantic` | Typed exact comparison over the full pinned profile; **the default** | Regression verification |
| `ExactArtifact` | Canonical representation / `ContentId` equality | Same-build determinism and encoder regression checks |
| `AdaptiveGoal` | Locator-based re-resolution, field subsets, tolerances, postcondition-centric goals | Resilient automation; MUST NOT be labeled "strict" in any surface |

Strict-comparison scope requires `AuthorKey`s ([semantic-model.md](semantic-model.md)
§3.2); in `AdaptiveGoal`, locator resolution to zero or multiple nodes is a divergence
or environment incompatibility, per policy.

## 6. Replay execution

- Replay runs on an **isolated runtime** built by the application-supplied replay
  environment factory: replay-only nodes, stages, and stores with no shared
  static/singleton state. The live runtime's mutation lane is gated for the duration
  with visible indication ([kernel-execution.md](kernel-execution.md) §7, §10).
- Replay is a long-running control operation (`OperationId`), resumable across gateway
  reconnects, single-flight per runtime.
- Before execution the replayer **pre-scans** the artifact: verifies closure and
  integrity, locates contamination intervals and stop-points (E5, `OutcomeUnknown`
  shapes, `DuringEffect` cancellations, temporal predicates), and refuses or plans stops
  accordingly ([guarantees.md](guarantees.md) §5.5).
- Execution then proceeds entry by entry: re-admit the recorded invocation (same
  contract version, resolved secrets), compare at every evidence cut per R4, stop at the
  first non-`Equal` answer with a structured report: the recorded expectation, the
  actual observation, and the semantic diff — all built from recording-safe fields only.
- E8 assertions are re-evaluated in place against the corresponding materialization,
  requiring the recorded outcome (`Satisfied` or `False`) to recur; a recorded or
  replay-side `Unevaluable` answers `Incomparable(reason)`
  ([guarantees.md](guarantees.md) §5.10). Replay fidelity here says nothing about the
  case verdict ([verification.md](verification.md) §1).
- Rejected entries are re-dispatched to verify the same rejection and the zero-effect
  guarantee; `BeforeEffect` cancellations replay with a synthetic pre-cancelled token
  ([guarantees.md](guarantees.md) §5.7).

## 7. Trust boundary

A replay artifact is executable input. Before any execution the replayer MUST enforce:

- artifact integrity: manifest closure and `ContentId` verification;
- resource limits: size, depth, node count, event count, blob bytes
  ([security-resources.md](security-resources.md) §5);
- the contract allowlist: only capability, state-source, and predicate contracts
  registered in the target runtime, at compatible versions, may execute or evaluate;
- secret handling: secret references resolve in memory only; unresolvable references
  stop replay before the affected entry;
- provenance policy: artifacts from untrusted sources are refused by default
  ([security-resources.md](security-resources.md) §6).
