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

**Registration.** View contracts are registered through the synchronous bootstrap
registry before the runtime starts, like capability, source, and predicate contracts
([kernel-execution.md](kernel-execution.md) §4, [adr 0010](../adr/0010-effect-protocol-and-kernel-host-contract.md));
there is no runtime view registration, so a view contract can never change while a
recording is active. A view contract descriptor declares its family (`Agent` or
`Record`), its scope, its materialization bounds, and whether keyless nodes are
included — a `Record`-family contract MUST exclude keyless nodes
([semantic-model.md](semantic-model.md) §3.2). Identifiers in the `kernel-raw` family
are reserved for the kernel's internal evaluation views and MUST NOT be registered.

**Projection rules in v2.0.** A registered view projects the full comparison surface
of its family: node role, hierarchy (parent links), attributes, capability
declarations (contract reference and current availability), and the state sources the
family's exposure opt-ins admit (§7.2) — the set strict replay comparison consumes
([recording-replay.md](recording-replay.md) §5.2). Values are normalized by a fixed
canonical ordering (ordinal by key or name; never locale-sensitive). The
`ViewContractId@version` identifies exactly this descriptor-plus-normalization rule
set; introducing richer projection rules is a new view contract version, never a
reinterpretation of an existing one.

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
- **Regions.** A completeness region is a `FieldPath` prefix, matched segment-wise
  (`nodes/save` covers `nodes/save/attributes/label`, never `nodes/save2`). A
  `CompletenessMap` is a bounded, ordinally sorted set of `(regionPrefix, reason)`
  entries in which the prefix is a unique key; the longest matching prefix answers a
  path; regions without an entry are complete; the empty map means the materialization
  is complete. When the entry bound would be exceeded, the overflowing entries coalesce
  into a single root-region `BudgetTruncated` entry — a completeness entry is itself
  never silently dropped ([guarantees.md](guarantees.md) §8).
- **One mapping to `Unevaluable`.** The reason vocabulary here and the
  `Unevaluable(reason)` vocabulary of [guarantees.md](guarantees.md) §3.5 are one
  deliberate mirror; guarantees.md §3.5 is the single normative mapping (including the
  `Virtualized`/`BudgetTruncated` → `Incompleteness` collapse, which loses nothing
  because the originating reason stays in the `CompletenessMap`). Implementations MUST
  NOT maintain a second mapping table.

## 4. Delivery: snapshot + delta + resync

- **Pull is the default** for agents: an on-demand snapshot at the current revision.
  Paginated reads MUST pin one `ContentId`-identified snapshot so all pages describe one
  revision.
- **Delta subscription** is an internal mechanism (and MAY be exposed to advanced
  clients in a later minor): deltas carry `ViewSequence`, the
  `baseContentId → resultContentId` transition, and a deterministic patch payload from
  which the result is reconstructible; they are gap-detectable. A subscriber observing
  a `ViewSequence` gap MUST resynchronize from an authoritative snapshot; deltas are
  never trusted across a gap. **Staging
  ([adr 0011](../adr/0011-observation-materialization-and-state-store.md)):** v2.0
  ships the snapshot path and a checkpoint-only timeline (§8); delta production lands
  with the recording module, where the deterministic patch encoding and the chain
  bounds of [recording-replay.md](recording-replay.md) §4 have their first consumer. A
  chain entry without a reconstructible payload is not a delta and MUST NOT be labeled
  one. Wait evaluation is not a subscriber: it satisfies this section by revision-gated
  re-evaluation against pinned kernel reads ([verification.md](verification.md) §2.1).
- A materialization is **revision-consistent**: every snapshot and delta is produced
  from a single `SourceRevision` via a revision-pinned read; work spanning multiple
  pumps either retains the pin or restarts. A snapshot MUST NOT mix revisions.
- Every observable mutation MUST advance `SourceRevision` — an adapter conformance
  obligation verified by the TCK ([adapter-conformance.md](adapter-conformance.md) §1,
  §7). Delta *delivery* is still not assumed perfect: each delta carries the resulting
  `SourceRevision` watermark, and watermark heartbeats are pump-boundary observations —
  the kernel owns no timer ([kernel-execution.md](kernel-execution.md) §6), so in any
  pump where a feed retained no entry it observes the current `SourceRevision`; an
  advance without a contiguous entry is a gap, and the next successful materialization
  is a resynchronizing checkpoint that carries the gap mark. Sub-pump heartbeat
  precision is not promised. Recording evidence never depends on subscription
  liveness: the E3/E4 cuts are fresh, revision-stamped materializations
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

### 5.1 The in-memory core and the durability boundary

The runtime-owned `StateStore` core is an in-memory, content-addressed **cache**
([architecture.md](../architecture.md) §2,
[adr 0011](../adr/0011-observation-materialization-and-state-store.md)):

- **Canonical encoding and `ContentId` production are injected** from the
  `Codec.CanonicalState` leaf ([adr 0007](../adr/0007-codec-and-package-boundaries.md));
  the seam yields the `ContentId`, the canonical payload bytes, and the exact encoded
  length in one answer, so downstream consumers can write and account for the blob
  without re-encoding. A runtime configured without the codec serves views, pinned
  snapshots, and predicate evaluation normally, but retains no blobs, produces no
  timeline, and cannot support recording — honest degradation, never placeholder
  identifiers. Cross-domain concealment is enforced by the store's domain-keyed lookup
  and by release-surface authorization; whether the codec additionally folds a keyed
  secret into digest production is a `Codec.CanonicalState` decision (adr 0011, open
  point).
- **Pins are reference counts** keyed by `(blob, owning OperationId)`; releasing an
  owner releases all of its pins. Unpinned blobs are evicted oldest-insertion-first
  when a `Put` would exceed the store budget; pinned blobs are never evicted.
- **Refusal is structured, never silent**: a `Put` that cannot fit answers with the
  reason (blob over its own bound, or the store budget unfit even after eviction), and
  each caller surfaces it per its lane and the failure matrix of
  [guarantees.md](guarantees.md) §7 — before E1, the recording open fails
  (`OpenFailed`); before E3, the interaction terminates
  `Faulted(EvidenceUnavailable, effectPermitted = false)`; at E4, the true terminal is
  preserved and only the recording fails; the diagnostic timeline records a gap.
- **Diagnostic retention never fails evidence**: timeline pins are released
  oldest-first before a `Put` on behalf of evidence is refused for budget reasons.
- **The in-memory pin is not the durable commit.** The StateStore-first order of
  [guarantees.md](guarantees.md) §5.1/§5.3 reads, end to end: canonical encode → cache
  retain-and-pin → durable blob write and flush (the durable coordinator, with the
  recording artifact) → evidence cut appended durably → `Ready`. The cache lease only
  guarantees the bytes the evidence will reference; durability is the recording
  module's obligation and `Ready` MUST NOT be answered before it holds.

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
| `SampledStateSource` | The document is read at materialization time (may consult external state); carries a declared freshness bound. Freshness is evaluated against the host-supplied logical clock of the pump in which materialization occurs; sub-pump precision is not promised ([kernel-execution.md](kernel-execution.md) §6) | Diagnostic only: excluded from strict comparison scope and from cross-source atomic assertions; staleness surfaces as `Stale` completeness |

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
retained entries indexed by `SourceRevision` plus a deterministic per-feed entry
sequence, with the causing `LogicalOrder` carried as optional metadata — present for
mutation-caused advances, absent for source-only publications and external mutations,
which have no admission order to cite ([adr 0009](../adr/0009-evidence-ordering-and-open-reason-vocabularies.md)).
Entries are surfaced through a read-only inspection tool
([protocol-topology.md](protocol-topology.md) §7). The timeline inherits the redaction
and domain rules of §5 unchanged — in particular, timeline entries expose record-view
`ContentId`s, so the reading surface is principal-bound and default-deny: a principal
whose domain is not the record domain receives no entries, indistinguishably from an
empty timeline. Retention is doubly bounded (entry count and retained bytes,
[security-resources.md](security-resources.md) §5); eviction releases the entry's pin.
In v2.0 the timeline retains checkpoints only (§4 staging); a chain-length rule
becomes relevant when deltas land. The timeline is a diagnostic surface; it carries no
replay authority.
