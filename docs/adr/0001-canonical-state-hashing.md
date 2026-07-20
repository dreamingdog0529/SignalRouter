# ADR 0001: Canonical state JSON normalization and hashing

> **Status:** Accepted
> **Date:** 2026-07-20
> **Deciders:** SignalRouter maintainers
> **Supersedes:** the "canonical state JSON normalization and hashing" open item in
> design.md §25

## Context

State observation (design.md §14) needs a deterministic fingerprint of each probe's
snapshot so that:

- strict replay (§16) can compare a recorded before/after state hash against the state
  produced by re-execution, and
- recordings (§15) can persist a compact `beforeHash`/`afterHash` instead of full snapshots.

The fingerprint MUST be identical for the same logical state across builds, machines, CLR
versions, and processes; otherwise replay diverges for reasons unrelated to application
behavior. design.md §25 left the normalization and hashing scheme unresolved, and §24/§25
require an ADR before the component is considered stable.

Two schemes were considered:

- **RFC 8785 (JSON Canonicalization Scheme, JCS).** The industry standard. Interoperable
  with external tooling, but its number canonicalization requires reproducing the
  ECMAScript `Number::toString` algorithm, which is intricate to implement correctly on
  `netstandard2.1` and introduces floating-point edge cases we do not otherwise need.
- **A constrained internal canonical form.** A JSON subset with a small, fully specified
  normalization, sidestepping floating-point ambiguity entirely.

## Decision

Adopt a **constrained internal canonical form** with SHA-256 hashing.

1. **Value subset.** A canonical snapshot contains only: object, array, string, boolean,
   integer, and null. Non-integer numbers (fractional or exponent form), numbers outside
   signed/unsigned 64-bit range, and any other construct are **rejected** during
   canonicalization rather than coerced. Probes that need fractional or high-precision
   values encode them as strings (the built-in `semantic-ui` probe encodes descriptor
   numeric values as normalized invariant-culture strings).
2. **Canonical form.** Object keys are emitted in ascending **ordinal** order; duplicate
   keys are rejected. Arrays preserve source order. Output has no insignificant whitespace
   and uses `System.Text.Json`'s default string escaping. Encoding is UTF-8.
3. **Hash.** SHA-256 over the canonical UTF-8 bytes, rendered as **lowercase hexadecimal**
   (64 characters). This satisfies the identifier constraints already enforced on
   `StateProbeObservation.Hash`.
4. **Redaction ordering.** Redaction is the probe's responsibility and happens before the
   snapshot is produced, so secret values never reach canonicalization or hashing
   (design.md §14).
5. **Failure model.** A probe that returns a null or uncanonicalizable snapshot is a
   runtime invariant violation (fail-fast), not an application-stage fault.

### Refinement to §14: queue state is not hashed

design.md §14 lists "queue state" as part of the `interaction-runtime` probe. Transient
queue depth is **excluded** from the hashed snapshot: it is timing-dependent and would make
an otherwise-reproducible interaction hash differently under replay, defeating the purpose
of the hash. The `interaction-runtime` probe hashes session epoch and registry revision.
Any queue metrics, if surfaced, belong out of band with the runtime bridge, not in a
replay-compared state hash.

## Consequences

- **Positive.** Fully deterministic across builds and machines; no floating-point
  canonicalization to get wrong; simple, self-contained, and testable; hashes drop straight
  into the existing result and recording schemas.
- **Negative.** The canonical form is **not** RFC 8785 and is not intended to interoperate
  with external JCS tooling. Probes must stay within the value subset (fractional values are
  encoded as strings). Revisiting interoperability later would be a new ADR.
- **Scope.** This ADR covers hash-level observation. Property-level semantic-UI diffs
  (`StatePropertyChange`) remain deferred (design.md §14) and do not affect the hash.

## Implementation

- `StateCanonicalizer` — canonicalization + SHA-256/hex.
- `IInteractionStateProbe` / `StateProbeSnapshot` — the probe contract.
- `InteractionStateProbeRegistry` / `StateProbeReading` — capture and diff.
- `SemanticUiStateProbe`, `InteractionRuntimeStateProbe` — built-in probes.
- `InteractionDispatcher` — before/after capture around publish (design.md §7.1 steps 5, 8).
