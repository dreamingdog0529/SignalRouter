# ADR 0008: Security model — authentication, discovery, resource bounds, and release gating

> **Status:** Accepted
> **Date:** 2026-07-25
> **Deciders:** SignalRouter maintainers
> **Builds on:** [ADR 0007](0007-protocol-envelope-v1.md) (runtime protocol envelope v1)

## Context

Design §19 lists the MVP security defaults as a checklist. Roadmap item 8 froze
protocol v1.0 with several of them already in place: Kestrel binds only `127.0.0.1`
and `::1`; sensitive command fields are redacted from recordings (`$secret` markers,
ADR 0005) and from wire results (`ProtocolInteractionOutcome` carries no exception
type, message, stack, rejection message, or state diff — ADR 0007); commands, targets,
and probes require explicit agent-visible registration; the protocol accepts no .NET
type names, reflection, code, or filesystem paths; envelope size, recording size, and
the pending-request ledger are bounded. Item 9 must land the remainder:
**authentication, the resource bounds §19 left as "pre-item-9 defaults", the
release-build gate, and the authentication-failure handling** that the auth path needs
to be safe.

A design review before implementation broke three premises in the naive plan and forced
the decisions below.

1. **The hello token authenticates the runtime to the host, not the reverse.** The
   `authToken` field travels in `hello` (runtime → host, ADR 0007). It lets the host
   reject an unknown runtime; it gives the runtime **no** proof of the host. A rogue
   local process that binds the loopback port while the real host is absent — or against
   a stale discovery file — receives the token and can then drive the Unity runtime.
   Host authentication would require changing `welcome`, which protocol v1.0 froze.
2. **§19's "each runtime launch" token and a host-issued token disagree.** A host
   outlives Editor domain reloads and runtime re-creations, so "one token per runtime
   launch" and "one token per host launch" are different lifetimes.
3. **Compile-time `Release` gating breaks the host.** `dotnet publish` defaults to the
   `Release` configuration, so disabling the control surface under `#if !DEBUG` would
   make every normally-distributed host inert.

## Decision

### Threat model (MVP scope)

The MVP defends a **single-user, single-machine** trust boundary. In scope: preventing a
browser page or a process on another user account from reaching the runtime, and
preventing accidental cross-wiring between an unrelated client and the runtime. **Out of
scope for the MVP:** a malicious process running as the *same* OS user, and a rogue host
impersonating the real one. Both are recorded here as explicit non-goals so the code
never *claims* a protection it does not provide.

Consequences of that boundary, all defense-in-depth on top of loopback-only binding:

- The discovery descriptor (below) is written owner-only, but a same-user attacker can
  still read it; the file's ACL is hardening, not a security boundary. We do not assert
  it stops a same-user attacker.
- The host is **not** authenticated to the runtime in v1.0. Mutual authentication
  (WSS + descriptor-pinned certificate fingerprint, a named pipe / Unix domain socket,
  or a `welcome`-carried host proof) is deferred to the next protocol major, because it
  cannot be added without breaking the v1.0 envelope freeze. This ADR records it as the
  single largest deferred item.
- WebSocket upgrades that carry a browser `Origin` header are rejected before the
  handshake. `Origin` is spoofable, so this is defense-in-depth, never an authentication
  substitute.

### Authentication token: issuer, lifetime, format, comparison

- **Issuer and lifetime: the host, once per host instance.** The `SignalRouter.McpHost`
  process mints one token at startup and holds it for its lifetime. This is stable
  across the Editor domain reloads and runtime re-creations that a single host spans.
  Design §19's "each runtime launch" is amended to **"each `SignalRouter.McpHost`
  instance"**; issuing per runtime launch would require the runtime to register and
  rotate a runtime-owned token with the host on every domain reload, which the MVP does
  not need.
- **Generation:** 256 bits from `RandomNumberGenerator`, lower-case hex (64 chars),
  which satisfies the envelope's identifier contract (≤256 chars, control-character-free).
- **Comparison:** the host decodes the presented hex to 32 bytes and compares against the
  expected 32 bytes with `CryptographicOperations.FixedTimeEquals`. `FixedTimeEquals`
  runs in time dependent on length, not contents, only when both inputs share a length;
  a wrong-length or non-hex token is treated as a mismatch **after** normalizing to a
  fixed-length comparison so decode failure does not leak timing. Nothing branches on the
  token value before the fixed-time compare.

### Discovery descriptor

The host publishes a single descriptor object, not a bare token:

```json
{ "schemaVersion": 1, "instanceId": "<guid>", "endpoint": "ws://127.0.0.1:8017/",
  "token": "<64-hex>", "pid": 12345, "startedAt": "<utc>" }
```

- **Location.** `%LOCALAPPDATA%\SignalRouter\hosts\` on Windows; `$XDG_RUNTIME_DIR/signalrouter/`
  on Unix (owner-only `0700`, session-scoped by spec). If `$XDG_RUNTIME_DIR` is unset,
  the host fails to start with a clear message rather than falling back to a
  world-readable location.
- **Namespacing.** The file name includes the port (`host-<port>.json`) so a second host
  on a different `SIGNALROUTER_PORT` does not last-writer-wins the first. The runtime
  selects by the port it is configured for.
- **Permissions.** Created with an inheritance-disabled owner-only DACL on Windows and
  `0600` under the `0700` directory on Unix. If the restrictive ACL cannot be applied,
  the host **fails closed** (does not publish, does not serve).
- **Publish ordering.** Written atomically (temp file + rename) **only after** Kestrel
  binds successfully. A bind failure leaves no descriptor.
- **Teardown.** On exit the host deletes the descriptor **only if** its `instanceId`
  still matches, so it never removes a successor's file.
- **Runtime consumption.** The Unity bridge reads the descriptor for its configured port,
  re-reading endpoint **and** token on every connection attempt (today both are captured
  once). **No fixed-port fallback:** an absent or stale descriptor means "not connected",
  not "connect to 8017 anyway". A descriptor whose `pid`/`startedAt` no longer
  corresponds to a live host is treated as stale and ignored.

### Handshake validation and failure handling

- **Where.** The host validates the token as a policy step **after** decoding the typed
  `hello` and **before** the Ready transition, the epoch update, and sending `welcome`.
  `HelloMessage.authToken` stays **optional** in the schema and reader — the token-less
  hello remains a valid v1 message on the wire; only host policy requires it.
- **Uniform failure.** Missing, malformed, mismatched, and stale tokens all produce the
  same fixed `unauthorized` error on the wire. A new `unauthorized` protocol error code
  is added (this is the item-9 completion ADR 0007 anticipated, not a v1 envelope
  change). The token, the hello, and any exception detail are never logged; failures are
  aggregated / rate-limited so a flood cannot amplify into unbounded log volume.
- **Runtime reaction.** Handshake failure is surfaced to the runtime owner as a
  **structured** failure code, not a swallowed close. On `unauthorized` the bridge
  invalidates and re-reads the descriptor and suppresses reconnect until the descriptor
  changes or normal backoff elapses. It **never** stops permanently: a host token
  rotation must be recoverable, and a rogue host answering `unauthorized` must not be
  able to wedge the bridge into a permanent stop (a denial-of-service).
- **Admission isolation (anti-DoS).** The host today reserves its single `active` runtime
  slot *before* authentication and waits up to 10 s for the hello, so a flood of
  unauthenticated sockets can starve the legitimate runtime with `runtime_busy`. The
  pending-handshake stage is separated from the authenticated-runtime slot: unauthenticated
  connections occupy only a bounded, short-lived handshake stage and never the runtime
  slot.

### v1.0 compatibility

This is a deployment-behavior change, not a wire-schema break, and completes the v1.0
contract ADR 0007 promised. The compatibility matrix:

| Runtime | Host | Result |
|---|---|---|
| new (sends token) | new (validates) | authenticated |
| old (no token) | new (validates) | **`unauthorized`** — intended; documented |
| new (sends token) | old (ignores token) | connects (token carried opaquely, as in v1) |
| old (no token) | old (ignores) | connects (pre-item-9 behavior) |

No capability negotiation or minor bump gates this: a downgrade path would let an
attacker downgrade too, defeating the requirement. If a token-less development mode is
ever needed it must be an explicit, default-off, warn-on-use insecure flag — never an
automatic fallback after an auth failure.

### Resource bounds

The agent snapshot is a **flat `targets[]` array with `parentId` links**, not a
recursive tree, so the envelope's JSON-depth limit does not bound the UI at all. Bounds
are decided per field and per cardinality, enforced at three points, and never applied by
truncation (truncating a snapshot would silently break parent links and interaction
discovery).

Per-field caps (reusing the envelope's identifier/text limits where they fit):

| Field | Cap |
|---|---|
| `Id`, `ParentId` | ≤ 256 (identifier) |
| `Role` | ≤ 64 |
| `Label` | ≤ 256 (text) |
| `Value` (agent-visible; sensitive values already redacted) | ≤ 1024 |
| interaction wire name, argument name | ≤ 256 |
| argument type token | ≤ 64 |

Cardinality caps:

| Quantity | Cap |
|---|---|
| agent-visible targets per snapshot | 1024 |
| available interactions per target | 16 |
| arguments per interaction | 16 |
| parent-chain depth | 32 |

Structural validation: the parent graph must have **no cycles** and no chain deeper
than the cap. A `parentId` that does not resolve to a registered target is **not** an
error — a target may be grouped under a non-interactive container that is not itself a
registered interaction target, and agent-view filtering can remove a registered parent
from the snapshot — so an unresolved parent simply terminates the chain.

Enforcement points:

1. **Registration** — per-target field and cardinality caps and parent-link validity are
   checked before a descriptor changes registry state (registration already fails fast on
   contract violations; these extend it). A violation throws and leaves the registry
   unchanged.
2. **Snapshot capture** — total agent-visible cardinality and the **aggregate serialized
   byte size** are checked as the snapshot is produced. If serialization would exceed the
   negotiated send limit, capture fails fast rather than emitting a truncated snapshot or
   deferring the cost to the wire layer.
3. **Host receive** — the host re-validates snapshot shape and counts within its own
   declared receive limit; it does not trust the peer to have enforced them.

The exact cardinality numbers above are provisional: PR-2 confirms that
`maxTargets × worst-case-node-size + envelope ≤ negotiated limit` holds for the caps as
written and benchmarks a realistic UI for allocation/latency, adjusting the cardinality
caps (not the method) if the measurement requires it. State-history length (§14.1) stays
unbounded-by-absence: the feature does not exist yet, so item 9 adds no history cap.

### Release gating

- **Unity.** Release policy is evaluated at the **`StartBridge()` entry point**, not by
  overloading the serialized `connectOnEnable` field (existing scenes would keep `true`,
  and `StartBridge()` is callable directly). In a non-development player the automatic
  `OnEnable` start is silently skipped as a normal state; an **explicit** `StartBridge()`
  call in a gated build fails fast with a fixed message. Enabling the bridge in a release
  player requires a dedicated, explicit opt-in (a custom build symbol or an explicit
  setting), never `connectOnEnable` alone. Current run mode is read with
  `Debug.isDebugBuild`, not `DEVELOPMENT_BUILD` alone (the latter reflects build-time
  configuration only).
- **Host.** The host is **not** gated by build configuration. Registering
  `SignalRouter.McpHost` in an MCP client to be spawned **is** the explicit operator
  opt-in; adding a second host-side compile gate would only make the normally-published
  (`Release`) host inert. "Release build disabled by default" is therefore satisfied on
  the host side by *not shipping the host inside the game*: it is a separate developer
  tool, absent from a shipped player, and it starts only when an operator wires it up.

## Implementation order

Four PRs, in this order (server enforcement must not merge ahead of the client change
that keeps old runtimes working):

1. **This ADR** plus the §19 / §25 amendments, the compatibility matrix, and the test
   plan (docs only).
2. **Resource bounds** — the per-field and cardinality caps, parent-graph validation, the
   aggregate-byte guard, host-side re-validation, and the benchmark.
3. **Authentication** — token generation, the discovery descriptor and its ACL, runtime
   re-read, host validation with `unauthorized`, structured handshake-failure surfacing,
   descriptor invalidation/backoff, and admission isolation, landed end to end (internal
   commits are fine; a server-only merge that breaks old runtimes is not).
4. **Release gating** — the Unity `StartBridge()` release policy and the documented host
   activation posture.

## Consequences

- Authentication protects the host from an unknown runtime and stops browser / other-user
  reach; it does **not** protect the runtime from a rogue same-user host. That gap is
  explicit and deferred to the next protocol major.
- The discovery descriptor makes the runtime tolerant of host restarts and port changes
  and removes the fixed-port assumption, at the cost of a small file-lifecycle protocol
  the host and runtime must both honor.
- Old runtimes stop connecting to a new host by design; the compatibility matrix and
  release notes must say so, because it is a silent behavior change otherwise.
- Resource bounds add fail-fast checks at registration and capture; a UI that exceeds a
  cap surfaces an error at registration time instead of producing an oversized or
  truncated snapshot later.
- The host's "disabled in release" posture is satisfied structurally (it is not shipped in
  the player) rather than by a compile switch, which keeps the normally-published host
  functional.
