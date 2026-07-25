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
  attributes, and capability metadata are included, how values are normalized, and which
  redaction policy applies;
- a **scope** — the subtree/filter it observes;
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
- Providers are not assumed perfect: the design does not presume every mutation yields a
  delta event. Gap detection plus resync, not provider perfection, is the correctness
  mechanism.
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
  OperationId · CausationId · RuntimeIncarnationId · EventKind · LogicalOrder
```

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

## 7. State timeline

Because recording evidence and timeline diagnostics reference `StateStore`
materializations, a bounded, queryable state history falls out of the same machinery:
retained snapshots/deltas indexed by `(SourceRevision, LogicalOrder)`, surfaced through
a read-only inspection tool ([protocol-topology.md](protocol-topology.md) §8). The
timeline inherits the redaction and domain rules of §5 unchanged; retention is bounded
by the `StateStore` budget ([security-resources.md](security-resources.md) §5). The
timeline is a diagnostic surface; it carries no replay authority.
