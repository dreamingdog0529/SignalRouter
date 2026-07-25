# ADR 0003 (v2): Four Stores, One Event Algebra, StateStore-First Commits

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-25
> **Normative specs:** [../spec/observation-state.md](../spec/observation-state.md) §6 ·
> [../spec/guarantees.md](../spec/guarantees.md) §5, §8 ·
> [../spec/recording-replay.md](../spec/recording-replay.md) §4

## Context

The v2 draft began as "journal-first": one always-on event journal from which recording,
recovery, and a state timeline would all fall out. Review broke that premise: always-on
diagnostics want cheap/bounded/lossy; recording wants request-durable-before-effect and
no loss; recovery wants non-evictable pendings with expiring terminals; state snapshots
want content-addressed retention. One store cannot honor four retention/durability
semantics, and one persistent schema would re-couple contracts that v1 deliberately
kept apart (wire-owned vs. recording-owned projections).

## Decision

- **Four stores, four semantics:** `KernelTrace` (always-on, bounded, lossy-permitted,
  gap-marked), `RecoveryIndex` (pending non-evictable, terminals expire, refuse-on-full),
  `RecordingSink` (non-droppable ReplayEvidence, explicit durability), `StateStore`
  (content-addressed, immutable, pinned/GC).
- **Shared vocabulary stops at the in-memory event algebra**
  (OperationId/CausationId/IncarnationId/EventKind/LogicalOrder); the four persistent
  schemas version independently.
- **RecordingSink commits from kernel authoritative transitions**, never as a
  subscriber of the lossy trace.
- **Dual-write order is StateStore-first**: blob durable + pinned → evidence appended →
  manifest reachability; crashes orphan GC-able blobs, never dangle references.
- Recording capacity policy is declared at open; recording outcomes (`Incomplete` /
  `Interrupted` / `Failed`) are separated from interaction outcomes.

## Alternatives considered

- **Single event-sourced journal:** rejected — it is an execution trace, not a source
  of truth (engine state cannot be rebuilt from it), and it cannot satisfy the four
  semantics at once; "recording = journal slice" either loses durability guarantees or
  taxes every interaction with recording-grade flushes.
- **One shared persistent event schema:** rejected — trace-only additions would bump
  the recording schema; the recovery index's representation would become a public
  contract; trace diagnostics could leak into artifacts.
- **Recording as trace subscriber:** rejected — lossy upstream silently poisons a
  no-loss contract.

## Consequences

- Recorder attach/detach and v1's maintenance-lease machinery disappear structurally
  (recording is fences + sinks, not runtime surgery), without pretending recovery or
  timelines come "for free".
- Each store's bounds and failure answers are independently specifiable — the failure
  matrix in guarantees.md becomes writable at all.
- The state timeline is a cheap projection of `StateStore` retention, with no replay
  authority.
- Cost: four schemas to version and a pin/GC protocol to implement correctly.
