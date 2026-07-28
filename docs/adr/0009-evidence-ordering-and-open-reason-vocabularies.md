# ADR 0009 (v2): Evidence Stream Ordering and Open Reason Vocabularies

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-25
> **Normative reference:** [../spec/guarantees.md](../spec/guarantees.md) §3.5, §10 ·
> [../spec/recording-replay.md](../spec/recording-replay.md) §2 ·
> [../spec/semantic-model.md](../spec/semantic-model.md) §4

## Context

Implementation planning surfaced two gaps that blocked transcribing
[guarantees.md](../spec/guarantees.md) into code.

First, the evidence stream was specified as "cuts in `LogicalOrder`", but
`LogicalOrder` is the total admission order of **mutation interactions**
([semantic-model.md](../spec/semantic-model.md) §4). E1/E7 (fences), E5 (barrier),
E6 (waits), and E8 (assertions) are not mutation interactions and carry no
`LogicalOrder`; E5's contamination interval needs endpoints that can lie between
interaction cuts. The stream had no defined position for over half of its cut kinds.

Second, the reason vocabularies (`Rejected`, `Incomplete`, `Incomparable`,
`Unevaluable`) existed only as scattered inline mentions — some codes named, some
conditions described but unnamed — with no canonical table, while §10 declared every
taxonomy change breaking. An implementation could neither enumerate the codes nor know
what adding one later would mean for recorded artifacts.

## Decision

- **`EvidenceSequence` orders the stream.** Every ReplayEvidence cut carries an
  artifact-local, monotonic append position. Interaction cuts (E2/E3/E4) additionally
  carry their interaction's `LogicalOrder`, and their relative stream order is
  consistent with it. Interval endpoints (E5) are `EvidenceSequence` positions.
- **Reason vocabularies are canonical and open.** [guarantees.md](../spec/guarantees.md)
  §3.5 reserves the codes the spec set already names, one table per taxonomy, each code
  bound to exactly one condition. The vocabularies stay open: readers MUST present
  unknown codes verbatim and MUST NOT branch execution on unknown meanings. Reserving a
  new code is a minor spec revision; renaming, removing, or re-defining a reserved code
  is a breaking taxonomy change under §10.
- **Unnamed rejection causes stay unnamed.** Authorization refusal, capability
  unavailability, precondition failure, incarnation mismatch, and unkeyed-target
  refusal are deliberately not reserved yet: their names must be chosen together with
  the exposure rules of [security-resources.md](../spec/security-resources.md) §4
  (a rejection code must not become an existence oracle for hidden targets), which is
  kernel-implementation work.

## Alternatives considered

- **`LogicalOrder` alone orders the stream:** rejected — non-interaction cuts would
  need fabricated `LogicalOrder` values, forging admission-order semantics they do not
  have and corrupting the one meaning that identifier is defined to carry.
- **Wall-clock or hybrid timestamps as stream positions:** rejected — timing is
  explicitly outside the promised determinism tiers
  ([guarantees.md](../spec/guarantees.md) §4); positions must be reproducible.
- **Closed reason enums:** rejected — applications and adapters extend the fault and
  rejection surfaces (custom capabilities, engine-specific conditions); a closed enum
  turns every extension into a breaking schema event and invites lossy "other" buckets,
  violating the honest-uncertainty stance.
- **Naming the remaining rejection causes now:** rejected — the names interact with
  exposure policy (distinguishing "not found" from "not authorized" can leak the
  existence of hidden targets); deciding them without the kernel's authorization
  design would bake in a security posture by accident.

## Consequences

- Every cut kind has a well-defined stream position; readers can classify shapes,
  detect out-of-order evidence, and evaluate contamination intervals without special
  cases per cut kind.
- `Contracts` can model the identifier family and reason types now, with
  `EvidenceSequence` as an ordinary comparable identifier.
- Recorded artifacts survive vocabulary growth: a new reserved code never invalidates
  an old artifact, and readers built against an older table degrade to verbatim
  presentation instead of failure.
- Cost: two identifiers where v1's single epoch had one ordering concept, and a
  standing obligation to route any future rejection-cause naming through a
  security-exposure review.
