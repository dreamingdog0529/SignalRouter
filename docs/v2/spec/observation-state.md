# SignalRouter v2 Specification — Observation and State

> **Status:** v2 design draft — normative once the v2 set is accepted
> **Applies to:** SignalRouter v2 (clean-slate design)
> **Companion specs:** [guarantees.md](guarantees.md) · [semantic-model.md](semantic-model.md) ·
> [kernel-execution.md](kernel-execution.md) · [recording-replay.md](recording-replay.md) ·
> [security-resources.md](security-resources.md)

Observation in v2 is projection, not truth: what leaves the runtime is always a view — a
scoped, contract-governed, completeness-annotated materialization of the node store at a
revision. This spec defines views, snapshots and deltas, resynchronization, the four
stores and their shared event algebra, and the `StateStore`. The key words MUST, MUST
NOT, SHOULD, and MAY follow RFC 2119.

## 1. Observation views

An **observation view** is defined by:

- a `ViewContractId@version` — the versioned projection rules: which node kinds,
  attributes, capability metadata, and state sources (§7) are included, how values are
  normalized, and which redaction policy applies;
- a **scope** — the subtree/filter it observes, plus the `sources/<StateSourceKey>`
  scopes it includes;
- a **security domain** — which principal class it serves.

Standard view families:

| View | Purpose |
|---|---|
| Agent view | What an agent may see; exposure-filtered, bounded |
| Record view | What recordings capture and strict replay compares |
| Diff | Not a separate view: a comparison operation over two materializations produced under the same `ViewContract` |

Views over the same node store may differ in membership and attribute set. The record
view and the agent view are independently versioned contracts; neither is derived from
the other.

## 2. Snapshot identification

A materialized snapshot is identified by:

```text
ObservationSnapshot {
  RuntimeIncarnationId
  SourceRevision
  ViewContractId@version
  ContentId
  CompletenessMap
}
```

`ContentId` alone never identifies an observation semantically; the tuple does. Two
snapshots are comparable only under the same `ViewContract` (or an explicit migration,
[recording-replay.md](recording-replay.md) §5).

## 3. Completeness

`CompletenessMap` is local, not a global boolean. Each omission carries a reason:

| Reason | Meaning |
|---|---|
| `Virtualized` | Subtree not materialized (e.g. virtualized list region) |
| `Redacted` | Field/value withheld by redaction policy |
| `OutOfScope` | Outside the view's scope or exposure policy |
| `BudgetTruncated` | Per-pump or per-view budget cut materialization short |
| `SourceUnavailable` | A state source produced no document (§7) |
| `Stale` | A sampled source's document is older than its declared freshness bound (§7) |
| `UnsupportedContract` | The source's contract version is not supported by this view contract |

Rules:

- Strict replay comparison requires the view to be complete in every region the pinned
  comparison profile requires; otherwise the comparison is `Incomparable(Incompleteness)`
  ([guarantees.md](guarantees.md) §3.3).
- A parent link pointing outside the observed scope terminates traversal as
  `OutOfScope`; it is a completeness condition, not an error.
- `Redacted` marks presence without content, so absence and redaction are never
  conflated (`absent` / `null` / `unknown` / `redacted` are four distinct comparator
  inputs, [recording-replay.md](recording-replay.md) §5.2).

## 4. Delivery: snapshot + delta + resync

- **Pull is the default** for agents: an on-demand snapshot at the current revision.
  Paginated reads MUST pin one `ContentId`-identified snapshot so all pages describe one
  revision.
- **Delta subscription** is the internal mechanism for recording and wait evaluation
  (and MAY be exposed to advanced clients): deltas carry `ViewSequence`, the
  `baseContentId → resultContentId` transition, and are gap-detectable. A subscriber
  observing a `ViewSequence` gap MUST resynchronize from an authoritative snapshot;
  deltas are never trusted across a gap.
- A materialization is **revision-consistent**: every snapshot and delta is produced
  from a single `SourceRevision` via a revision-pinned read; work spanning multiple
  pumps either retains the pin or restarts. A snapshot MUST NOT mix revisions.
- Every observable mutation MUST advance `SourceRevision` — an adapter conformance
  obligation verified by the TCK ([adapter-conformance.md](adapter-conformance.md) §1,
  §7). Delta *delivery* is still not assumed perfect: each delta carries the resulting
  `SourceRevision` watermark and subscriptions emit periodic watermark heartbeats, so a
  subscriber that observes the watermark advance without contiguous deltas treats it as
  a gap and resynchronizes. Recording evidence never depends on subscription liveness:
  the E3/E4 cuts are fresh, revision-stamped materializations
  ([guarantees.md](guarantees.md) §5).
- The **`ViewWatermark`** of a materialization or subscription is its view-side
  high-water mark: the highest `SourceRevision` whose mutations it has fully applied.
  It is a role of `SourceRevision`, not a separate identifier
  ([semantic-model.md](semantic-model.md) §4). Because materializations are
  revision-consistent, a snapshot's watermark equals its pinned `SourceRevision`; a
  subscription's watermark advances with delivery and is the gap-detection signal
  above. Evidence cuts that fix an observation basis (E3, E8) record the watermark of
  the exact materialization they reference, so a reader can prove which observation
  state a cut speaks for without consulting subscription history.
- Delta chains are bounded (maximum chain length between checkpoints,
  [recording-replay.md](recording-replay.md) §4); readers MUST NOT need unbounded chains
  to reconstruct a state.
- Materialization respects the pump budget; truncation surfaces as `BudgetTruncated`
  completeness, never as silent omission ([kernel-execution.md](kernel-execution.md) §6).

## 5. StateStore

The `StateStore` is the content-addressed store for materialized observations
(snapshots and deltas).

- It stores **view materializations** — post-redaction, post-visibility — never raw node
  state. Consequently a blob is safe to hand to any reader inside its security domain.
- Content addresses are namespaced per security domain. A `ContentId` from the record
  view MUST NOT be exposed to agent-domain readers, and cross-domain existence probes
  MUST NOT succeed (a low-entropy secret must not be confirmable by hashing a guess,
  [security-resources.md](security-resources.md) §4).
- Blobs are immutable; `Put` is idempotent by content.
- **Pinning:** a recording pins the blobs its evidence references
  ([recording-replay.md](recording-replay.md) §4); unpinned blobs are GC-eligible.
  Orphan blobs from failed opens are GC-eligible by construction
  ([guarantees.md](guarantees.md) §7).
- `ContentId` structure, verification, and migration follow the artifact contract in
  [semantic-model.md](semantic-model.md) §5.

## 6. The four stores and the shared algebra

The kernel emits semantic events; four stores consume them under **different
persistence semantics**. What is shared is only the in-memory event algebra:

```text
SemanticEventAlgebra (BCL types, no serialization)
  OperationId · EventCausation · RuntimeIncarnationId · EventKind · LogicalOrder
```

- **`EventCausation`** is not a new identifier: it is the union already implied by
  §7.2 — caused by a `RequestId`, caused externally (with a source hint), or uncaused
  (`None`). A continuation's causal binding (`ParentRequestId + ContinuationOrdinal +
  fingerprint`, [semantic-model.md](semantic-model.md) §6) maps into it without loss:
  the causing `RequestId` is the parent, and the ordinal/fingerprint remain on the
  admission envelope.
- **`EventKind`** is an open, kernel-owned vocabulary, non-normative for the
  persistent schemas. The reserved minimum set: `Admitted`, `StateTransition`,
  `EffectPermitted`, `EffectFenceReached`, `TerminalCommitted`,
  `SourcePublicationAdopted`, `PredicateArmed`, `PredicateResolved`,
  `AssertionEvaluated`, `HumanIntentBlocked`, `ContaminationObserved`,
  `IncarnationLifecycle`, `TraceGap`. `RequestId` and `OperationId` participation is
  per kind — mutation events carry a `RequestId`, operation events an `OperationId`;
  neither is universally required.

| Store | Semantics | Persistent schema |
|---|---|---|
| `KernelTrace` | Always on, cheap, bounded, lossy-permitted, gap-detectable | `TraceEventSchema` (independent) |
| `RecoveryIndex` | Pending entries non-evictable; terminals retained for a window; at capacity, refuse new admissions | `RecoveryRecordSchema` (internal, not a public contract) |
| `RecordingSink` | Non-droppable ReplayEvidence per [guarantees.md](guarantees.md) §5; explicit durability contract | `RecordingEventSchema` (public, versioned) |
| `StateStore` | Content-addressed, immutable, pinned/GC | `StateObjectSchema` (public, versioned) |

Rules:

- The four persistent schemas version **independently**. A trace-only diagnostic
  addition never bumps the recording schema; the recovery index's representation is
  never a public contract.
- The `RecordingSink` is **not** a subscriber of the lossy `KernelTrace`: ReplayEvidence
  commits directly from the kernel's authoritative state transitions to the durable
  sink. Trace loss can therefore never silently degrade a recording.
- Sensitive material follows the redaction rule ([semantic-model.md](semantic-model.md)
  §7): no store ever receives an unredacted sensitive value; trace-lane diagnostics MUST
  NOT leak into recording artifacts.

## 7. State sources

Domain state that the node tree cannot represent (inventory, navigation, scene phase —
the v1 state-probe concern) enters observation as **state sources**. A source appears
inside views as its own scope, `sources/<StateSourceKey>`, holding one typed document
governed by a `StateSourceContractId@version`
([semantic-model.md](semantic-model.md) §8).

### 7.1 Two source classes

| Class | Contract | Strict eligibility |
|---|---|---|
| `RevisionBoundStateSource` | The application **publishes** an immutable typed document (with causation) as a kernel message; adoption swaps the document and advances the shared `SourceRevision` atomically ([kernel-execution.md](kernel-execution.md) §4, [semantic-model.md](semantic-model.md) §4) — snapshots, watermarks, and pinned reads therefore identify source documents and node state in one revision order | Comparable under strict replay; assertable, including cross-source and node+source predicates |
| `SampledStateSource` | The document is read at materialization time (may consult external state); carries a declared freshness bound | Diagnostic only: excluded from strict comparison scope and from cross-source atomic assertions; staleness surfaces as `Stale` completeness |

The distinction exists because strict comparison needs point-in-time consistency:
only publication-through-the-kernel gives a document a place in the revision order.
A callback captured at read time (the v1 probe shape) cannot guarantee that two
sources — or a source and the node tree — describe the same moment.

### 7.2 Source rules

- **Exposure is declared per view family**: agent exposure and record exposure are
  independent opt-ins; a source may be recordable but not agent-visible, or vice versa.
- **Causation:** every revision-bound publication carries its causation (a
  `RequestId`, or an external-source hint). A publication caused outside the active
  controlled work that lands during a recorded interaction's effect window participates
  in contamination (E5, [guarantees.md](guarantees.md) §5.5).
- **Blob-reuse invalidation:** source publications count as relevant mutations for E3
  checkpoint reuse ([guarantees.md](guarantees.md) §5.3).
- **Registration pinning:** the source contract table is pinned in E1 and immutable
  while a recording is active ([guarantees.md](guarantees.md) §5.1).
- **Redaction and domains:** source documents follow the same
  redaction-at-production and security-domain namespacing rules as node
  materializations (§5, [semantic-model.md](semantic-model.md) §7).
- **Replay:** the replay environment factory MUST wire source fixtures — initial
  documents and reset — per the case's fixture contract
  ([verification.md](verification.md) §5.3,
  [adapter-conformance.md](adapter-conformance.md) §1); source and predicate contracts
  join the artifact pre-scan allowlist ([recording-replay.md](recording-replay.md) §7).

## 8. State timeline

Because recording evidence and timeline diagnostics reference `StateStore`
materializations, a bounded, queryable state history falls out of the same machinery:
retained snapshots/deltas indexed by `(SourceRevision, LogicalOrder)`, surfaced through
a read-only inspection tool ([protocol-topology.md](protocol-topology.md) §7). The
timeline inherits the redaction and domain rules of §5 unchanged; retention is bounded
by the `StateStore` budget ([security-resources.md](security-resources.md) §5). The
timeline is a diagnostic surface; it carries no replay authority.
