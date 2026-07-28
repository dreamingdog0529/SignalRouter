# ADR 0008 (v2): MCP Verification Surface — Assertions, State Sources, Sealed Cases

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-25
> **Normative specs:** [../spec/verification.md](../spec/verification.md) ·
> [../spec/observation-state.md](../spec/observation-state.md) §7 ·
> [../spec/guarantees.md](../spec/guarantees.md) §5.10

## Context

The owner asked v2 to make **verification through MCP-driven operations easy**: agents
should assert expectations server-side while driving the UI, inspect domain state
beyond the node tree (the v1 state-probe concern), and turn recorded sessions into
named CI cases. The v2 set as first written had no domain-state observation source, no
assertion primitive, and no case/runner story. A design round exposed four traps in
the naive additions: piggybacking assertions on the E6 wait pair contradicts the close
fence and R1; letting a reproduced `False` assertion count as replay success lets
failing tests pass CI; a `gateway verify` runner would re-grow host-side authority
against ADR 0004; and probe-style read-time capture cannot give two sources — or a
source and the tree — the same moment.

## Decision

1. **State sources join the projection model** as `sources/<StateSourceKey>` scopes:
   `RevisionBoundStateSource` (immutable documents published through the kernel
   mailbox; atomic swap + revision allocation; strict-replay and assertion eligible)
   and `SampledStateSource` (read at materialization; diagnostic only). Key, contract,
   and display name are separate; agent and record exposure are independent; sources
   participate in E1 pinning, E3 invalidation, and E5 contamination.
2. **One predicate machinery, three lifecycles.** A shared `PredicateContract`
   (declarative bounded AST, pure evaluator, registered custom predicates) serves the
   existing E6 wait pair and E4 capability postconditions unchanged, plus a new
   **atomic E8 `AssertionEvaluated` cut** — no open commitment, ignored by the close
   fence, closure-free (R5).
3. **Two axes, never merged:** replay fidelity (`Equal | Diverged | Incomparable`)
   versus case verdict
   (`Passed | Diverged | FailedAssertion | Unevaluable | InfrastructureFailed`,
   classified from the run's first non-passing event).
   Assertions state expected truth; live `Unevaluable` maps to replay `Incomparable`;
   unreadable fields are never `False` (no boolean oracle).
4. **Caller expectations never change interaction terminals.** `invoke` with an
   expectation returns `{interaction, verification, evidenceRef}`; only
   capability-contract postconditions decide terminals
   (`Faulted(CompletionPostconditionNotSatisfied)`).
5. **Cases are manifests over immutable artifacts.** A version-controlled
   `VerificationCaseManifest` binds an artifact digest, fixture/environment contract,
   and required assertions; sealing is condition-gated (Completed, closure-verified,
   self-contained, strict-eligible, required assertions `Satisfied`, contract
   preflight).
6. **The runner is not the gateway.** `Verification.Cli` (case selection, launch,
   aggregation, exit codes) + adapter-owned `VerificationHost` (headless engine,
   fixtures) + runtime/replayer (pre-scan, comparison, case terminals); the gateway
   stays projection-only.

## Alternatives considered

- **Assertions as E6 pairs (armed==resolved):** rejected — E6 is wait-specific: the
  close fence cancels open arms, R1 demands pair matching, and the two-cut shape does
  not fit an atomic evaluation.
- **`False → False` counts as passing replay:** rejected — a faithfully reproduced
  failing assertion would pass CI; fidelity and verdict must be separate axes.
- **`Faulted(PostconditionFailed)` for caller expectations:** rejected — it conflates
  a contract-compliant effect with a caller's unmet hope and makes one invocation's
  terminal depend on who called it.
- **Gateway-owned verify runner:** rejected — recording/replay state is runtime-owned
  (ADR 0004); the gateway would re-grow a second authority.
- **Probe-style read-time capture for strict sources (v1 shape):** rejected — serial
  `Capture()` calls cannot guarantee cross-source point-in-time consistency; only
  publication through the kernel's linearization point can.
- **Client-side verification (agent pulls trees and diffs locally):** rejected as the
  primary path — no evidence trail, no replayable checkpoints, unbounded payloads, and
  every agent reimplements comparison badly.

## Consequences

- An agent's E2E session becomes a sealed, replayable CI case with its assertions
  re-checked mechanically; CI distinguishes "app regressed" from "harness broke" by
  exit code.
- Domain state joins the same observation, redaction, comparison, and evidence
  machinery as the UI tree — no parallel probe subsystem.
- The recording schema gains one cut (E8) and two pinned contract tables; artifact
  readers grow correspondingly.
- Applications carry new obligations for strict-scope domain state: revision-bound
  publication and fixture/reset contracts. Sampled sources remain a low-effort
  diagnostic escape hatch.
- The predicate AST, catalog, and validator are a new specified component — bounded,
  but real work.
