# SignalRouter v2 — Architecture

> **Status:** v2 design draft
> **This document shows the integrated shape only.** Normative rules live in
> [spec/](spec/); decision rationale lives in [adr/](adr/); the stable "why" lives in
> [philosophy.md](philosophy.md).

## 1. System context

```text
                       ┌───────────────────────────── engine process ─┐
MCP client             │   Adapter (Unity, …)                         │
   │ stdio             │      │ SDK: nodes · effects · ingress · pump │
   ▼                   │      ▼                                       │
Gateway ── ILocalDuplexChannel ──► Runtime                            │
(external, restartable,│      Kernel ─ single-owner state machine     │
 state-minimal,        │        │                                     │
 non-authoritative)    │        ├─ KernelTrace     (lossy, bounded)   │
                       │        ├─ RecoveryIndex   (authoritative)    │
                       │        ├─ RecordingSink   (durable evidence) │
                       │        └─ StateStore      (content-addressed)│
                       └──────────────────────────────────────────────┘
```

- The **runtime** is the only authority for interaction state; the **gateway**
  terminates MCP, discovers/authenticates the runtime, and holds nothing that cannot be
  reconstructed by querying it ([spec/protocol-topology.md](spec/protocol-topology.md)).
- The runtime connects out to the gateway (client role) via the owner-only discovery
  descriptor; the loopback WebSocket is the first `ILocalDuplexChannel`.

## 2. Logical modules and dependency direction

```text
Contracts (BCL only)          ← capability contracts, identity, event algebra, outcomes
  ├─ Kernel (BCL only)        ← state machine, mailbox, lanes, stores' in-memory core
  ├─ ProtocolSession (BCL)    ← session model, split-phase shapes, handshake logic
  └─ AdapterSdk (BCL only)    ← INodeSource / IEffectExecutor / IIngressSource / IPumpHost

Codec leaves (own the only serializer dependencies; independently versioned)
  ├─ Codec.CanonicalState     ← materialization encoding, ContentId production
  ├─ Codec.Recording          ← RecordingEventSchema artifacts
  └─ Codec.Protocol           ← wire envelope encoding

Infrastructure leaves
  ├─ Transport.WebSocket      ← first ILocalDuplexChannel
  ├─ Gateway.Mcp              ← the external gateway executable
  └─ Adapter.Unity            ← first engine adapter (+ its engine-version pins)
```

Rules: dependencies point up this list, never sideways into an engine; JSON types never
appear in Contracts/Kernel/AdapterSdk; distribution packaging may bundle logical
modules, chosen by adapter restore burden, without changing these boundaries
([adr/0007](adr/0007-codec-and-package-boundaries.md)).

## 3. Main data flows

**Mutation (any ingress):**

```text
submit(requestId, fingerprint, invocation, envelope)
  → admission: dedup · authorize · LogicalOrder · RecoveryIndex(pending) · ack
  → mutation lane: Validating → Invoking(permit) → WaitingCompletion → Observing → Terminal
  → RecoveryIndex(terminal) · [E2/E3/E4 when recording] · answer waiters
```

**Observation (pull):** view request → projection under ViewContract → materialization
(redacted, bounded, completeness-mapped) → snapshot pinned for pagination.

**Recording:** open fence → E1(+base snapshot) → per-interaction E2/E3/E4, barriers E5,
waits E6 → close fence → E7. Blobs commit StateStore-first
([spec/guarantees.md](spec/guarantees.md) §5).

**Replay:** verify artifact → pre-scan for stop points → isolated runtime from the
adapter's factory (live mutation lane gated, visibly) → per-entry re-admission and
typed comparison → first non-Equal stops with a structured report.

**Recovery:** gateway restart → re-discover, re-authenticate → `query(requestId)`
against the runtime; runtime crash → new incarnation, stranded work answers
`OutcomeUnknown` after retention. No second ledger exists anywhere.

## 4. Kernel at a glance

Single-owner, mailbox-driven, resumable state machines; one active mutation, control
lane prioritized at every turn boundary; adapter effects leave and return as messages;
the host engine drives everything through `Pump(maxTurns, deadline, framePhase)`
([spec/kernel-execution.md](spec/kernel-execution.md)).

## 5. Store ownership

| Store | Owner | Truth it holds |
|---|---|---|
| `KernelTrace` | kernel | recent diagnostics; loss permitted, gaps marked |
| `RecoveryIndex` | kernel | what was admitted and what it became — the recovery authority |
| `RecordingSink` | recording op | non-droppable ReplayEvidence for one artifact |
| `StateStore` | runtime | immutable, domain-namespaced observation materializations |

Shared between them: the in-memory event algebra only. Persistent schemas are four
independent contracts ([spec/observation-state.md](spec/observation-state.md) §6).

## 6. Trust boundaries

1. **Process boundary** — gateway ↔ runtime: authenticated local channel, owner-only
   rendezvous, bounded messages, fixed failure answers.
2. **Exposure boundary** — application ↔ agents: nothing visible or invocable without
   explicit registration; views are domain-scoped; redaction precedes every
   materialization.
3. **Artifact boundary** — replay input: integrity-verified, limit-checked,
   allowlisted, provenance-gated before a single capability executes.
4. **Release boundary** — ingress off by default in release builds; the gateway is
   absent from shipped applications ([spec/security-resources.md](spec/security-resources.md)).

## 7. Specification map

| Question | Spec |
|---|---|
| What answers exist at every failure boundary; what evidence a recording holds | [spec/guarantees.md](spec/guarantees.md) |
| What nodes, capabilities, identities, and IDs mean | [spec/semantic-model.md](spec/semantic-model.md) |
| How execution is scheduled and cancelled | [spec/kernel-execution.md](spec/kernel-execution.md) |
| How observation is projected, delivered, stored | [spec/observation-state.md](spec/observation-state.md) |
| How artifacts are written, compared, replayed | [spec/recording-replay.md](spec/recording-replay.md) |
| How processes connect, recover, and speak MCP | [spec/protocol-topology.md](spec/protocol-topology.md) |
| What adapters implement and how support is proven | [spec/adapter-conformance.md](spec/adapter-conformance.md) |
| What is defended, bounded, and gated | [spec/security-resources.md](spec/security-resources.md) |

Decision rationale: [adr/0001](adr/0001-single-owner-kernel.md) –
[adr/0007](adr/0007-codec-and-package-boundaries.md).
