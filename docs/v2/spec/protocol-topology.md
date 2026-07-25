# SignalRouter v2 Specification — Protocol and Topology

> **Status:** v2 design draft — normative once the v2 set is accepted
> **Applies to:** SignalRouter v2 (clean-slate design)
> **Companion specs:** [guarantees.md](guarantees.md) · [kernel-execution.md](kernel-execution.md) ·
> [semantic-model.md](semantic-model.md) · [security-resources.md](security-resources.md)

This spec defines the process topology, the authority split between runtime and
gateway, the split-phase session protocol, discovery and authentication, and the MCP
mapping. The key words MUST, MUST NOT, SHOULD, and MAY follow RFC 2119.

## 1. Topology

```text
MCP client ──stdio── Gateway (external, restartable, state-minimal, non-authoritative)
                        │ ILocalDuplexChannel (first implementation: loopback WebSocket)
                        ▼
                     Runtime ── Kernel · RecoveryIndex · RecordingSink · StateStore
```

- The **runtime** (in the engine process) is the **only authority** for interaction
  state.
- The **gateway** (the v1 McpHost's successor) terminates MCP and forwards; it can crash
  and restart at any time without losing interaction truth.
- The runtime connects to the gateway as a client (survives engine domain reloads
  without restarting the MCP client); the rendezvous is the owner-only discovery
  descriptor (§5).
- The channel abstraction is `ILocalDuplexChannel`; loopback WebSocket is its first
  implementation, not an architectural commitment. Remote (non-local) channels are out
  of scope ([security-resources.md](security-resources.md) §2).
- An in-engine MCP server (streamable HTTP, no gateway) is a possible **deployment
  profile** for headless hosts that already run an HTTP stack; it is not the standard
  topology and receives no design weight in v2.

## 2. Authority split

| Runtime owns | Gateway owns |
|---|---|
| `RuntimeIncarnationId` | MCP initialization and capability negotiation |
| Request identity, fingerprints, dedup | stdio lifetime |
| Admitted/pending/terminal outcomes | Tool schemas and result projection |
| Cancel intent and disposition | Runtime discovery and authentication |
| Retention and expiry | The current runtime connection |
| Recording/replay operation state | Ephemeral correlation of live MCP calls to `RequestId`s |
| `RecoveryIndex` | Deadlines, detach, backpressure toward the MCP client |

The gateway holds **no second ledger**: no terminal cache, no recovery journal, no
persisted correlation state. "State-minimal" is deliberate — a literal stateless proxy
is impossible (a pending MCP call needs an ephemeral waiter), but everything the gateway
holds is reconstructible by querying the runtime.

After a gateway restart: re-discover, re-authenticate, and answer in-flight questions by
**querying the runtime** — never by restoring gateway-side state.

When the runtime is unreachable, the gateway answers `RuntimeUnavailable` (or `Pending`
/ `OutcomeUnknown` where those are provably correct). It MUST NOT fabricate terminals
([guarantees.md](guarantees.md) §3.4).

## 3. Session model

- A **session** binds one gateway connection to one runtime incarnation after
  authentication. Session state is: negotiated protocol version, capability set, size
  limits per direction, and the incarnation ID.
- Reconnection preserves the incarnation; recovery scope binds to
  `RuntimeIncarnationId`, never to the connection.
- Incarnation change invalidates `NodeRef`s and pending requests per
  [kernel-execution.md](kernel-execution.md) §10; the gateway relays the new incarnation
  to interested clients and never bridges requests across incarnations.
- Envelope hygiene carries over from v1: versioned envelope, strict major gating with
  lower-minor-wins negotiation, per-direction size limits, ignore-unknown-member
  forward compatibility for minor revisions, and unknown message types never executed.

## 4. Split-phase protocol

The base protocol shape for every mutation is:

```text
submit(requestId, fingerprint, invocation, envelope) → accepted(requestId) | rejected(reason)
query(requestId) → pending | terminal(outcome) | unavailable
```

- `RequestId` is assigned by the caller **before** dispatch, so a retry after any crash
  can re-submit idempotently: the runtime deduplicates by
  `(RuntimeIncarnationId, RequestId)` with fingerprint verification
  ([kernel-execution.md](kernel-execution.md) §3).
- Admission is explicitly acknowledged; acceptance is never inferred from silence.
- Terminal results flow either as an asynchronous completion message on the session or
  via `query`; both carry the same terminal projection.
- Long-running operations (waits, recording control, replay) follow the same shape with
  `OperationId`s and are single-flight per runtime where the operation demands it.
- A query for an ID the runtime cannot prove anything about — never admitted in this
  incarnation, or expired from retention — answers `OutcomeUnknown` directly; the §6
  taxonomy is exhaustive, and the runtime never guesses.

## 5. Discovery and authentication

Carried forward from v1 (ADR 0008) with the same posture:

- The gateway mints a per-instance 256-bit token and publishes an **owner-only
  discovery descriptor** (platform-appropriate user-private location, atomic publish,
  strict parse, loopback-literal endpoints only, port-scoped namespace).
- The runtime re-reads the descriptor per connection attempt, presents the token in the
  hello, and the verifier compares in fixed time. Authentication is evaluated before
  version negotiation; failures answer a fixed `unauthorized` with no echo of
  credentials.
- The trust boundary remains single-user, single-machine: a same-user malicious process
  and a rogue gateway are non-goals for v2's default profile
  ([security-resources.md](security-resources.md) §2). Mutual authentication is a
  designed-for extension: the v2 hello/welcome reserve the negotiation surface so adding
  it is a minor revision, not a new major.

## 6. Failure answers

The protocol's failure vocabulary is exactly the guarantees taxonomy
([guarantees.md](guarantees.md) §3): `Pending`, `Terminal(outcome)`,
`RuntimeUnavailable`, `OutcomeUnknown`, plus admission `rejected(reason)`. No transport
condition may surface as an invented interaction outcome.

## 7. MCP mapping

MCP tools are projections of the split-phase protocol:

| Tool | Maps to |
|---|---|
| `observe` (tree/snapshot read, paginated, pinned) | view snapshot pull |
| `invoke` | submit + bounded wait convenience; on timeout returns `pending` with `requestId` |
| `get_result` | query |
| `wait_for` | predicate operation (armed/resolved) |
| `cancel` | explicit kernel cancel request |
| `start_recording` / `stop_recording` / `replay_recording` / `get_operation_result` | control operations |
| `inspect_state` (read-only timeline) | state timeline query ([observation-state.md](observation-state.md) §7) |

Rules:

- Convenience wrappers (blocking `invoke`) are sugar over split-phase; the underlying
  `requestId` is always returned so a timed-out caller can recover by `query`.
- **MCP cancellation ≠ UI cancellation.** An MCP-level cancel or client disconnect
  detaches the waiter by default; the interaction proceeds to its true terminal. UI
  cancellation happens only through the explicit `cancel` tool. MCP Tasks, where
  negotiated, MAY surface long-running operations, but MUST NOT be the only recovery
  path.
- Exceptions never cross the MCP seam; every tool answer is a typed projection
  including the failure vocabulary of §6.
- Tool payloads are bounded and exposure-filtered per
  [security-resources.md](security-resources.md) §4–5.

## 8. Protocol versioning

The v2 wire protocol starts at its own major version 1 (it shares no compatibility with
the v1 protocol). Major mismatches fail the handshake explicitly; minors negotiate
lower-wins. Recording schema, trace schema, and protocol versions are independent
([observation-state.md](observation-state.md) §6).
