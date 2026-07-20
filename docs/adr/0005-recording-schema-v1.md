# ADR 0005: Recording schema v1

> **Status:** Accepted
> **Date:** 2026-07-21
> **Deciders:** SignalRouter maintainers
> **Builds on:** [ADR 0001](0001-canonical-state-hashing.md) (canonical state hashing)

## Context

Design §15 commits recordings to append-only JSON Lines with a session header, a request
event durable before the first stage, and exactly one terminal event per known outcome;
§15.1 adds the guarantees (sequence order, secret-key substitution, version-rejecting
readers, artifact-root confinement, `OutcomeUnknown` for unpaired requests). The §15
example, however, was illustrative and under-specified in ways that matter to the strict
replayer (§16.1) and the security model (§19):

- it showed a single `beforeHash`/`afterHash`, but state observations are **per-probe**
  hash sets;
- it omitted the rejection code, which §16.1 requires for re-verifying `Rejected` entries;
- `FaultInfo` carries a .NET exception type, message, and stack trace, none of which may
  be persisted (§19 forbids .NET type names in recordings; §12.2 excludes stack traces
  from replay comparison);
- the secret-key format, durability level, size bound, and truncation-recovery rule were
  unspecified.

Because a recording is a persistent schema, fixing these requires an ADR (§25).

## Decision

One UTF-8 JSON object per line, each line terminated by `\n` (0x0A). The first line is
the session header. `schemaVersion` is 1 and is independent of command schema versions.

```json
{"kind":"session","schemaVersion":1,"sessionId":"<registry SessionEpoch>","appBuild":"<caller-supplied>","startedAt":"2026-07-21T03:04:05.1234567Z"}
{"kind":"interaction_requested","sequence":12,"requestId":"<32-hex>","origin":"Agent","command":{"name":"click","version":1,"targetId":"menu.start","arguments":{}}}
{"kind":"interaction_completed","sequence":12,"requestId":"<32-hex>","result":{"status":"Faulted","stages":[{"id":"click.apply-state","status":"Completed"},{"id":"click.sound","status":"Faulted"}],"faultCode":"AudioDeviceUnavailable"},"state":{"before":{"semantic-ui":"<64-hex>"},"after":{"semantic-ui":"<64-hex>"}}}
```

1. **Header.** `sessionId` is the registry's `SessionEpoch`; `appBuild` is a required,
   caller-supplied validated identifier (Core has no build-number source); `startedAt` is
   the injected clock's UTC instant in round-trip ISO 8601. The header is written eagerly
   at recorder construction.

2. **Request event.** Appended inside the dispatcher's enqueue lock — the only place the
   §15.1 sequence-order guarantee holds under concurrent enqueue — and before the FIFO
   queue-tail swap, so a failed append leaves the queue chain intact. Durability before
   the first stage follows from flushing at enqueue; stages only run after dequeue.
   `origin` is the `InteractionOrigin` name. `correlationId` and `idempotencyKey` are not
   recorded: strict replay does not verify them, and §7.2 classifies them as runtime
   metadata. Validation failures that precede enqueue (`CommandNotRegistered`,
   `ReentrantDispatch`; §7.1 step 1) are never recorded, which is why recorded sequences
   are strictly increasing but **not contiguous**.

3. **Terminal event.** Exactly one per known outcome, matched by `(sequence, requestId)`.
   `result.stages` is the full stage progress in index order (the array position is the
   index), superseding the illustrative `failedStageId`/`completedStageIds`: the failed
   stage is the last element and completed stages precede it. `result.rejectionCode` is
   present exactly when the status is `Rejected` (no rejection message — no free text in
   recordings). `result.faultCode` is present exactly when the status is `Faulted` and
   holds `FaultInfo.ApplicationCode` or `null`; the exception type, message, and stack
   trace are never written. `state.before`/`state.after` map probe IDs to hashes in
   ascending ordinal key order; both are empty for outcomes that never executed
   (`Rejected`, cancelled before start). Terminal events of different interactions may
   legally interleave out of sequence order (a cancelled-while-queued request finishes
   before its predecessor).

4. **Secret substitution.** An argument whose **catalog schema** marks it sensitive is
   replaced by `{"$secret":"<name>@<version>/<argument>"}`. The catalog is the privacy
   floor: a target-side sensitivity upgrade (§13.3) is not visible at enqueue time
   because targets resolve after dequeue, so it never influences recording redaction. The
   key is deterministic; a per-occurrence lookup uses the enclosing request event's
   `requestId`, so the replayer's resolver contract is `(requestId, key)` with no schema
   change. Since v1 argument values are scalars, an object value is an unambiguous
   marker.

5. **Strict readers.** Unknown kinds, unknown or duplicate fields, numeric enum
   spellings, non-canonical hashes (not 64 lowercase hex), and unsupported
   `schemaVersion` values are rejected (§15.1 "reject … rather than guessing"). Any
   schema addition therefore bumps `schemaVersion`. The version gate runs before field
   validation so a future header rejects as an unsupported version, not an unknown field.

6. **Truncation recovery.** The trailing `\n` is the write-commit marker — an
   intentional deviation from generic JSON Lines, which does not require a final
   newline. A final byte run without it is discarded on read (even if it parses, the
   write cannot be proven complete) and reported with its byte count; every
   newline-terminated line must be fully valid, anywhere in the file. A request without
   a terminal event is surfaced as `OutcomeUnknown`, never coerced to `Faulted`.

7. **Durability and bounds.** Each line is one write followed by
   `FileStream.Flush(flushToDisk: true)` for file sinks (`Stream.Flush()` otherwise) —
   the strongest flush netstandard2.1 offers, not a hardware write-through guarantee. A
   configurable `MaxRecordingBytes` (default 64 MiB) is checked before each append.

8. **Failure semantics.** Recording is a guarantee, not best effort: any environmental
   append failure (sink I/O, size bound) poisons the recorder; later appends fail fast,
   new dispatches fail at enqueue, and already-queued work fails before its first stage.
   A terminal-append failure happens after side effects ran, so the dispatcher keeps the
   executed result in the idempotency cache (and satisfies concurrent waiters with it)
   before propagating — a retry with the same key must not repeat side effects. Recovery
   means constructing a new dispatcher and recorder.

9. **Confinement.** File recordings resolve against a caller-supplied artifact root:
   rooted relative paths are rejected, `..` segments are resolved by full-path
   normalization, and the result must remain under the root (ordinal prefix with a
   separator boundary). The check is lexical — junctions or reparse points below the
   root are not followed — and recordings carry no tamper detection; the threat model is
   a locally trusted artifact root. No default root is defined (§25 open item).

### Alternatives rejected

- **Per-request secret keys.** The enclosing request event already identifies the
  occurrence; per-request keys would only complicate replay provisioning.
- **Recording rejection/fault messages.** Free text is a leak surface; codes suffice for
  replay verification.
- **Best-effort recording** (log-and-continue on append failure). Violates the §15.1
  guarantee and this repository's fail-fast policy.
- **Appending request events outside the enqueue lock.** Breaks the sequence-order
  guarantee under concurrent enqueue.
- **Tolerant readers** (ignore unknown fields). Chosen strictness means additions are
  always a version bump; tolerated fields could silently drop data replay should verify.
- **`state` as an array of `{id, version, hash}`.** The probe version is already bound
  into the hash envelope (ADR 0001), so a version mismatch surfaces as hash divergence;
  the map mirrors `StateObservation`.
- **Richer headers** (canonicalization ID, probe manifest, schema digests) and a
  **`security_event` kind** (§19 authentication failures belong to the MCP host's audit
  surface, §23 item 9): candidates for a future version.
- **A single ordered writer with durable acknowledgement and batched fsync.** The MVP
  accepts one fsync per line inside the enqueue lock (recording is opt-in); the async
  writer is a schema-neutral upgrade to revisit if Unity frame latency demands it.

## Consequences

- **Positive.** The strict replayer (§16.1) needs no schema change: catalog lookup
  (`command.name`/`version`), secret resolution (`$secret` inventory), before/after hash
  comparison (per-probe maps), terminal status, fault code, stage progress, and rejection
  code are all present and typed. Crash recovery is a read-time rule, not a repair tool.
- **Replay preconditions.** Both built-in probes hash the session epoch and registry
  revision as runtime identity (ADR 0001). A replay harness must therefore reconstruct
  the registry with the recorded `sessionId` and re-register targets deterministically,
  or every before-hash comparison fails. Stages must not use `Sequence`, `RequestId`, or
  `Origin` in business logic — replay cannot reproduce them.
- **Negative.** Enqueue latency includes one line write plus flush while recording; a
  poisoned recorder halts all further dispatches on that dispatcher by design; strictness
  makes every schema addition a breaking version bump.
- **Open items.** Default artifact-root location, state-snapshot size limits (§25);
  probe-value redaction for low-entropy secrets (hash dictionary exposure) remains a
  probe-side concern outside this schema; hash-level (not property-level) state
  divergence is what recordings can support until snapshots are retained (§14.1).

## Implementation

- `InteractionRecording.cs` — clock abstraction, error codes,
  `InteractionRecordingException` (optionally carrying the executed result), secret-key
  grammar, schema constants.
- `InteractionRecorder.cs` — options, poisoning writer, catalog-floor redaction, file
  factory with artifact-root confinement.
- `InteractionRecordingReader.cs` — strict reader, truncation recovery, `OutcomeUnknown`
  pairing, secret-key inventory.
- `InteractionDispatcher` — request append inside the enqueue lock, terminal append on
  every terminal path, poison guard before stages, idempotency-cache preservation on
  terminal-append failure.
- `InteractionFaultException` — the public channel for stages to fault with a stable
  application code, so `faultCode` is populatable (§12.2).
