# ADR 0006: Strict replay semantics v1

> **Status:** Accepted
> **Date:** 2026-07-21
> **Deciders:** SignalRouter maintainers
> **Builds on:** [ADR 0001](0001-canonical-state-hashing.md) (canonical state hashing),
> [ADR 0005](0005-recording-schema-v1.md) (recording schema v1)

## Context

Design §16.1 lists the seven strict-replay verification steps and requires stopping at
the first divergence with a structured report, but leaves the failure semantics
under-specified: what is a divergence versus an error, what the report may contain, how
recorded cancellations and continuations replay, and what the caller must provide. These
choices define public failure semantics, so §25 requires an ADR. The recording schema is
unchanged — ADR 0005 already guarantees every replay input is present and typed.

## Decision

`InteractionReplayer.ReplayAsync(recording, dispatcher, secretResolver?, token)` replays
one entry at a time through the normal dispatcher with `Origin.Replay` and no correlation
or idempotency key, verifying the §16.1 steps in order and returning an
`InteractionReplayReport`.

1. **Divergence versus exception.** A *divergence* is trustworthy information that the
   current build differs from the recording: a missing `name@version`, recorded arguments
   that no longer fit the catalog schema, an unresolvable secret, undecodable arguments,
   a state-hash mismatch, or a status/rejection-code/fault-code/stage mismatch. Replay
   stops at the first one and reports it. An *exception*
   (`InteractionReplayException` with a stable error code) means replay could not obtain
   a trustworthy comparison: caller misconfiguration (session-epoch mismatch, attached
   recorder, busy dispatcher, stage-originated reentrancy, missing resolver) or a broken
   resolver contract. Caller cancellation throws `OperationCanceledException` — checked
   before each entry and immediately after every awaited dispatch regardless of the
   returned status, so a cancellation-ignoring stage cannot masquerade as a divergence.

2. **Sanitized reporting.** The report never carries `InteractionResult` (its `FaultInfo`
   holds exception type, message, and stack trace — §12.2/§19) nor recorded argument
   payloads (`ArgumentsJson` may hold plaintext for an argument whose sensitivity has
   since been upgraded, §13.3). Entries are identified by
   `InteractionReplayEntryRef` (sequence, request ID, command identity, target); actual
   outcomes are projected through `RecordedOutcome` — exactly the fields a recording may
   persist; state differences are per-probe hash pairs with nullable sides so missing and
   extra probes are representable. Divergence reports name offending arguments and secret
   keys, never values.

3. **Per-status comparison scope.** `Succeeded`: status and state hashes only — §16.1
   scopes stage comparison to faulted results, so pipelines may restructure their stages
   without diverging as long as observable behavior matches. `Faulted`: null-sensitive
   fault-code equality, then full stage-array equality (equivalent to §12.2's completed
   stage IDs plus failed stage), then state hashes. `Rejected`: rejection-code equality
   plus a fresh probe bracket (one pinned reading before dispatch, `ReadSame()` after)
   verifying **zero probe-observable state change** — the strongest zero-side-effect
   claim recordings support. State differences are hash-level only until snapshots are
   retained (§14.1, ADR 0005 open item).

4. **Recorded cancellations.** A pre-start cancellation (no stages) is replayed
   deterministically with a synthetic already-cancelled token — never the caller's — and
   fully verified (status, empty stages, zero-side-effect bracket). A mid-execution
   cancellation (stages present) is a known terminal outcome whose timing and partial
   effects are unreproducible; replay stops **before** it with
   `Stopped`/`CancelledDuringExecution` instead of manufacturing a guaranteed false
   divergence.

5. **`OutcomeUnknown` is stop-only.** Replay stops before an unpaired request
   (`Stopped`/`OutcomeUnknown`). §15.1's "explicit recovery policy" is intentionally
   undefined in v1; a future policy is an additive overload, not a breaking change.

6. **Continuations are suppressed.** Schema v1 carries no parent linkage, so a live
   continuation cannot be matched against recorded entries. While the replay lease is
   held the dispatcher counts drained continuations instead of scheduling them; the
   requesting entry is fully verified, then replay stops with
   `Stopped`/`ContinuationRequested`. Recordings containing continuations are therefore
   un-replayable past the first request in v1; true continuation replay needs a recorded
   parent linkage (future schema version).

7. **Exclusive replay lease.** Replay acquires an internal lease on the dispatcher under
   its enqueue lock. Acquisition requires genuine idleness: an active-dispatch counter
   spans each dispatch through its continuation drain (the queue tail alone is released
   before continuations are posted), and a pending count covers the posted-but-not-yet-
   enqueued window. While held, non-replay dispatches and concurrent replays are
   rejected; the lease is released (and all suppression state cleared) on every exit
   path. This makes the replayer's pre-dispatch before-state check TOCTOU-safe: nothing
   else can run between the reading and the dispatch. A recorder-free dispatcher is
   required — a recorder would append events for every replayed entry and mix recording
   failure semantics into verification.

8. **Preconditions.** The registry must be reconstructed with the recording's session ID
   (checked directly; deterministic target re-registration is enforced indirectly because
   built-in probe hashes encode epoch and revision — drift surfaces as the first
   before-state mismatch, per ADR 0005). Secrets resolve per entry in replay order via
   `IInteractionSecretResolver.TryResolve(requestId, key)`; there is no global preflight,
   so an earlier divergence or stop always wins over a later `SecretResolverMissing`.
   Resolver values are `InteractionValue` scalars (`Number` is `decimal`); recorded
   plaintext scalars pass through byte-for-byte, so only resolver-supplied secrets are
   subject to the decimal range.

9. **Recorded-argument audit.** Before decoding, every recorded property is audited
   against the current catalog schema: unknown arguments, missing required arguments,
   wrong scalar kinds, secret markers for non-sensitive arguments, plaintext for
   now-sensitive arguments, and wrong-kind resolved secrets are all
   `ArgumentSchemaMismatch` divergences. Codecs may be permissive; the audit is not.

### Alternatives rejected

- **Reporting raw results or arguments.** Leaks exception text and possibly plaintext
  (§19); the sanitized projection loses nothing replay is allowed to compare.
- **Replaying mid-execution cancellations.** Cancellation timing is not recorded;
  any dispatch would diverge or, worse, silently execute further side effects.
- **Executing or matching continuations.** Without parent linkage, matching is
  guesswork and a live continuation double-executes side effects nondeterministically.
- **A recovery-policy parameter now.** No policy has a specification; an unused enum
  would freeze a guess into the public surface.
- **Stage-array comparison for `Succeeded`.** Would make pipeline layout part of
  `name@version` compatibility, which §16.1 does not require; state hashes already
  guard observable behavior.
- **A plain busy check instead of a lease.** Not atomic: a dispatch can enqueue after
  the check, and the queue tail is released before continuations are posted, so
  "idle" without the counters is unprovable.

## Consequences

- **Positive.** §22 acceptance criteria 5 and 6 are directly testable; divergence
  reports are safe to forward over MCP; a poisoned or busy runtime is rejected up front
  instead of producing misleading divergences.
- **Negative.** Replay claims a whole dispatcher exclusively; recordings that use
  continuations verify only their first chain link in v1; zero-side-effect verification
  is only as strong as the registered probes.
- **Open items.** Recovery policies for `OutcomeUnknown`; continuation linkage in a
  future schema version; adaptive replay (§16.2) builds on this contract.

## Implementation

- `InteractionReplay.cs` — report/divergence/stop models, `IInteractionSecretResolver`,
  `InteractionReplayException` and error codes.
- `InteractionReplayer.cs` — the verification loop, argument audit and secret
  substitution, probe comparison and brackets.
- `InteractionDispatcher` — internal replay lease (idle acquisition, external-dispatch
  rejection, continuation suppression), active-dispatch and pending-continuation
  counters.
- `RecordedOutcome.FromResult` — the sanitized actual-outcome projection.
