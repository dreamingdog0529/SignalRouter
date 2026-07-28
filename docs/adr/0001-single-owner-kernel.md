# ADR 0001 (v2): Single-Owner Kernel with Lanes and Mailbox

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-25
> **Normative spec:** [../spec/kernel-execution.md](../spec/kernel-execution.md)

## Context

v1 concentrated queueing, validation, sequencing, snapshotting, publishing, recording,
result construction, idempotency, and two lease mechanisms in one dispatcher
(~2,100 lines). Its hardest defects clustered around cross-thread interlocking:
`AssignIdentity` as a multi-purpose linearization point, `activeDispatches +
pendingContinuations` idle accounting, thread-static continuation markers, and
lease/drain interactions. Determinism was defended, not structural.

External review of the v2 draft sharpened an initial "single-threaded turn loop" thesis:
a loop that blocks on an awaited interaction starves cancels, queries, disconnects, and
adapter completions, while interleaving mutations destroys non-interleaving guarantees.

## Decision

The kernel is a **single-owner, mailbox-driven set of resumable state machines**:

- all input arrives as mailbox messages; adoption is the only linearization point;
- at most one mutation interaction is active; a control/observation lane is prioritized
  at every turn boundary;
- adapter effects leave and return as messages — the kernel never awaits arbitrary tasks;
- the host engine drives the kernel via `Pump(maxTurns, deadline, framePhase)`;
- long-running work releases the mutation lane through `OperationRef`s;
- determinism is specified in three tiers (serializable / replayable /
  timing-deterministic), with the third an explicit non-goal.

## Alternatives considered

- **Run-to-completion turn loop (one interaction = one turn):** rejected — starves the
  control lane during awaits, or gives up mutation non-interleaving.
- **Keep a lock-based multi-entry dispatcher (v1 shape, decomposed):** rejected — the
  complexity was inherent to multi-owner mutation of shared state, not to file size.
- **Actor framework dependency:** rejected by the zero-dependency axiom; the mailbox
  needs ~one linearization point, not a framework.

## Consequences

- Locks around interaction state disappear; ordering exists because one owner assigns it.
- Control responsiveness becomes a *contract* (adapter sync bounds + TCK enforcement)
  rather than an emergent property.
- The kernel is engine-agnostic and testable without any engine (conformance tier 1).
- Mid-effect timing remains non-deterministic by declaration, which downstream specs
  (comparison, cancellation) must and do respect.
