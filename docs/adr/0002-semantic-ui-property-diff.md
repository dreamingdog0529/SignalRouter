# ADR 0002: Semantic-UI property-level state diff

> **Status:** Accepted
> **Date:** 2026-07-20
> **Deciders:** SignalRouter maintainers
> **Builds on:** [ADR 0001](0001-canonical-state-hashing.md) (canonical hashing)

## Context

[ADR 0001](0001-canonical-state-hashing.md) established hash-level state observation: a
changed probe reports its before/after SHA-256 hashes. design.md §14 promises the MVP state
diff also carries "property-level changes for semantic UI descriptors", which PR #8 deferred
— a changed probe carried an empty change set.

A hash tells a caller *that* the semantic UI changed, not *what* changed. Agents and replay
diagnostics want the concrete field-level delta ("`menu.start` became disabled") without
re-fetching and diffing the whole tree themselves. The result model already carries the
target type — `StateProbeDiff.Changes` is an `EquatableList<StatePropertyChange>`, and
`StatePropertyChange` is `(Path, Before, After)` with `Before`/`After` typed as
`InteractionValue` — but nothing populated it.

Two shapes were considered:

- **Generic JSON diff / JSON Patch** over the canonical snapshot bytes. Uniform across all
  probes, but design.md §14 defers generic JSON Patch "until a stable canonicalization and
  size policy has been proven", and a generic path/value form does not map onto the typed
  `InteractionValue` before/after the model already exposes.
- **A descriptor-aware diff owned by the probe.** The `semantic-ui` probe knows its own
  snapshot schema, so it can emit typed, human-readable field changes directly.

## Decision

Adopt a **descriptor-aware property diff, provided by the probe itself**.

1. **Capability interface.** A probe may implement `IStatePropertyDiffProvider`, whose
   `DiffProperties(before, after)` explains a hash change as `StatePropertyChange`s over its
   own snapshot schema. `StateProbeReading.Diff` calls it only for a probe whose hash
   changed; a probe without the interface reports the hash change with an empty change set,
   exactly as before. This keeps the registry infrastructure free of any probe-ID special
   casing — a probe owns its schema, so a probe owns its diff.
2. **Semantic-UI scope: matched-target scalar fields.** `SemanticUiStateProbe` enumerates a
   change for each scalar descriptor field — `role`, `label`, `parentId`, `visible`,
   `enabled`, `value` — of a target present in **both** snapshots (matched by ordinal `id`).
   Each change's `Before`/`After` is the field reconstructed as an `InteractionValue`
   (`parentId`/absent `value` → `InteractionValue.Null`; a numeric descriptor value round-
   trips through its normalized invariant string).
3. **Path form.** `targets[<id>].<field>` (e.g. `targets[menu.start].enabled`). This
   satisfies the identifier constraint on `StatePropertyChange.Path` (non-empty, no edge
   whitespace, no control characters) and is unique per `(id, field)`. `StateProbeDiff` sorts
   changes by path.
4. **Deferred: structural and nested changes.** Target **additions/removals** and
   **`availableInteractions`/argument-schema** changes still change the hash but are **not**
   enumerated. The 3-field `StatePropertyChange` (both sides a present, differing scalar
   `InteractionValue`) cannot express presence ("target added") or nested structure without a
   model extension; those are left to a follow-up. A probe may therefore report a hash change
   with an empty or partial `Changes` list — the hash stays authoritative.
5. **Failure model.** A diff provider that emits an invalid change (e.g. equal before/after,
   or a value it cannot parse from its own snapshot) is a runtime invariant violation
   (fail-fast, `InteractionInvariantViolationException`), not an application-stage fault —
   consistent with ADR 0001 rule 5 for capture.

## Consequences

- **Positive.** Callers get a typed, human-readable field delta for the most common case
  (an existing control changing state) with no extra round trip. The capability interface
  keeps the registry generic and lets application probes opt in to their own property diffs.
  The change is additive: `StatePropertyChange`/`StateProbeDiff` already existed, and
  `InteractionResult` validation checks only hashes, not `Changes`.
- **Neutral — hash unaffected.** Property changes are an overlay on top of the ADR 0001
  hash; they never feed into it. Number normalization (`1.0` == `1.00`) is shared with the
  hash via `decimal` equality, so an equivalent value yields neither a hash change nor a
  property change.
- **Negative.** The diff is intentionally incomplete: add/remove and nested interaction
  changes are visible only at hash level until the deferred model extension lands. The
  ambiguity between a JSON-`null` value and an explicit `Null`-kind value collapses to
  `InteractionValue.Null`, so a change purely between those two is not enumerated (the hash
  still records it). State-snapshot size limits (design.md §25) are not yet enforced on the
  enumerated change set.

## Implementation

- `IStatePropertyDiffProvider` — the optional capability contract.
- `SemanticUiStateProbe` — implements `DiffProperties` over its snapshot schema.
- `InteractionStateProbeRegistry` / `StateProbeReading` — retain each probe's snapshot in the
  reading and call the provider for a changed probe; fail fast on an invalid diff.
