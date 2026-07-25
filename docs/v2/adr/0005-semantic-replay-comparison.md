# ADR 0005 (v2): Typed Semantic Comparison under Pinned Profiles

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-25
> **Normative specs:** [../spec/recording-replay.md](../spec/recording-replay.md) §5 ·
> [../spec/guarantees.md](../spec/guarantees.md) §3.3, §5, §9

## Context

v1 verified replay through canonical-JSON SHA-256 state hashes: admirably strict, but
opaque (a mismatch says nothing about *what* diverged), brittle across representation
changes, and coupled to one canonicalization forever. The v2 draft's first correction —
"compare node/capability/value sets semantically" — risked the opposite failure:
under-specified fuzzy matching that silently weakens the guarantee.

## Decision

Promote comparison, don't relax it:

- every artifact pins a versioned **`ReplayComparisonProfile`** in its header: view
  contract, redaction policy, node matching rules, compared field set, collection
  rules, normalization, completeness requirements, unknown-extension policy, migration
  rules;
- comparison is **typed exact equality** over that profile; results are three-valued —
  `Equal | Diverged | Incomparable(reason)` — and unevaluable never masquerades as
  diverged;
- hashes are demoted to the `ContentId` role (integrity, dedup, fast equality path);
  `ContentId` inequality routes to the typed comparator, never directly to `Diverged`;
- three modes: `StrictSemantic` (default), `ExactArtifact` (ContentId equality for
  same-build/encoder checks), `AdaptiveGoal` (locator fallback and tolerances — never
  labeled strict);
- comparison covers all evidence cuts (E1–E8 since
  [ADR 0008](0008-mcp-verification-surface.md)), so zero-mutation and wait-only
  recordings and the final reached state are verified;
- intermediate delta sequences are never compared (timing tier is a non-goal);
- the verified claim is named **observational equivalence relative to the profile** —
  never "application equivalence".

## Alternatives considered

- **Keep hash-only equality (v1):** rejected — undiagnosable divergences, permanent
  canonicalization lock-in, and `Incomparable` cannot exist (everything unevaluable
  becomes a false `Diverged`).
- **Loose semantic matching (subset fields, tolerance by default):** rejected — a
  regression tool that shrugs is worse than one that over-fires; tolerances belong in
  the explicitly non-strict `AdaptiveGoal` mode.
- **Compare only before/after cuts (E3/E4):** rejected — leaves artifact boundaries
  and predicates unverified.

## Consequences

- Divergence reports become field-level and actionable.
- Profiles make comparison rules evolvable (new profile version) without corrupting
  old artifacts' meaning; incompatibility surfaces as `Incomparable`, honestly.
- The comparator is a substantial, testable component — complexity moved from "one
  hash function" to "one specified equality", deliberately.
- `AuthorKey` discipline becomes a user-visible requirement for strict scope
  ([ADR 0002](0002-observation-view-and-identity.md)).
