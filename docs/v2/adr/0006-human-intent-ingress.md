# ADR 0006 (v2): ManagedIntent / ObservedExternal and the Scoped Equivalence Axiom

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-25
> **Normative specs:** [../spec/adapter-conformance.md](../spec/adapter-conformance.md) §6 ·
> [../spec/kernel-execution.md](../spec/kernel-execution.md) §7 ·
> [../spec/semantic-model.md](../spec/semantic-model.md) §6

## Context

v1's axiom — human, agent, test, and replay input share one dispatcher — was load-bearing
but overstated. In practice the Unity adapter dispatched *after* the engine's own
notification (`onClick`), text widgets mutated draft state before the semantic commit,
and unmanaged listeners could never be fully excluded. Meanwhile the v2 kernel's serial
mutation lane raised a new temptation: route human input around the kernel to protect
latency. Review rejected both extremes: "all human input is capturable" is false;
"human input is just an external observation" destroys the equivalence property that
makes recordings faithful.

## Decision

Classify every human-caused change, per adapter and capability:

- **`ManagedIntent`** — input capturable before its application-level effect. It is
  normalized to a capability invocation and admitted through the same validator,
  executor, and observation boundary as agent input. The equivalence axiom is scoped to
  exactly this: *given the same principal authority, preconditions, and invocation, the
  post-admission semantic effect path and observation contract are
  ingress-independent.* Handlers must not branch on the identity envelope.
- **`ObservedExternal`** — effects that cannot be prevented or pre-captured (native
  side effects, unmanaged listeners, IME/draft internals, engine-autonomous mutation).
  These are traced as observations with contamination semantics — never promoted to
  replayable input.
- Human latency is protected by bounding mutation-lane occupancy (pump budgets,
  `OperationRef` escape), not by bypassing the kernel. Gating during replay/exclusive
  automation is visible, and every refused intent traces as `HumanIntentBlocked`.
- The v1 `Origin` enum is replaced by the four-field identity envelope
  (Principal / Ingress / Provenance / Causality), used for authorization and audit only.

## Alternatives considered

- **Total equivalence (v1 phrasing):** rejected — falsified by the adapters' own
  mechanics; an axiom that is quietly false breeds special cases.
- **Demote all human input to external observation:** rejected — human sessions would
  become unreplayable and human/agent behavior could legitimately diverge.
- **Kernel bypass for human input:** rejected — reintroduces multi-owner mutation, the
  exact v1 failure mode, to solve a latency problem the pump contract already bounds.

## Consequences

- The equivalence claim is finally *checkable*: the TCK verifies declared
  classifications and the no-branching rule.
- Contamination becomes explicit and diagnosable instead of a silent replay mystery.
- Adapters owe documentation of Managed vs Observed per input class — honest, and new
  work.
- Audit gains provenance resolution v1's enum could not express (e.g. human-directed
  agent actions).
