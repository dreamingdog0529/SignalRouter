# ADR 0004: Nested interaction/argument property-level state diff

> **Status:** Accepted
> **Date:** 2026-07-21
> **Deciders:** SignalRouter maintainers
> **Builds on:** [ADR 0003](0003-structural-property-diff.md) (structural add/remove diff)

## Context

[ADR 0002](0002-semantic-ui-property-diff.md) and [ADR 0003](0003-structural-property-diff.md)
built the `semantic-ui` property diff up to matched-target scalar fields and target
additions/removals. Both explicitly deferred **item 4**: nested `availableInteractions` /
argument-schema changes stayed hash-only — an interaction appearing on a target, or an
argument's requiredness or sensitivity changing, altered the hash but carried no
`StatePropertyChange`.

This is the last hash-only gap in the semantic-UI diff, and a diagnostically real one: "a
`click` operation appeared on `menu.start`" or "the `value` argument became sensitive" is
exactly the kind of delta the property diff exists to surface without a caller re-fetching and
re-diffing the whole tree. The snapshot already encodes the nested shape per target:

```
availableInteractions: [ { wireName, version, arguments: [ { name, type, required, sensitive } ] } ]
```

interactions sorted by `(wireName, version)`, arguments in schema order — and argument order is
itself observable state (design §6.1: it defines codec output property order) that feeds the
hash.

The blocker was never the model (ADR 0003 already made `StatePropertyChange` presence-capable);
it was the lack of a **nested path convention** and **matching rules** for interactions and
arguments.

## Decision

Enumerate nested changes under an extended path, matching interactions by identity and
arguments by name.

1. **Path grammar.** Extend `targets[<id>]…`:
   - interaction key field: `targets[<id>].availableInteractions[<wireName>@<version>].wireName`
     and `….version`
   - argument field: `targets[<id>].availableInteractions[<wireName>@<version>].arguments[<name>].<field>`,
     `<field>` ∈ `{ type, required, sensitive, ordinal }`

   The interaction segment key is `<wireName>@<version>`. This is collision-safe against
   existing scalar paths: after `targets[<id>].`, a scalar path continues with a fixed field
   name (`role`/`label`/`parentId`/`visible`/`enabled`/`value`), a nested path with the
   constant segment `availableInteractions[` — disjoint sets, so existing target-level paths
   and values are untouched.

2. **Matching rules.** Interactions are matched by `(wireName, version)` (registry-unique per
   descriptor); arguments by `name` (unique within an interaction). A matched interaction emits
   nothing for its key fields (identical by construction) and recurses into arguments. A matched
   argument emits a `Modified` change per differing field.

3. **Presence per field (added/removed), like a target.** An interaction present on only one
   side emits `wireName` and `version` as single-sided `Added`/`Removed` changes, then per-field
   presence for each of its arguments. Emitting the key fields is what keeps an added/removed
   interaction with an **empty** argument list (e.g. `click@1`) visible — otherwise it would
   collapse back to hash-only. An argument present on only one side emits `type`/`required`/
   `sensitive` on the present side.

4. **Reordering via a conditional synthetic `ordinal`.** A matched argument also carries a
   synthetic `ordinal` = its 0-based index in the (canonically ordered) `arguments` array, but
   **only when the argument-name set is identical on both sides** of that interaction. A pure
   reorder then surfaces as `Modified` `ordinal` changes; when membership changes, the
   `Added`/`Removed` changes already explain the difference and no `ordinal` is emitted, so
   insert/remove-induced index shifts do not generate noise.

   - *Rejected — unconditional absolute `ordinal` (Option A).* Faithful to the hash's absolute
     order, but every insertion/removal shifts following arguments' ordinals, emitting shift
     "noise" alongside the add/remove changes.
   - *Rejected — do not model position (Option B).* Simplest, but a pure reorder (observable,
     hash-changing) would produce an empty explanation, which is surprising for a
     debuggability-focused diff.
   - The conditional form keeps Option A's fidelity for the case it matters (same membership,
     different order) and Option B's quiet for the case it does not, at the cost of one
     name-set comparison.

5. **Escaping stance.** As with the existing target-`id` path, identifiers (`id`, `wireName`,
   `name`) are concatenated unescaped. A `wireName`/`name` containing `. [ ] @` could in
   principle collide with a delimiter, the same pre-existing exposure ADR 0002 accepted for
   `id`. Any real collision is caught by `StateProbeDiff`'s unique-`Path` enforcement, which
   fails fast (`InteractionInvariantViolationException`, ADR 0002 rule 5) — it can never cause
   silent corruption. A uniform cross-segment escaping scheme is a possible later follow-up.

## Consequences

- **Positive.** The last hash-only gap in the semantic-UI diff is closed: interaction and
  argument additions, removals, field changes, and membership-preserving reordering are all
  typed, path-addressed deltas with no extra round trip. The change is additive and reuses the
  ADR 0003 nullable-side model unchanged.
- **Reachability caveat.** Through a live single-catalog registry, a descriptor's schema must be
  order-for-order compatible with the catalog, so the only nested changes actually reachable are
  an argument `sensitive` flip and an interaction add/remove. Argument add/remove, type/required
  change, and reordering arise only in hand-crafted snapshots or cross-catalog/replay scenarios.
  `DiffProperties` is a pure snapshot→snapshot function and handles the general case for
  robustness and forward compatibility; the tests cover the unreachable cases via
  `StateProbeSnapshot.FromJson`.
- **Negative.** An added/removed interaction or argument emits several changes (its key fields
  plus each argument's fields), so the `Changes` list grows; state-snapshot size limits
  (design §25) are still not enforced on the change set. `ordinal` is synthetic — a diff-only
  construct not present in the snapshot schema — justified as diagnostic metadata explaining an
  authoritative hash, not as a reproduction of the schema.
- **Neutral — hash unaffected.** All nested changes are an overlay on the ADR 0001 hash; they
  never feed into it.

This resolves ADR 0002 item 4 / ADR 0003 item 4.

## Implementation

- `SemanticUiStateProbe.DiffProperties` — the matched-target branch now also calls
  `AddInteractionChanges`; the single-sided target branches also call `AddPresenceInteractions`.
  New helpers index interactions by `<wireName>@<version>` and arguments by `name` (with their
  array index), emit matched/added/removed interaction and argument changes, and gate `ordinal`
  on a `SameArgumentNames` membership check.
- Path/leaf construction is shared: `AddSingleSided` (single-sided leaf) and a full-path
  `AddIfChanged` overload back both the target-level and nested enumeration.
- `StatePropertyChange` / `StateProbeDiff` — unchanged; the new paths flow through the existing
  guarded diff path and its unique-`Path` fail-fast.
