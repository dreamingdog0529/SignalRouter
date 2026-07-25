# ADR 0007 (v2): BCL-Only Core, Independent Codec Leaves, Logical ≠ Distribution Packaging

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-25
> **Normative reference:** [../architecture.md](../architecture.md) §2 ·
> [../spec/adapter-conformance.md](../spec/adapter-conformance.md) §5

## Context

v1's .NET-first libraries were held hostage by engine constraints: System.Text.Json
pinned to Unity's bundled 8.0.x across the whole solution, C# 9 caps, fragile
local-feed eviction rituals, and warnings against transitive pinning. Contract helpers
were duplicated (`InteractionContract`/`ProtocolContract`) because Core kept its copy
internal. The v2 draft proposed one `Format` package to quarantine serialization;
review split it further: canonical-state encoding, recording schema, and wire encoding
change for different reasons and must version independently.

## Decision

- **BCL-only core:** `Contracts`, `Kernel`, `ProtocolSession`, and `AdapterSdk` depend
  on the BCL alone. No serializer types appear in their surfaces. The owner-fixed
  zero-dependency axiom retires the third-party bus entirely.
- **Three codec leaves**, independently versioned, own the only serializer
  dependencies: `Codec.CanonicalState` (materialization + ContentId production),
  `Codec.Recording` (artifact schema), `Codec.Protocol` (wire envelope).
- **Engine constraints quarantine in the adapter package:** language level, bundled
  library pins, restore mechanics live in `Adapter.Unity` and never propagate upward.
- **Logical modules ≠ distribution packages:** distribution bundles are chosen by
  adapter restore burden (fewer packages for Unity, if that's what restore reliability
  needs) without moving logical boundaries.
- **Primitive invariants may be shared** (identifier grammar, bounded-string rules) via
  a single source; *projections stay independent* — wire-owned, recording-owned, and
  trace-owned representations of the same event never merge into one schema
  ([ADR 0003](0003-store-separation-and-commit-order.md)).

## Alternatives considered

- **One `Format` package for all serialization:** rejected — recording, wire, and
  canonical-state contracts have different change drivers; one package couples their
  release cadence and re-creates v1's cross-contract drag.
- **Full unification of contract helpers:** rejected — sharing grammar primitives is
  safe; sharing outcome projections re-couples wire and recording compatibility, which
  v1 deliberately separated.
- **Serializer abstraction inside the core (pluggable `ISerializer`):** rejected —
  an abstraction over serializers leaks the hardest constraints (buffering, depth
  budgets, ownership) and the core does not need to serialize at all.
- **One package per logical module:** rejected — v1 showed restore/eviction friction
  scales with package count on the engine side; packaging follows distribution need.

## Consequences

- An engine's bundled-library version can never again dictate the core's dependency
  graph; a Unity upgrade touches one leaf.
- Codec evolution (e.g. a new canonical representation) is a leaf major, not a
  core event.
- The reference (in-process) adapter + BCL-only core run the full kernel/TCK tiers in
  plain CI with no engine present.
- Cost: three codec release lines to maintain, and packaging decisions carry a
  documented rationale instead of a default.
