# ADR 0003: Structural (add/remove) property-level state diff

> **Status:** Accepted
> **Date:** 2026-07-20
> **Deciders:** SignalRouter maintainers
> **Builds on:** [ADR 0002](0002-semantic-ui-property-diff.md) (semantic-UI property diff)

## Context

[ADR 0002](0002-semantic-ui-property-diff.md) implemented a descriptor-aware property diff for
the `semantic-ui` probe, but only for **matched-target scalar fields** — a target present in
**both** snapshots. It explicitly deferred **target additions and removals** (item 4): a target
that appears or disappears changes the hash but carried no `StatePropertyChange`.

The blocker named there was the model. `StatePropertyChange` was `(Path, Before, After)` with
both `Before` and `After` a present, differing `InteractionValue`, so it could not express
presence — there is no "before" value for an added target, and no "after" value for a removed
one. Enumerating add/remove needs the model to represent an absent side.

An added or removed target is diagnostically as important as a mutated one ("a `menu.options`
button appeared"). Leaving it hash-only forces a caller to re-fetch and diff the whole tree,
which is exactly what the property diff exists to avoid.

## Decision

Extend `StatePropertyChange` to express presence, and enumerate target add/remove per field.

1. **Nullable absent side.** `StatePropertyChange.Before` and `.After` become nullable
   (`InteractionValue?`). At least one side must be present; when both are present they must
   still differ (ADR 0002's invariant). A derived `Kind` (`StatePropertyChangeKind` —
   `Modified`, `Added`, `Removed`) records which case a change is: `Before` absent → `Added`,
   `After` absent → `Removed`, both present → `Modified`. The constructor signature is
   unchanged for existing callers passing two present values (they get `Modified`).

2. **Per-field presence, not a single marker.** A target present on only one side is
   enumerated as one change **per scalar field** (`role`, `label`, `parentId`, `visible`,
   `enabled`, `value`), reusing the ADR 0002 path form `targets[<id>].<field>`. An addition
   sets the present value on `After` and leaves `Before` null; a removal is the mirror. This
   keeps the path convention and the field readers uniform with the matched-target case — an
   added target is just its fields going from absent to present — and preserves the full
   descriptor value rather than collapsing it to a presence flag.

3. **Absent vs explicit null are distinct.** A C#-null side (field absent) is distinct from
   `InteractionValue.Null` (field present with a null value). An added target whose `value` or
   `parentId` is null still emits a change (`Before` null, `After` `InteractionValue.Null`) —
   the field went from absent to present-null.

4. **Still deferred: nested changes.** Nested `availableInteractions`/argument-schema changes
   remain hash-only. They need a nested path convention and an interaction/argument matching
   rule, which is out of scope here and left to a further follow-up (ADR 0002 item 4).

   > **Update (ADR 0004):** resolved — [ADR 0004](0004-nested-interaction-property-diff.md)
   > adds the nested path convention (interactions keyed by `(wireName, version)`, arguments
   > by `name`) and enumerates interaction/argument additions, removals, field changes, and
   > (membership-preserving) reordering. No semantic-UI change remains hash-only.

## Consequences

- **Positive.** A target appearing or disappearing is now a typed, per-field delta with no
  extra round trip, closing the largest gap ADR 0002 left. The change is additive and
  backward compatible: existing two-value construction still yields a `Modified` change, and
  `InteractionResult` validation checks only hashes, not `Changes`.
- **Neutral — hash unaffected.** Presence changes are an overlay on the ADR 0001 hash, never
  fed into it. `Kind` is derived from the two sides, so record value-equality is unchanged.
- **Negative.** An added or removed target emits one change per field (six today), so the
  `Changes` list is larger than a single presence marker would be; state-snapshot size limits
  (design.md §25) are still not enforced on the change set. Nested interaction changes remain
  invisible at property level until a later ADR.

## Implementation

- `StatePropertyChange` — `Before`/`After` nullable, plus a derived `StatePropertyChangeKind
  Kind`; rejects both-sides-absent and both-sides-present-equal.
- `SemanticUiStateProbe.DiffProperties` — indexes both snapshots by id, emits `Modified`
  changes for matched targets (unchanged), `Removed` per-field changes for a target only in
  the before snapshot, and `Added` per-field changes for a target only in the after snapshot.
- `InteractionStateProbeRegistry` / `StateProbeReading` — carry the nullable-side changes
  through the existing guarded diff path unchanged.
