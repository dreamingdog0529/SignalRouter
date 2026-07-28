# SignalRouter v2 — Philosophy

> **Status:** v2 design draft
> **Audience:** everyone; this is the stable "why" behind the v2 design
> **Normative rules live in [spec/](spec/)**; this document contains no class names,
> wire fields, or default capacities.

## 1. Mission

Make an application's user interface **observable and controllable as data**: represent
UI operations as structured, replayable commands; expose what can be seen and done as a
semantic projection; and give humans, tests, replays, and AI agents the same
application-level behavior — so failures reproduce deterministically and agents drive
software without pixels.

## 2. Axioms

Fixed by the project owner for v2; everything else in the design serves them:

1. **The mission stands** — semantic UI projection, structured commands, deterministic
   record/replay, MCP agent control.
2. **The core is engine-agnostic.** Unity is the first adapter, not the definition of
   the product.
3. **The core is dependency-free.** Kernel and contracts depend on the BCL only;
   serializers live in leaf packages; no third-party bus.

## 3. Observation is projection, not truth

A semantic tree is not the application's state; it is an **observation** — produced
under a versioned view contract, over a scope, with per-region completeness, at a
revision. Different consumers (agents, recordings, diffs) get different projections of
the same underlying store, and no consumer may assume its view is total, always fresh,
or gap-free: deltas are sequence-checked, gaps are detected, and resynchronization from
an authoritative snapshot is a normal event, not an error. This is the accumulated
lesson of the accessibility world (UIA's multiple views, AT-SPI's cache-and-signal
model) applied deliberately, rather than rediscovered incident by incident.

## 4. Operations target capabilities, not roles

What a node *is* (its role) and what can be *done* to it (its declared capabilities)
are separate facts. Commands dispatch against declared, versioned capability
contracts — never against an inference like "buttons are clickable". Each capability
also declares how its completion is evidenced, so "done" means the same thing on every
engine that hosts it.

## 5. Determinism is structural, and honest about its tier

v2 obtains ordering from structure — a single-owner kernel with one linearization
point — rather than defending it with counters and locks. And it names exactly what it
promises, in three tiers:

- **serializable** — a total order of mutations exists at execution time: guaranteed;
- **replayable** — recorded evidence suffices to re-execute and compare: guaranteed;
- **timing-deterministic** — frames, wall clock, delta batching, cancellation timing:
  **a non-goal**, and nothing in the system may quietly depend on it.

## 6. Honest uncertainty

When the system cannot prove what happened, it says so: `OutcomeUnknown`,
`Incomparable`, `Incomplete`, `Interrupted` are first-class answers, never converted
into invented successes or failures. Recording loss is never silent; refusing new work
is preferred to shedding accepted work; and a crash leaves behind either evidence or an
honest gap — never a guess.

## 7. Equivalence, precisely scoped

The v1 intuition "human input and agent input take the same path" survives, with an
honest boundary:

> Given the same principal authority, preconditions, and capability invocation, the
> semantic effect path and observation contract after admission are independent of how
> the invocation entered the system.

Human input the adapter can capture before its effect (**managed intent**) is admitted
like any other invocation. Effects that cannot be captured (**observed external**) are
recorded as observations, marked as contamination where they intersect controlled work,
and never dressed up as replayable input. Equivalence is a claim about managed semantic
intent — not about physical event streams.

## 8. What a verified replay means

A strict replay that passes proves **observational equivalence relative to a pinned
comparison profile**: the same admitted invocations, in the same order, produced
observations and terminal evidence that compare exactly equal under versioned, typed
rules. It does not prove the application did everything else identically — unobserved
side effects (network, audio, persistence) are outside the claim, and the design never
calls this "application equivalence".

## 9. Dependency and adapter philosophy

- The kernel owns semantics; engines own pixels, threads, and frames. Everything
  engine-specific lives behind a small SDK, and every engine constraint (language
  level, bundled libraries, packaging) is quarantined in that engine's adapter package.
- Serialization is a leaf concern: independent codec packages own the only serializer
  dependencies, because wire, recording, and canonical-state formats change for
  different reasons.
- A support claim is an evidence claim: an adapter is "supported" only when the shared
  conformance kit passes on the real engine in CI. Anything less is labeled
  experimental, out loud.

## 10. Security posture

External control of a UI is a privileged capability. The default boundary is one user
on one machine: local-only channels, per-instance secrets, owner-only rendezvous,
explicit exposure of every node and capability, bounded everything, redaction at the
moment values are produced, and release builds closed by default. Artifacts that drive
replay are untrusted input until verified. Uncertainty here too is honest: what the
boundary does not defend is written down, not implied.

## 11. Non-goals

- Timing determinism (§5).
- Crash-spanning exactly-once effects: admission is deduplicated within an incarnation
  and retention window; effects on a live UI cannot be transactionally undone, and the
  design does not pretend otherwise.
- Remote network control in the default profile.
- Pixel-based recognition or coordinate fallback.
- Wrapping or emulating existing UI automation frameworks.
- Automatic rollback of partial effects.
- Compatibility with v1 artifacts, protocol, or API surface (v2 is a clean slate; v1
  remains governed by its own documents).

## 12. Relationship to the v1 design

v1 ([docs/design.md](../design.md), ADRs 0001–0008) proved the mission and taught the
lessons v2 is built on: where a single dispatcher concentrates too much, where
retrofitted recording control breeds leases and epochs, where a hash is too opaque to
diagnose, and where "the same path for everyone" needed a sharper sentence. v2 restates
the philosophy so those lessons are structural assumptions, not patches.
