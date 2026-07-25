# ADR 0004 (v2): External State-Minimal, Non-Authoritative Gateway

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-25
> **Normative spec:** [../spec/protocol-topology.md](../spec/protocol-topology.md)

## Context

v1's external MCP host grew a ~1,670-line bridge holding pending tables, epoch
transitions, single-flight control recovery, and byte-exact resend windows — a second
ledger that had to mirror runtime truth and was patched repeatedly under review. The
clean-slate question: keep an external host, embed an MCP server in the engine, or
make the host a thin proxy?

## Decision

Keep the external process topology, but define the host as a **state-minimal,
non-authoritative gateway**:

- the runtime is the only authority (identity, dedup, outcomes, retention, recovery,
  operation state); the gateway owns MCP termination, discovery/authentication, the
  live connection, ephemeral call↔request correlation, and deadlines/detach;
- the base protocol is split-phase (`submit → accepted`, `query → pending | terminal |
  unavailable`) with caller-assigned `RequestId` and fingerprint-verified idempotent
  submit — which is what makes a thin gateway *possible*;
- after a gateway restart: re-discover, re-authenticate, query the runtime; no gateway
  ledger is ever restored;
- when the runtime is unreachable, answer `RuntimeUnavailable`/`OutcomeUnknown`; never
  fabricate;
- MCP cancellation detaches by default; UI cancellation is a distinct explicit
  operation;
- the channel is `ILocalDuplexChannel` (loopback WebSocket first); runtime-as-client
  and the owner-only descriptor rendezvous carry over from v1.

## Alternatives considered

- **Literal stateless proxy:** impossible — answering a pending MCP call requires an
  ephemeral waiter; "state-minimal + non-authoritative" is the honest formulation.
- **In-engine MCP server (streamable HTTP, no gateway):** rejected as default —
  discovery/origin/auth/session/resumption obligations do not disappear, an ASP.NET
  Core-class dependency enters the engine (violating the dependency axiom), and an
  engine restart takes down both the MCP server and the recovery authority
  simultaneously. Retained only as a possible deployment profile for headless hosts.
- **v1 shape (authoritative host ledger + recovery window):** rejected — duplicated
  truth was the complexity engine; every recovery feature had to be built twice and
  reconciled.

## Consequences

- Gateway crash/restart becomes a non-event for interaction truth.
- Host-side recovery code (pending tables, resend windows, epoch reconciliation)
  is deleted by design; the runtime's `RecoveryIndex` is the single implementation.
- The runtime carries slightly more (result retention, operation ledger) — where the
  authoritative state already lives.
- Engine death still kills recovery *authority* with the engine; the protocol answers
  honestly (`RuntimeUnavailable`, then `OutcomeUnknown` after retention) rather than
  pretending a survivor exists.
