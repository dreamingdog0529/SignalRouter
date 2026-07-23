# ADR 0007: Runtime protocol envelope v1

> **Status:** Accepted
> **Date:** 2026-07-24
> **Deciders:** SignalRouter maintainers
> **Builds on:** [ADR 0005](0005-recording-schema-v1.md) (recording schema v1),
> [ADR 0006](0006-strict-replay-semantics.md) (strict replay semantics)

## Context

Design §18.3 specifies the runtime WebSocket protocol only at the envelope level:
required fields, a handshake that exchanges versions, capabilities, and payload limits,
major-version gating, and a forward-compatibility policy. The wire schema is a public
compatibility surface, so §25 requires an ADR for the concrete contract. Three gaps
surfaced during design review and drive the decisions below: the dispatcher mints
request IDs internally, which makes `get_interaction_result` unanswerable after a
disconnect that races the reply; a bounded runtime cannot promise unlimited
exactly-once semantics without saying what it forgets; and §13.3 described the session
epoch as changing on reconnect, which would break result recovery — the one thing the
epoch exists to scope.

**Draft status:** protocol v1.0 is an internal draft until the MCP host (roadmap
item 8) ships against it. Message additions and payload extensions before that point
are ordinary edits to this ADR, not new majors.

## Decision

### Envelope

One protocol message is one UTF-8 JSON object — and, in item 8, one complete WebSocket
text message (a fragmented transport read is reassembled to `EndOfMessage` before it
reaches the reader). The envelope carries `protocol`, `messageId`, `type`,
`sessionEpoch`, `requestId`, `inReplyTo`, and `payload`. §18.3's field list is treated
as a minimum: `inReplyTo` is added because multiplexing concurrent requests over one
socket needs per-transmission correlation. `messageId` correlates one transmission — a
resend uses a fresh `messageId`; `requestId` identifies one logical interaction across
process boundaries (§7.2) and never changes on resend.

Wire identifiers are length-capped and control-character-free. Error text is bounded,
single-line, and never echoes payload content or credentials; decode failures report
fixed descriptions and carry back the envelope's `messageId`/`type` only after those
values individually validate.

### Versioning and handshake

`protocol` is a strict `MAJOR.MINOR` string. Majors gate compatibility: the reader
decodes a foreign-major message only to the envelope — enough to answer
`protocol_version_incompatible` — and never its payload. Minors are cumulative
(implementing minor N implies 0..N), which is what makes "lower minor wins" sound: the
hello's envelope version is the highest the runtime speaks, and the welcome's envelope
version **is** the selected version, which the runtime verifies never exceeds its
offer.

The handshake (`hello` from the runtime, `welcome` from the host, per §18.1's
connection direction) exchanges an informational peer version, a capability set, and
**per-direction** receive limits: each side declares what it will accept and adopts the
peer's declaration as its send limit, so a large registry snapshot and a small execute
request stop competing for one number. Before the handshake completes, both sides
enforce a fixed bootstrap limit that hello, welcome, and error always fit within.
Capabilities intersect ordinally; unknown names degrade silently. v1.0 defines no
capability names. The hello reserves an `authToken` field — §19's token travels in the
handshake payload, keeping authentication inside the protocol and testable in pure C#;
item 9 adds validation and the `unauthorized` code, and until then the field is carried
opaquely.

### Request identity and the submission ledger

The wire `requestId` is **submitter-assigned**: the host (ultimately the MCP caller)
names the request. Runtime-assigned IDs have an unfixable race — accept, disconnect
before the acknowledgment, and the host holds side effects it can neither query nor
safely resend. `interaction_accepted` therefore acknowledges reservation and FIFO
admission (carrying the runtime `sequence`), it does not distribute identity.

The runtime tracks every submission in a bounded ledger keyed by
`(sessionEpoch, requestId)` with states `Received → Queued → Running → Terminal`, a
byte-exact content fingerprint, and a retention deadline for terminal results. A resend
with the same fingerprint is answered from the ledger and never re-dispatched; the same
ID with different content is `request_id_conflict` (arguments must be repeated
verbatim — semantically-equal-but-reformatted JSON is a conflict, erring against double
execution). Bounded capacity and unlimited exactly-once cannot coexist, so the
guarantee is explicit: pending entries are never evicted, terminal results survive
until their deadline, and a full ledger answers `capacity_exhausted` instead of
forgetting old work. An expired or unknown query answers `result_unavailable`, which
the host must surface as `OutcomeUnknown` (§8), never as an invented outcome.

### Split-phase submission (resolved, item 8a)

The Core API backing host-owned identity is `InteractionDispatcher.Submit`: identity,
sequence, and FIFO position are fixed synchronously under the enqueue lock — the
duplicate-live-ID check, sequence reservation, queue chaining, and cancellation
registration are one linearization point — and the caller receives them alongside two
signals. `Started` resolves true at the moment execution genuinely begins (the
Queued → Running transition the ledger tracks) and false when the request terminates
without starting; `Completion` carries the terminal result.
`InteractionAdmissionKind.Completed` marks requests that never entered the FIFO
(immediate rejections), for which no `interaction_accepted` applies. `TryCancel`
accepts a cancellation request without guaranteeing a `Cancelled` outcome — the
terminal status stays authoritative — and answers false for unknown or terminal IDs.

Deduplication authority is deliberately split: the protocol ledger is the single
transport-level authority (the wire `idempotencyKey` feeds its fingerprint), so
submissions carry no idempotency key and Core fails fast on concurrent duplicate IDs
instead of deduplicating. A ledger reservation whose Core admission throws (replay
lease, disposed dispatcher) is discarded via `Abandon` — the only state that may be
forgotten, because nothing was queued and no acceptance was sent; the transport maps
the thrown rejection to a transport-plane error. The ledger defaults resolve §25:
capacity 256, terminal retention 10 minutes.

Consequence for the MCP tool surface (item 8c): a caller-side timeout is only
recoverable if the MCP caller knows the request ID, so `execute_interaction` accepts an
optional caller-supplied `request_id` (identifier-validated; host-generated otherwise)
and every response shape — including timeout and error surfaces — carries the
request ID back.

### Session epoch

§13.3 is corrected: the session epoch changes when the Unity runtime is recreated
(including domain reload) and is **preserved across transport reconnects** — changing
it per connection would destroy exactly the post-disconnect result recovery it scopes.
The hello proposes the runtime's current epoch, the welcome echoes it, and after the
handshake every epoch-stamped message must match; a mismatch closes the connection
because a changed epoch is by definition a new runtime session. Requests stranded by an
epoch change are never re-executed automatically. `ping`/`pong` are epoch-free
protocol-level liveness (application data, unlike WebSocket control frames — answering
proves the receive loop is alive); a connection identifier, if ever needed, is an
item-8 envelope addition.

### v1.0 message set

`hello`, `welcome`, `error`, `ping`/`pong`, `execute_interaction`,
`interaction_accepted`, `interaction_result`, `get_interaction_result`,
`cancel_interaction` (a disconnect cancels nothing per §8, so recovery needs explicit
cancellation plus result queries), and `get_registry_snapshot`/`registry_snapshot`
(the canonical semantic-ui agent-view document, verbatim, with its probe version; the
host projects `get_ui_tree` and `list_interactions` from it). `wait_for` and the
recording/replay operations are deferred to item 8 for co-design with host semantics;
recordings will be addressed by logical artifact handles, not raw paths, keeping §19's
path policy in item 9.

### Forward compatibility and decoding

Unknown envelope and payload members are ignored; unknown message types are reported
(`unknown_message_type`) but never executed. Everything else is strict: duplicate
members at typed levels (System.Text.Json does not reject them itself), missing or
wrong-typed required members, and constructor contract violations are all
`malformed_message`. Command `arguments` pass through as opaque raw JSON for the Core
catalog's strict per-command validation — a lenient transport shell around a strict
command core (§6.1, D11). JSON depth is bounded end to end: the reader parses the whole
envelope at one depth limit, and opaque payloads carry a budget reduced by their
nesting offset so writer output always re-decodes. Size limits are enforced before
parsing on receive and during encoding on send. The reader returns typed verdicts
instead of throwing; only `JsonException` and constructor `ArgumentException`s are
converted, never a blanket catch.

### Result payload

`interaction_result` carries a **wire-owned** sanitized projection
(`ProtocolInteractionOutcome`), not `RecordedOutcome` — the recording schema and the
wire protocol version independently and must not share a serialized contract. The
projection mirrors ADR 0005's floor: status, stage IDs and statuses, rejection code,
application fault code, and per-probe hash maps. Exception types, messages, stack
traces, `RejectionInfo.Message`, and `StateDiff` never cross the wire in v1.0
(pre-empting item 9 redaction). This narrows a literal reading of §18.2's
"`execute_interaction` returns a terminal `InteractionResult`": the tool returns the
terminal outcome in sanitized wire form. Richer detail (rejection messages,
property-level diffs) would be additive minor-version work.

### Alternatives rejected

- **Runtime-assigned request IDs with an acceptance notification.** The
  disconnect-before-acknowledgment race leaves the host unable to query or safely
  resend; no notification ordering fixes identity the host never received.
- **Reusing `RecordedOutcome` on the wire.** Couples two contracts with different
  change reasons; a recording schema bump would silently become a wire break.
- **Auth in WebSocket upgrade headers.** Moves the security boundary into transport
  plumbing that pure C# conformance tests cannot reach; loopback-only operation makes
  post-accept rejection harmless.
- **Per-message type versioning (`type@version`).** Commands need it because catalogs
  evolve per command; protocol messages evolve together and already sit under the
  envelope version.
- **Silent LRU eviction for the ledger.** Turns "exactly once" into "probably once"
  under load without telling the host; refusing new work is honest and recoverable.
- **Normalizing arguments JSON before fingerprinting.** Canonicalization cost and
  subtle equivalence bugs to tolerate a client that rewrites its own resend bytes;
  strictness errs against double execution.

## Consequences

- **Positive.** Every protocol behavior — negotiation, phase enforcement, duplicate
  and conflict handling, retention — is pure and tested without sockets; item 8 wires
  transport around decided contracts instead of deciding them mid-implementation.
- **Negative.** Hosts must generate unique request IDs or see conflicts; resends must
  repeat bytes verbatim.
- **Open items (item 8).** Transport framing and reconnect wiring (8b); `wait_for`
  and recording messages with artifact handles (8c/8d). Resolved in 8a: the Core
  split-phase submission API with `cancel_interaction` dispatch wiring, and the
  default ledger capacity and retention (§25). Item 9: `authToken` validation,
  timing-safe comparison, `unauthorized`, final limits.

## Implementation

- `ProtocolSchema.cs` — property names, message types, error codes, limits.
- `ProtocolVersion.cs` / `ProtocolHandshake.cs` — strict version parsing; pure hello
  and welcome evaluation with per-direction limits and capability intersection.
- `ProtocolMessages.cs` / `ProtocolInteractionOutcome.cs` — the twelve v1 message
  types and the sanitized wire outcome projection.
- `ProtocolMessageWriter.cs` / `ProtocolMessageReader.cs` — deterministic encoding
  under a write-time size bound; verdict-based decoding with duplicate-member
  rejection and identifier-validated error references.
- `ProtocolConnectionStateMachine.cs` — phase, role-direction, and epoch enforcement.
- `ProtocolRequestLedger.cs` — bounded submission tracking with fingerprint
  deduplication, retention, and unadmitted-reservation abandonment.
- `InteractionSubmission.cs` / `InteractionDispatcher.Submit` / `TryCancel` — the
  split-phase Core submission API (item 8a).
- Unity `ProtocolGoldenVectorTests.cs` — byte-exact golden-vector parity against the
  bundled System.Text.Json 8.0.
