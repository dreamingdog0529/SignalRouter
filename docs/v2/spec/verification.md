# SignalRouter v2 Specification — Verification

> **Status:** v2 design draft — normative once the v2 set is accepted
> **Applies to:** SignalRouter v2 (clean-slate design)
> **Companion specs:** [guarantees.md](guarantees.md) · [semantic-model.md](semantic-model.md) ·
> [observation-state.md](observation-state.md) · [recording-replay.md](recording-replay.md) ·
> [protocol-topology.md](protocol-topology.md) · [adapter-conformance.md](adapter-conformance.md) ·
> [security-resources.md](security-resources.md)

Verification is a first-class consumer of the observation model: agents assert
expectations against pinned snapshots while driving the UI, inspect domain state beyond
the node tree, and turn a recorded session into a repeatable CI case. This spec defines
the predicate model, the assertion surface, verification cases (manifest, seal,
fixtures), the runner ownership, and the report taxonomy. The key words MUST, MUST NOT,
SHOULD, and MAY follow RFC 2119.

## 1. The two axes

Everything in this spec keeps two orthogonal questions separate
([guarantees.md](guarantees.md) §9):

- **Replay fidelity** — did the replayed run observe what the recording observed?
  Answered per comparison as `Equal | Diverged | Incomparable(reason)`.
- **Case verdict** — did the scenario meet its expectations? Answered per case as
  `Passed | FailedAssertion | Unevaluable | InfrastructureFailed` (§6).

A required assertion that evaluated `False` at record time and `False` at replay is
perfect fidelity and a failed case. No tool, report, or schema may merge the axes.

## 2. Predicate model

### 2.1 What is shared

One versioned **`PredicateContract`** model — AST shape, type checking, and the pure
evaluator — serves three consumers. Only the machinery is shared; the lifecycles are
deliberately distinct and unchanged:

| Consumer | Lifecycle | Evidence |
|---|---|---|
| Explicit wait (`wait_for`) | armed → resolved | E6 pair ([guarantees.md](guarantees.md) §5.6) |
| Capability-contract postcondition | evaluated during `Observing`; determines the terminal | embedded in E4 ([guarantees.md](guarantees.md) §5.4) |
| Standalone assertion | atomic evaluation | single E8 cut ([guarantees.md](guarantees.md) §5.10) |

### 2.2 Predicate contracts

A predicate is a **declarative, snapshot-local, pure** expression over one observation
materialization (node scope and/or state-source documents):

- The AST admits only allowlisted operators: existence, typed equality/inequality and
  ordering comparisons, string prefix/suffix/containment, counting over keyed
  collections, boolean composition. No iteration constructs, no arithmetic beyond
  comparison, no cross-snapshot references, no time.
- Structural bounds are enforced at validation: AST depth, node count, operand sizes
  ([security-resources.md](security-resources.md) §5). No code ever crosses the wire —
  the AST is data, extending the v1 posture.
- Clauses carry **stable clause IDs** so evaluations, evidence, and explanations refer
  to clauses positionally-independently.
- **Custom predicates** are application-registered `PredicateContract`s (reverse-DNS
  namespaced, versioned) with contractual obligations: input is the immutable
  materialization only; no clock, randomness, network, filesystem, or live service
  access; bounded execution; TCK-verified ([adapter-conformance.md](adapter-conformance.md) §7).
  Agents can only reference registered predicates and the standard library — never
  submit code.
- Secret operands use the E2 secret-reference mechanism: the reference is recorded,
  resolution happens in memory, and no explanation or witness ever contains the value
  ([security-resources.md](security-resources.md) §3).

### 2.3 Evaluation answers

Every evaluation answers exactly one of:

| Outcome | Meaning |
|---|---|
| `Satisfied` | The predicate evaluated true against the materialization |
| `False` | It evaluated false |
| `Unevaluable(reason)` | It could not be evaluated: referenced field `Redacted` or `OutOfScope`, region incomplete, contract version unsupported, source `SourceUnavailable`/`Stale` |

**No boolean oracle:** a comparison against a value the caller is not entitled to read
is `Unevaluable(Redacted)` or an authorization rejection — never `False`
([security-resources.md](security-resources.md) §4). `Incomparable` does not exist on
the live side; it is the replay-side mapping of `Unevaluable`
([guarantees.md](guarantees.md) §3.3).

## 3. Assertions

### 3.1 Semantics

An assertion is an atomic, read-only evaluation of one predicate against one pinned
materialization. Assertions state **expected truth**: negative expectations are written
as predicates that evaluate true (`count == 0`, `not exists`, `!=`). Tools never invert
outcomes.

### 3.2 Snapshot and causal binding

The caller chooses the evaluation basis:

- `current` — a fresh pinned snapshot at evaluation time;
- `snapshotRef` — an already-pinned snapshot the caller obtained (same-domain only);
- `afterRequestId` — the after-materialization of that request's terminal (E4), the
  correct basis for "did my invocation produce this state": it excludes mutations that
  slip between the invocation's completion and a later `current` read.

An **assertion batch** evaluates multiple predicates against one pinned materialization
atomically; individual sequential tool calls give no same-revision guarantee and MUST
NOT be advertised as equivalent.

### 3.3 Results and evidence

The tool answer carries the outcome (§2.3), per-clause expected/actual with clause IDs,
bounded redacted witness paths, and the snapshot identification tuple
(incarnation, revision, view contract, source table version, scope, `ContentId`,
completeness). Per-clause structured explanations are diagnostic, not comparison
material. While a recording is active, each evaluation is committed as one E8 cut
([guarantees.md](guarantees.md) §5.10).

Agent-domain answers never expose record-domain `ContentId`s
([observation-state.md](observation-state.md) §5).

### 3.4 Caller expectations on invoke

A caller-supplied expectation attached to an invocation does **not** alter the
interaction terminal: a capability that completed per its completion profile is
`Succeeded` even if the caller's expectation failed. The combined tool answer is:

```text
{ interaction: Terminal(...), verification: Passed | Failed | Unevaluable, assertionEvidenceRef }
```

This is gateway sugar for `invoke` followed by an assertion causally anchored to
`afterRequestId` (§3.2) — nothing more. Only a postcondition bound into the versioned
**capability contract** participates in completion semantics; its failure terminates
the interaction `Faulted(CompletionPostconditionNotSatisfied)` with a stable detail
(`False | TimedOut | EvaluationUnavailable`) in E4
([guarantees.md](guarantees.md) §5.4, [semantic-model.md](semantic-model.md) §2.2).

## 4. Discovery and pre-validation

Verification is only easy if expectations can be authored without trial and error:

- A **catalog** surface enumerates, per security domain: visible state sources with
  their contract IDs/versions and field schemas (stable paths, types, collection keys),
  node attribute vocabulary per role, available standard operators, and registered
  custom predicates with versions.
- `validate_predicate` type-checks a predicate against the catalog **without
  evaluating** it, returning per-clause errors (unknown field, type mismatch,
  unsupported operator, bound violations). Validation is free of observation cost and
  side effects.

Tool projections are listed in [protocol-topology.md](protocol-topology.md) §7.

## 5. Verification cases

### 5.1 Case = manifest + artifact

A verification case is a version-controlled **`VerificationCaseManifest`** referencing
an immutable recording artifact — never a mutated artifact:

- manifest fields: case name, description, tags; the artifact's content digest
  (tamper-evident binding); the fixture/environment contract (§5.3); the required
  assertion set (by clause/evidence reference); the expected comparison profile;
  runner requirements (adapter, engine version range);
- the artifact itself carries no editable metadata — renaming or retagging a case
  touches only the manifest, so artifact closure ([guarantees.md](guarantees.md) §5.9)
  is never re-opened.

### 5.2 Seal conditions

An artifact may be sealed into a case only if **all** hold:

1. artifact outcome `Completed` with reader-verified closure;
2. self-contained (or its external `StateStore` dependency resolved and frozen);
3. strict-eligible: every compared node keyed, no contamination intervals, no
   `OutcomeUnknown` shapes, no temporal predicates;
4. every **required** assertion in the artifact evaluated `Satisfied`;
5. contract preflight succeeds: every capability, state-source, and predicate contract
   pinned in E1 is available at compatible versions.

An artifact that fails sealing remains a **diagnostic recording** — storable,
replayable for investigation, but never a CI case. Sealing failures report which
condition failed.

### 5.3 Deterministic setup

A recording's base snapshot describes the initial state; it does not reproduce it. The
manifest therefore names a **fixture contract**: the application-defined setup/reset
the replay environment factory must perform (seeded stores, scene/screen selection,
revision-bound source initial documents) before the base comparison runs. The factory
obligation is part of the Adapter SDK
([adapter-conformance.md](adapter-conformance.md) §1); a case whose fixture contract
the host cannot satisfy answers `InfrastructureFailed(FixtureUnavailable)`, never a
divergence.

## 6. Runner and reports

### 6.1 Ownership

The runner splits along the existing authority boundaries — the gateway gains no
authority ([protocol-topology.md](protocol-topology.md) §2,
[adr 0004](../adr/0004-external-state-minimal-gateway.md)):

| Component | Owns |
|---|---|
| `Verification.Cli` (logical module, separate from `Gateway.Mcp`) | case selection, host launch, aggregation, report emission, exit codes |
| `VerificationHost` (application/adapter-owned) | headless/batch engine lifecycle, pump driving, frame phases, fixture/reset execution |
| Runtime / replayer | artifact pre-scan, isolated replay runtime, comparison, first-divergence report, per-case terminal |
| Gateway | MCP projection only; MAY ship in the same executable as a thin composition root |

CI runs need no MCP client: `Verification.Cli` drives the host and runtime directly.
It reuses the tier-3 conformance **infrastructure** — engine launcher, pump
integration, report plumbing — but an application's verification suite is not adapter
conformance and MUST NOT be labeled as such
([adapter-conformance.md](adapter-conformance.md) §7.3).

### 6.2 Report taxonomy

Three versioned schemas, none collapsed into another:

- **Case verdict** — `Passed` (fidelity all `Equal`, required assertions `Satisfied`),
  `FailedAssertion` (a required assertion is not `Satisfied`, regardless of fidelity),
  `Unevaluable` (a required comparison or assertion answered
  `Incomparable`/`Unevaluable`), `InfrastructureFailed(reason)` (host launch, fixture,
  artifact integrity, timeout). Each verdict carries the first failing evidence
  reference and, for divergences, the typed semantic diff.
- **Batch outcome** — aggregates verdicts and expresses batch-level conditions a
  verdict cannot: empty selection, invalid manifest, engine unavailable, aborted run.
- **Exit codes** — a stable, documented mapping from batch outcome for CI: success /
  test failure / unevaluable / infrastructure error are distinct codes; CI must be able
  to distinguish "the app regressed" from "the harness broke" without parsing text.

Reports are machine-readable, versioned, and carry only recording-safe fields.
