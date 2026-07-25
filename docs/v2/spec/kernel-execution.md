# SignalRouter v2 Specification — Kernel Execution

> **Status:** v2 design draft — normative once the v2 set is accepted
> **Applies to:** SignalRouter v2 (clean-slate design)
> **Companion specs:** [guarantees.md](guarantees.md) · [semantic-model.md](semantic-model.md) ·
> [observation-state.md](observation-state.md) · [adapter-conformance.md](adapter-conformance.md)

The kernel is the single owner of interaction execution. This spec defines its state
machine, the mailbox, the lane model, the pump contract with the host engine,
cancellation, continuations, and the arbitration of concurrent actors. The key words
MUST, MUST NOT, SHOULD, and MAY follow RFC 2119.

## 1. Single-owner principle

Exactly one logical owner — the kernel — mutates interaction state. All inputs reach it
as **mailbox messages**; nothing else touches kernel state. Consequences:

- The kernel holds no locks around interaction state; concurrency control is the
  mailbox's single linearization point (§4).
- The kernel never awaits arbitrary tasks. An adapter invocation is issued as an outgoing
  effect request and its completion returns as an incoming mailbox message
  ([adapter-conformance.md](adapter-conformance.md) §3). The kernel remains responsive
  between the two.
- The kernel is engine-agnostic and thread-agnostic: it runs wherever the host pumps it
  (§6), typically the engine main thread.

Determinism follows structurally: admission order is a total order because one owner
assigns it (`serializable` tier, [guarantees.md](guarantees.md) §4).

## 2. Lanes

The kernel processes two lanes:

- **Mutation lane** — capability invocations that may change application state. At most
  **one** mutation interaction is active at any time; the rest queue in admission order.
- **Control/observation lane** — cancellation requests, status queries, observation
  reads, wait-predicate evaluation, standalone assertion and assertion-batch
  evaluation ([verification.md](verification.md) §3), revision-bound state-source
  publications ([observation-state.md](observation-state.md) §7), lifecycle messages
  (recording open/close fences, incarnation teardown), and adapter completions. Control messages are processed with
  priority **at each turn boundary**; the spec deliberately does not claim "always
  processable", because a synchronous adapter call can occupy the owner thread —
  responsiveness is bounded instead by the adapter execution-time contract and enforced
  by the TCK ([adapter-conformance.md](adapter-conformance.md) §4, §7).

An `OperationRef` (`OperationId`) detaches the **caller**, never the guarantee: the
mutation lane is held until the adapter reports the **effect fence** — the point after
which the effect can no longer mutate application state. Only work that outlives its
effect fence (e.g. a postcondition watch) and operations that never mutate (waits,
queries, observation) continue under an `OperationRef` on the control/observation lane.
Explicit waits (`wait_for`) always live on the control/observation lane.

## 3. Admission

A submission carries: the caller-assigned `RequestId`, the semantic fingerprint, the
capability invocation ([semantic-model.md](semantic-model.md) §2.2), and the identity
envelope (Principal/Ingress/Provenance/Causality, [semantic-model.md](semantic-model.md) §6).

At admission — a single mailbox-serialized step — the kernel:

1. Deduplicates by `(RuntimeIncarnationId, RequestId)`: a duplicate with a matching
   fingerprint returns the retained answer (idempotent submit); a duplicate with a
   different fingerprint is `Rejected(RequestIdConflict)`.
2. Authorizes the invocation against the principal and the node's exposure policy.
3. Assigns `LogicalOrder` and enqueues on the mutation lane.
4. Registers the admission in the `RecoveryIndex` (pending, non-evictable).
5. Acknowledges admission explicitly to the submitter (`accepted(requestId)`) — the
   split-phase protocol's first phase ([protocol-topology.md](protocol-topology.md) §4).
6. While a recording is active, appends E2 durably before any effect
   ([guarantees.md](guarantees.md) §5.2).

Admission refusal (`Rejected`) is always evidence-free of side effects.

## 4. Mailbox

A multiple-producer, single-consumer queue with:

- **One linearization point:** message adoption. Concurrent arrival order is not
  reproducible and is not part of any guarantee; the adopted sequence is recorded and is
  the only order that exists ([guarantees.md](guarantees.md) §4).
- **Priority classes:** control before mutation admission at each turn boundary; within a
  class, FIFO by adoption.
- **Bounded capacity** per class with declared overflow policy: control-lane overflow is
  a kernel fault (it must be sized for the worst case); mutation-lane overflow refuses
  admission (`Rejected(CapacityExhausted)`); state-source publication overflow answers
  the publisher with an explicit refusal — a partial document swap never occurs
  ([guarantees.md](guarantees.md) §7).
- **Atomic publication:** adoption of a revision-bound state-source publication swaps
  the source's immutable document and allocates the observation revision in one step;
  observers can never see a torn document or an unrevisioned swap
  ([observation-state.md](observation-state.md) §7.1).
- **Starvation rule:** the pump guarantees that if the mutation lane is idle, queued
  control messages are drained before the turn ends; if a mutation is active, control
  messages are processed at every step boundary of its state machine.

## 5. Interaction state machine

Each admitted mutation interaction is a resumable state machine owned by the kernel:

```text
Admitted → Validating → Invoking → WaitingCompletion → Observing → Terminal
```

| State | Work | Exit |
|---|---|---|
| `Validating` | Re-resolve the target by `AuthorKey`/`NodeRef`, check capability availability and preconditions; no side effects | `Rejected` (no E3) or advance |
| `Invoking` | Take the before-cut (E3 EffectPermit when recording, [guarantees.md](guarantees.md) §5.3), then issue the adapter effect request | advance |
| `WaitingCompletion` | Await the completion evidence required by the bound completion profile, as mailbox messages | evidence, fault, or cancellation |
| `Observing` | Materialize the after observation, evaluate the capability postcondition (final evaluation embeds in E4) | advance |
| `Terminal` | Commit terminal to `RecoveryIndex`, append E4 when recording, answer waiters | done |

Rules:

- Each step yields an explicit result (`Completed | Yield | Fault`); a step MUST NOT
  block the owner thread beyond the synchronous adapter execution-time contract.
- State-dependent rejection in `Validating` terminates with the `E2 + E4,
  effectPermitted=false` shape — no permit, zero effects.
- Nested submission from inside an effect is refused (`Rejected(ReentrantDispatch)`);
  follow-up work uses continuations (§8).
- Effect handlers MUST NOT branch on `LogicalOrder`, `RequestId`, or the identity
  envelope; replay cannot reproduce them.

## 6. Pump contract

The host engine drives the kernel:

```text
Pump(maxTurns, deadline, framePhase) → PumpReport
```

- The kernel processes at most `maxTurns` turns and returns by `deadline`; it never
  installs its own thread or timer.
- `framePhase` (e.g. `Update`, `LateUpdate`, or adapter-defined phases) is an input the
  kernel exposes to completion profiles that need frame fencing (`FrameCommitted`).
- Per-frame observation work is budgeted: snapshot/delta materialization respects a
  per-pump byte/node budget. Carry-over across pumps is revision-consistent: it
  continues only against a revision-pinned read of the same `SourceRevision`; if the
  pinned revision cannot be retained, materialization restarts, and truncation surfaces
  as `BudgetTruncated` completeness ([observation-state.md](observation-state.md) §4).
- `PumpReport` states work remaining, so hosts can schedule additional pumps.
- Adapters declare their synchronous execution-time bound; the TCK enforces it. A pump
  on the engine main thread therefore has a computable worst-case occupancy:
  `maxTurns × (step bound + adapter sync bound)`.

## 7. Multi-actor arbitration

Humans, agents, tests, and replay coexist. Arbitration rules:

- All **ManagedIntent** input ([adapter-conformance.md](adapter-conformance.md) §6) —
  human or otherwise — enters through admission and the same mutation lane. Human intents
  MAY be prioritized **at mailbox adoption, before `LogicalOrder` is assigned**; once
  admitted, execution strictly follows `LogicalOrder` — priority never reorders admitted
  interactions, and nothing interleaves into an active effect.
- During replay, and during any session holding **exclusive control**, foreign mutation
  admission is gated. Gating MUST be visible: the adapter surfaces UI indication, and
  every refused or deferred human intent is traced as `HumanIntentBlocked` — silent
  dropping of human input is prohibited ([guarantees.md](guarantees.md) §8).
- Read-only sessions (observation, queries) are never blocked by the mutation lane.
- Human input latency is protected by construction: mutation-lane occupancy is bounded
  (§2 `OperationRef` rule, §6 pump bounds), not by bypassing the kernel.

## 8. Cancellation

- A cancel request is a control-lane message referencing a `RequestId`.
- Before the effect permit: the interaction terminates `Cancelled` with
  `phase = BeforeEffect` (replayable with a synthetic pre-cancelled token).
- After the permit: cancellation is delivered to the adapter as a cooperative signal;
  the terminal records `phase = DuringEffect` (or `AfterEffect` when the effect had
  already completed) with full `CancellationEvidence`
  ([guarantees.md](guarantees.md) §5.7).
- Cancellation of a queued (not yet active) interaction is always `BeforeEffect`.
- MCP-level cancellation is *detachment* by default and maps to a kernel cancel only via
  an explicit cancel operation ([protocol-topology.md](protocol-topology.md) §7).

## 9. Continuations

An active effect may commit follow-up invocations:

- The parent declares an ordered list of continuation invocations; the kernel records
  them as `ContinuationCommitment[]` in the parent's terminal (E4).
- Children are admitted only after the parent's terminal is durable; each child's
  `Causality` carries `ParentRequestId + ContinuationOrdinal + fingerprint`
  ([guarantees.md](guarantees.md) §5.8).
- Children are ordinary admissions: they take their own place in `LogicalOrder` and their
  own E2/E3/E4 evidence.

## 10. Incarnation lifecycle

- Runtime teardown/recreation (including a domain reload) produces a new
  `RuntimeIncarnationId`. `NodeRef`s, request namespaces, and pending admissions do not
  survive; stranded requests answer `OutcomeUnknown` after retention and are never
  re-executed automatically.
- Incarnation change is a control-lane lifecycle message; the kernel drains what it can
  prove, marks the rest per the failure matrix ([guarantees.md](guarantees.md) §7), and
  fences the new incarnation against stale submissions (`RuntimeIncarnationId` mismatch
  is refused at admission).
- Replay runs on an **isolated runtime instance** supplied by an application factory —
  never the live one; the live runtime's mutation lane is gated for the duration
  ([recording-replay.md](recording-replay.md) §6).
