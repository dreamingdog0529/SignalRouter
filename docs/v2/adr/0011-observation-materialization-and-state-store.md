# ADR 0011 (v2): Observation Materialization, the StateStore Core, and Delta Staging

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-26
> **Normative reference:** [../spec/observation-state.md](../spec/observation-state.md) §1, §3–§5, §8 ·
> [../spec/kernel-execution.md](../spec/kernel-execution.md) §5–§6 ·
> [../spec/guarantees.md](../spec/guarantees.md) §5.1, §5.3, §7 ·
> [../spec/security-resources.md](../spec/security-resources.md) §5.1

## Context

Implementing the observation layer surfaced decisions the spec set had left open:
who produces `ContentId`s when the BCL-only kernel may not serialize
([adr 0007](0007-codec-and-package-boundaries.md)); what the in-memory `StateStore`
core is when durable artifacts do not exist yet; what the delta subscription of
[observation-state.md](../spec/observation-state.md) §4 minimally is in v2.0 when a
delta without a reconstructible payload carries no information; how the timeline is
indexed when source-only publications advance `SourceRevision` without a
`LogicalOrder` ([adr 0009](0009-evidence-ordering-and-open-reason-vocabularies.md));
and how the evidence coordinator obtains the *exact* observation basis its cuts
promise when control turns keep advancing the revision.

## Decision

- **Canonical encoding and `ContentId` production are injected.** The kernel accepts a
  canonical-state codec seam implemented by the `Codec.CanonicalState` leaf. One call
  answers `{ContentId, canonical payload bytes, exact encoded length}` — enough for
  the recording module to write the blob durably and account for it without
  re-encoding. Without the codec the runtime serves views, pinned snapshots, and
  predicate evaluation normally, retains no blobs, produces no timeline, and cannot
  support recording: honest degradation. Whether digest production additionally folds
  a keyed secret (identifier-level cross-domain concealment on top of the store's
  domain-keyed lookup and release-surface authorization) is decided with
  `Codec.CanonicalState` (open point, item 4).
- **The `StateStore` core is an in-memory cache; durability is the recording
  module's.** Pins are reference counts keyed by `(blob, owning OperationId)`; GC
  evicts unpinned blobs oldest-insertion-first at over-budget `Put`; refusals are
  structured and mapped per caller lane onto the failure matrix; diagnostic (timeline)
  pins release before an evidence `Put` is refused for budget. The StateStore-first
  order reads end to end: canonical encode → cache retain-and-pin → durable blob
  write/flush → evidence appended durably → `Ready`. The cache lease is not the
  durable commit and `Ready` never precedes durability.
- **v2.0 observation delivery is snapshot + checkpoint-only timeline.** This amends
  the delta-subscription decision of [adr 0002](0002-observation-view-and-identity.md)
  in implementation scope only: the delta *contract* (ViewSequence contiguity,
  `base → result` chaining, reconstructible patch payload, gap ⇒ resync, bounded
  chains) stays normative and lands with the recording module, its first real
  consumer. Wait evaluation is not a subscriber — it re-evaluates revision-gated
  against pinned kernel reads. Watermark heartbeats are pump-boundary observations; a
  revision advance without a retained entry is a gap, and the next successful
  materialization is a checkpoint carrying the gap mark.
- **The timeline is indexed by `SourceRevision` plus a deterministic entry sequence;
  `LogicalOrder` is optional metadata.** Every observable mutation advances
  `SourceRevision`, so it is already a total order over retained states; source-only
  and external advances simply carry no causing order. Its reading surface is
  principal-bound and default-deny because entries expose record-view `ContentId`s.
- **The kernel guarantees the exact evidence basis.** When the codec is configured,
  the record-view materialization pinned at `Observing` is retained per interaction
  until the terminal evidence commits; coordinator-requested fresh materializations
  take an expected basis and answer mismatch explicitly (E3 re-materializes at the
  new revision; E4 uses the retained materialization). The item-2 evidence seam
  (`PermitEvidence`/`TerminalEvidence` carrying watermarks only) is unchanged.

## Consequences

- The recording module (item 5) extends the runtime surface rather than reworking it:
  authoritative hooks for E1/E5/E6/E7/E8 (never derived from the lossy trace),
  catalog snapshot/version access for E1 pinning, canonical blob export, and cut-level
  lease release are additions to the coordinator seam declared here.
- Registering a view contract is bootstrap-only, so a view contract can never change
  under an active recording; the `kernel-raw` family is reserved for the kernel's
  internal evaluation views.
- Every materialization — including internal evaluation reads — is bounded
  (`ObservationViews.MaxMaterializationNodes`); overflow is `BudgetTruncated`
  completeness and evaluates as `Unevaluable(Incompleteness)`, never a partial answer.

## Rejected alternatives

- **Kernel-side hashing / placeholder `ContentId`s** — violates the serializer-free
  core ([adr 0007](0007-codec-and-package-boundaries.md)) or fabricates identifiers
  that recorded artifacts would then have to launder; honest absence is cheaper than
  un-lying an identifier.
- **A kernel-owned durability seam (`IStateStoreDurability`) now** — without the
  codec there is no canonical byte form to persist; a durability interface with no
  bytes behind it would freeze a shape the recording module has better information to
  design.
- **Implementing delta production now (with or without a public subscription API)** —
  a "delta" whose payload cannot reconstruct the result is a checkpoint with a
  misleading label; a patch encoding chosen before its first consumer (replay
  comparison) exists would be designed blind. Checkpoint-only is honest and
  sufficient for the v2.0 timeline.
- **A copy-on-write store to retain pins across pumps** — cost without a consumer;
  the spec's "either retains the pin or restarts" permits restart-always, which the
  budget-deferral rule implements.
- **Fabricating a `LogicalOrder` for source-only revision advances** — exactly the
  anti-pattern [adr 0009](0009-evidence-ordering-and-open-reason-vocabularies.md)
  closed: an order that does not exist must not be invented for indexing convenience.
- **An unauthenticated public timeline (the `KernelTrace` precedent)** — the trace
  exposes no content identifiers; the timeline does, and record-domain `ContentId`s
  must not reach agent-domain readers ([observation-state.md](../spec/observation-state.md) §5).
