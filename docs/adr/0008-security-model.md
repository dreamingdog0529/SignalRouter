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
  expected 32 bytes with `CryptographicOperations.FixedTimeEquals`. The guarantee is
  scoped to **secret-dependent timing**: the compare against the expected token runs in
  time independent of *which* bytes differ. `FixedTimeEquals` alone does not make the hex
  decode itself constant-time, but a malformed or wrong-length token's decode time depends
  only on the attacker's own input, not on the secret, which is sufficient under the
  same-user/local threat model. A wrong-length or non-hex token is a mismatch; nothing
  branches on how *close* the value is to the secret.

### Discovery descriptor

The host publishes a single descriptor object, not a bare token:

```json
{ "schemaVersion": 1, "instanceId": "<guid>", "endpoint": "ws://127.0.0.1:8017/",
  "token": "<64-hex>", "pid": 12345, "startedAt": "<utc>" }
```

- **Location.** `%LOCALAPPDATA%\SignalRouter\hosts\` on Windows; `$XDG_RUNTIME_DIR/signalrouter/`
  on Unix (owner-only `0700`, session-scoped by spec). If `$XDG_RUNTIME_DIR` is unset,
  the host fails to start with a clear message rather than falling back to a
  world-readable location. macOS does not guarantee `XDG_RUNTIME_DIR`, so a **macOS host
  is out of scope for item 9** (the initial supported host is the Windows Editor
  workflow); this is documented, not silently degraded.
- **Namespacing.** The file name includes the port (`host-<port>.json`) so a second host
  on a different `SIGNALROUTER_PORT` does not last-writer-wins the first. This is a
  **credential rendezvous keyed on a pre-shared port**, not port discovery: the runtime
  selects by the port it is configured for. Two Editors that pick the *same* port read the
  same token, so accidental cross-wiring is only prevented when each Editor uses a distinct
  port.
- **`startedAt` and liveness.** `startedAt` is the host's **OS process start time**, not
  the publish time, so that with `pid` it forms a stale-descriptor heuristic. The runtime
  treats a descriptor as stale when no live process has that pid **and** matching start
  time (guarding against pid reuse), tolerating `Process` lookup exceptions
  (`HasExited`/`StartTime` can throw on exit or permission errors). PID + start time is a
  liveness heuristic, **not** proof of host identity.
- **Permissions.** The `hosts` **directory**, the **temp file**, and the final file are all
  created owner-only: an inheritance-disabled owner-only DACL on Windows (applied at
  creation via `FileSystemAclExtensions`, not after the fact) and `0700` dir / `0600` file
  on Unix (`Directory.CreateDirectory(path, UnixFileMode)`, `FileStreamOptions.UnixCreateMode`).
  An existing directory's permissions are re-verified. The token is written only into an
  already-restricted file, and the final permissions are re-verified after the rename. If
  any of this cannot be guaranteed, the host **fails closed** (does not publish, does not
  serve).
- **Atomic publish.** Written to a random-named temp file in the same directory
  (`CreateNew`), permissions applied, token written, then `File.Move(temp, final, overwrite:
  true)`. The reader opens with `FileShare.ReadWrite | FileShare.Delete` and briefly, so it
  never blocks a rename and never observes a partial file. Same-volume rename plus a
  real-OS test that the reader never sees partial JSON pins the guarantee.
- **Lifecycle and cleanup (TOCTOU).** `WaitForShutdownAsync` calls `StopAsync`, which
  releases the port to a successor — so deleting the descriptor *after* shutdown can race a
  successor that rebinds the port and republishes, and an `instanceId` check-then-delete is
  **not** atomic. The descriptor is therefore deleted in **`ApplicationStopping`, while the
  port is still held**; publish and the stopping callback are serialized under one
  lifecycle lock; publishing after stopping has begun is forbidden; a publish failure
  triggers immediate shutdown; and a **readiness gate** withholds `welcome` until publish
  succeeds.
- **Strict reader.** The runtime does not trust the descriptor's endpoint. It validates:
  `schemaVersion == 1`, exactly 64 lower-case hex for `token`, a canonical GUID
  `instanceId`, `pid > 0`, a strict UTC `startedAt`, `host` is literally `127.0.0.1` or
  `::1`, the endpoint's port equals the selector port, no userinfo/query/fragment, path is
  `/`, a descriptor size cap, and rejects duplicate JSON members. A descriptor that fails
  any check is ignored (a rate-limited diagnostic), never connected to — otherwise a
  corrupted file could send the token to a foreign endpoint.
- **Runtime consumption.** The Unity bridge reads the descriptor for its configured port,
  re-reading endpoint **and** token on every connection attempt. **No fixed-port fallback:**
  an absent, stale, or invalid descriptor means "not connected", not "connect to 8017
  anyway". "Invalidating" a descriptor on the runtime side means discarding the in-memory
  snapshot and re-reading; the runtime never deletes the host-owned file.

### Handshake validation and failure handling

- **Host-only required policy.** The expected token lives on a **host-only, required**
  authentication policy, not on the shared `ProtocolPeerOptions` (which both roles use to
  declare themselves). A nullable auth field on the shared type would turn a production
  wiring omission into a silent auth-off; instead the host holds the expected 32 bytes
  (defensive copy) in a dedicated policy that `HostBridgeOptions` requires, failing fast at
  startup on a null or wrong-length token.
- **Order and boundary.** The token is validated on the **typed** `hello`, **before** the
  version check, so a bad token plus an incompatible major still answers `unauthorized`
  rather than `protocol_version_incompatible`. Only a **successfully decoded** `authToken`
  string that is missing, wrong-length, non-hex, or mismatched maps to `unauthorized`; a
  JSON-type or envelope-schema violation is a `malformed_message` from the reader, *before*
  a typed hello exists. `HelloMessage.authToken` stays **optional** in the schema and
  reader — the token-less hello remains a valid v1 message; only host policy requires it.
  A new `unauthorized` protocol error code is added (the item-9 completion ADR 0007
  anticipated, not a v1 envelope change). The token, the hello, and any exception detail
  are never logged; failures are rate-limited so a flood cannot amplify into unbounded log
  volume.
- **Runtime surfacing (no error reflection).** A handshake `ErrorMessage` from the host
  must **not** be routed through the connection decision's outbound `ErrorCode` — that
  field means "an error *I* send back", so reusing it would reflect the host's
  `unauthorized` back at the host. The peer's failure is surfaced as a **separate typed
  outcome** (a `PeerHandshakeErrorCode` / "close, send nothing" verdict), accepted only
  when the `ErrorMessage.inReplyTo` matches the hello this runtime sent. `RuntimeBridgeSession`
  keeps **normal completion** (teardown still looks identical to a peer disconnect for
  send/post/generic-close failures); it additionally exposes an immutable termination
  outcome readable after `RunAsync` completes. The bridge special-cases **only** a code of
  exactly `unauthorized`; every other termination is a generic disconnect. On `unauthorized`
  the bridge discards its in-memory descriptor snapshot and re-reads, and suppresses
  reconnect until the descriptor changes or normal backoff elapses, but **without resetting
  the reconnect attempt counter** and **never** stopping permanently (a host token rotation
  must recover, and a rogue host answering `unauthorized` must not wedge the bridge into a
  DoS stop).
- **Admission isolation (anti-DoS), three stages.** The host today reserves its single
  `active` runtime slot *before* authentication and waits `ReplyTimeout` for the hello, so a
  flood of unauthenticated sockets can starve the legitimate runtime with `runtime_busy`.
  Because `PerformHandshakeAsync` already flips the machine to Ready, sets the session,
  runs the epoch transition, and sends `welcome`, the slot claim cannot simply move later;
  the handshake is split into three stages:
  1. **Receive-and-validate** — bounded bootstrap receive, decode, auth, and protocol
     evaluation, producing a *candidate-local* machine/session/hello. It touches neither the
     epoch, the pending tables, `welcome`, nor the `active`/`ready` slots.
  2. **Promote** — under the gate, confirm `active == null` and atomically promote the
     winning candidate; a loser is sent an `inReplyTo`-correlated `runtime_busy` and closed.
  3. **Commit** — send `welcome` with a **failure-detecting** send, then the epoch
     transition, then `ready = connection`, then recovery and the receive loop. The global
     epoch is not changed before the `welcome` send succeeds, and `ready` is not delayed past
     recovery (which would strand requests made in between).

  Unauthenticated connections occupy only a **bounded, short-lived** handshake stage with
  its own `HandshakeTimeout` and `MaxPendingHandshakes` (separate settings, not the reused
  `ReplyTimeout`; concrete values validated on CI and slow devices). When an authenticated
  runtime already holds the slot, the host returns `runtime_busy` **before reading the
  auth** to protect the incumbent — so "a wrong token while a runtime is active" is
  specified to answer `runtime_busy`, not `unauthorized`. A bounded pool caps the worst-case
  starvation window and resource use; it is not a full availability guarantee over loopback
  TCP.
- **Epoch safety.** An `unauthorized` hello's (arbitrary) session epoch must not change the
  host's `SessionEpoch` or touch any pending execute/reply/control/query state — validation
  is candidate-local and rejected before any global mutation.

### v1.0 compatibility

This is a deployment-behavior change, not a wire-schema break, and completes the v1.0
contract ADR 0007 promised. The matrix must be read at **two levels** — the wire handshake
and the actual deployment — because they diverge:

| Runtime | Host | Wire handshake | Deployment result |
|---|---|---|---|
| new (sends token) | new (validates + publishes descriptor) | authenticated | connects |
| old (no token) | new (validates) | `unauthorized` | rejected — intended |
| new (needs descriptor) | old (no descriptor) | would carry token opaquely | **cannot connect** — old host publishes no descriptor and the new runtime has no fixed-port fallback |
| old (no token) | old (ignores) | connects | connects (pre-item-9) |

The naive "new runtime + old host connects" holds only at the wire level; at the
deployment level the new runtime never reaches an old host, because it will not connect to
a fixed port without a valid descriptor. This is therefore a **matched-version rollout**,
documented as such. Adding an automatic fixed-port fallback would be a downgrade path (and
lets an attacker force it), so it is prohibited. If a token-less development mode is ever
needed it must be an explicit, default-off, warn-on-use insecure flag — never an automatic
fallback after an auth failure.

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

1. **Registration** — per-target field caps and per-target cardinality caps (interactions
   per target, arguments per interaction) are checked before a descriptor changes registry
   state (registration already fails fast on contract violations; these extend it). A
   violation throws and leaves the registry unchanged. Cross-target rules (the target
   count and the parent graph) cannot be decided from one descriptor — a parent may be
   registered later — so they are enforced at capture.
2. **Snapshot capture** — the agent-visible **target count** (checked after view
   filtering, because the documented bound is agent-visible targets), the **parent graph**
   (cycles and depth over the full registered set), and a fixed Core **byte ceiling** are
   enforced. The byte ceiling is checked **while writing**, not after, so a pathological
   registry cannot allocate far beyond the bound before failing. Core is
   session-independent and cannot know a connection's negotiated limit; the byte ceiling
   is a fixed value below the protocol's default receive limit, and the **transport**
   enforces the negotiated per-direction limit at send time (`payload_too_large`).
3. **Host receive** — the host re-validates snapshot shape and counts within its own
   declared receive limit; it does not trust the peer to have enforced them, and rejects a
   malformed element (a non-object target/interaction/argument or a duplicate target ID)
   rather than skipping it.

The cardinality numbers above are provisional. A single worst-case node at the caps (16
interactions × 16 maximum-length arguments plus a maximum-length value) far exceeds any
wire limit, so the byte ceiling — not `count × worst-case-node` — is the binding guard.
PR-2 confirms a realistic UI populated to the target cap captures within the byte ceiling
with headroom and that the ceiling sits below the default receive limit, adjusting the
cardinality caps (not the method) if measurement requires it. State-history length
(§14.1) stays unbounded-by-absence: the feature does not exist yet, so item 9 adds no
history cap.

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

Authentication is large enough to land as its own **sequence of sub-PRs**, ordered so that
every intermediate merge is releasable and no merge enforces auth before the runtime can
present a token. Enforcement flips on **last**:

1. **This ADR** plus the §19 / §25 amendments, the two-level compatibility matrix, the
   descriptor-lifecycle and cleanup ordering, the malformed/auth boundary, and the test
   plan (docs only). *(Resource bounds shipped separately as the PR after the original ADR.)*
2. **Additive Protocol** — the `unauthorized` code, the host-only authentication policy and
   verifier (guarded so nothing enforces until a host wires it), the peer-handshake-failure
   typed outcome, and the error-reflection-prevention tests.
3. **Descriptor contract / store / resolver** — the strict parser, the OS paths, the
   owner-only ACL/mode creation, the process-liveness inspector, and Unix/Windows real-OS
   integration tests (a `windows-latest` CI job for DACL; the existing `ubuntu-latest` job
   for `0700`/`0600`).
4. **Host descriptor lifecycle** — token generation, secure publish, the readiness gate,
   and shutdown-before-port-release cleanup. Auth is **not yet enforced** (no policy wired),
   so old runtimes still connect.
5. **Unity descriptor-only connection** — the `endpointUrl` → port-selector migration,
   per-attempt descriptor re-read, token send, and stale/invalid/no-descriptor tests.
6. **Host handshake refactor + enforcement** — the three-stage
   receive-validate / promote / commit split, the bounded pending-handshake stage, atomic
   promotion, Origin rejection, rate-limited auth diagnostics, and end-to-end auth tests.
   This is the merge that turns authentication on, after the runtime (step 5) already sends
   a token.

Intermediate sub-PRs are assembly order within the effort; only the matched runtime+host
pair after the final merge is the finished configuration. A later **release-gating** PR
(item 9's last piece) adds the Unity `StartBridge()` release policy and documents the host
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
