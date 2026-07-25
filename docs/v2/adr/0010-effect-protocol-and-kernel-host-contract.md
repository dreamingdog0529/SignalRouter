# ADR 0010 (v2): Effect Protocol, Evidence Gating, and the Kernel–Host Contract

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-26
> **Normative reference:** [../spec/kernel-execution.md](../spec/kernel-execution.md) §2–§6 ·
> [../spec/adapter-conformance.md](../spec/adapter-conformance.md) §2–§4 ·
> [../spec/guarantees.md](../spec/guarantees.md) §3.5, §5.3 ·
> [../spec/security-resources.md](../spec/security-resources.md) §5

## Context

Implementation planning for the kernel module surfaced points where the execution
specs stated obligations without the mechanics to honor them: the E3 permit is a
durability *gate* but no runtime object represented it outside a recording; "the
mutation lane is held until the effect fence" conflicted with `afterRequestId`'s
promise to exclude subsequent mutations; `AdapterAcknowledged` — evidence for effects
the engine *cannot fence* — cannot simultaneously stand in for a fence; "the kernel
never installs its own thread or timer" was being over-read as "never reads a clock",
which makes the pump deadline unenforceable; synchronous duplicate-`AuthorKey` failure
collided with "all inputs are mailbox messages"; typed argument values had no path to
the executor; and every resource bound had a dimension but no default. This ADR fixes
the seven coupled decisions; the amended spec text is normative, this record explains
why and what was rejected.

## Decision

1. **Effect permits are runtime tokens gated by evidence readiness.** The kernel mints
   an opaque, single-use `EffectPermitToken` for every permitted effect, in recording
   and non-recording modes alike, and issues it only after its evidence obligation
   reports ready. Internally this is an *evidence coordinator* seam
   (`prepare admission evidence` / `prepare effect permit` / `commit terminal
   evidence`, answering ready, pending, or fault): with no recording active the
   coordinator is an explicit no-op that answers ready immediately; the recording
   module (implementation item 5) supplies the durable coordinator whose
   permit-preparation *is* E3 durability ([guarantees.md](../spec/guarantees.md)
   §5.3), whose preparation fault *is* the pre-effect evidence failure
   (`Faulted(EvidenceUnavailable)`), and whose terminal-commit fault fails the
   recording alone while the true terminal is preserved in the `RecoveryIndex`
   (the §7 failure matrix). No placeholder `ContentId`s are ever fabricated.
2. **Fence and completion are two signals; completion implies the fence only where the
   profile proves it.** The executor reports `EffectFenceReached(permit)` — the point
   after which the effect can no longer mutate application state — and, separately,
   `EffectCompletion(permit, resolution)`. A completion implies an unreported fence
   only for profiles whose evidence semantics entail "no further mutation after
   completion" (`Applied@1`, `FrameCommitted@1`, `PostconditionSatisfied@1`).
   `AdapterAcknowledged@1` is excluded: it exists precisely for effects the engine
   cannot fence, so an acknowledgment can never release the single-mutation
   invariant. Consequently a **mutating** capability MUST NOT be bound to
   `AdapterAcknowledged@1` unless the adapter can still report a genuine fence;
   an effect that can provide neither is not a conformant `ManagedIntent` mutation
   and belongs to `ObservedExternal`. The permit-token state table (issued → adopted →
   fenced → completed, with refusal, duplicate, unknown-token, and stale-incarnation
   transitions) is normative in
   [adapter-conformance.md](../spec/adapter-conformance.md) §3. The guarantee this
   protocol makes is **at-most-once dispatch within an incarnation plus exactly-once
   completion messaging** — never effect-exactly-once across crashes
   ([guarantees.md](../spec/guarantees.md) §6.1).
3. **The mutation lane is released when the after basis is pinned, not at the fence.**
   If the next mutation could start at the fence, the previous interaction's after
   observation and postcondition could absorb the successor's effects, breaking
   `afterRequestId`'s exclusion promise ([verification.md](../spec/verification.md)
   §3.2). The kernel therefore holds the lane through `Observing` until the
   after-observation basis is pinned; only post-basis work (postcondition watches,
   waiters) continues on the control lane.
4. **The kernel reads no ambient clock; it reads two host-supplied ones.** Semantic
   time (wait timeouts, retention expiry) advances only with the logical `now` the
   host passes per pump, resolving at pump boundaries. Deadline enforcement reads a
   host-injected monotonic clock at step boundaries — without it `Pump(…, deadline)`
   is unenforceable. Monotonicity violations fail fast. The adapter's synchronous
   execution-time bound stays **normative** (it underwrites control-lane
   responsiveness and the worst-case pump occupancy of
   [kernel-execution.md](../spec/kernel-execution.md) §6); the TCK enforces its
   logical form (synchronous return, declared frame counts) and tier 3 measures the
   wall-clock form. The timing non-goal of [guarantees.md](../spec/guarantees.md) §4
   is about replay reproduction, not about abandoning pump responsiveness.
5. **The kernel depends on the AdapterSdk shape assembly.** `AdapterSdk` carries only
   interfaces and message/descriptor shapes over Contracts (zero behavior); the
   kernel references it to call `IEffectExecutor` and to hand adapters its
   counterpart sinks. [architecture.md](../architecture.md) §2 gains this one edge.
6. **Registration is bootstrap-then-messages.** Initial construction happens through a
   synchronous builder before the runtime starts (duplicate `AuthorKey` throws).
   After start, registration/unregistration/attribute updates are bounded control-lane
   messages answered with a receipt (duplicate `AuthorKey` fails in the receipt,
   still "immediately" in the sense of [semantic-model.md](../spec/semantic-model.md)
   §3.2 — before any subsequent message). Status queries never enter the mailbox:
   the owner atomically publishes an immutable status snapshot that readers consult,
   and queries carry the querying principal — a `RequestId` outside the principal's
   authority answers exactly as an unknown id, extending existence concealment
   ([guarantees.md](../spec/guarantees.md) §3.5) to the query path.
7. **Typed argument values travel as an ephemeral payload; the kernel owns the
   fingerprint.** A submission carries the typed `InvocationPayload` alongside the
   recording-safe invocation summary. The payload lives in memory only, is never
   stored in the mailbox's retained structures, `RecoveryIndex`, trace, or events,
   and its lifetime ends at adoption refusal, terminal, or cancellation
   ([security-resources.md](../spec/security-resources.md) §3). The kernel derives
   the authoritative semantic fingerprint and argument digest from the canonicalized
   payload; a caller-supplied fingerprint is verified, never trusted — otherwise
   identical fingerprints could smuggle different payloads through dedup.
8. **Bounds are a versioned resource profile.** Defaults live in a named profile
   (`default@1`, [security-resources.md](../spec/security-resources.md) §5); the TCK
   verifies that *configured* bounds are enforced, not that specific numbers are in
   force, so tuning a default is a profile revision, not a conformance change.

## Alternatives considered

- **Fire-and-forget event notification as the recording seam:** rejected — E3 is a
  durability gate, not a notification; a sink API cannot express waiting for
  durability, `EvidenceUnavailable`, the E4-fault-vs-terminal split, or gating child
  continuations on parent terminal durability.
- **Completion always implies the fence:** rejected — an `AdapterAcknowledged`
  completion would release the mutation lane while the engine may still mutate,
  breaking the single-active-mutation invariant.
- **Releasing the mutation lane at the fence:** rejected for now — it requires
  pinning an immutable after basis at fence time and restricting postcondition
  evaluation to causally attributable updates; the simple rule is safe and can be
  relaxed later without breaking recorded artifacts.
- **A fully clockless kernel:** rejected — the pump deadline and monotonic timeout
  ordering are unenforceable on a fixed per-pump timestamp.
- **Synchronous owner-thread registration API:** rejected — it adds a second input
  path beside the mailbox and forces rules for out-of-pump calls, reentrancy from
  effects, and ordering against queued messages; bootstrap-plus-messages keeps the
  single linearization point intact.
- **A separate `Runtime.Abstractions` module, or kernel-owned port interfaces:**
  rejected — a new module adds packaging surface against ADR 0007's
  restore-burden principle, and kernel-owned ports would invert the spec's
  assignment of the SDK interfaces to `AdapterSdk`; the one documented edge is
  cheaper than either.
- **Advisory-only synchronous execution bound:** rejected — it would dissolve the
  basis of the control-lane responsiveness claim and the computable worst-case pump
  occupancy.

## Consequences

- The interaction state machine is identical with and without an active recording;
  recording attaches by swapping the no-op coordinator for a durable one, never by
  changing kernel control flow.
- Adapters get one unambiguous token lifecycle to implement and the TCK can drive it
  black-box, including the malformed transitions.
- Human-visible cost: mutation throughput is bounded by after-basis pinning, one
  interaction at a time — accepted; the lane was already single-flight.
- The kernel's two-clock model makes tier-1 tests fully deterministic (both clocks
  are test inputs) while keeping the deadline promise real.
