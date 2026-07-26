# ADR 0013 (v2): Performance Normativity and the Allocation Policy

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-27
> **Normative reference:** [../spec/performance.md](../spec/performance.md) ·
> [../spec/kernel-execution.md](../spec/kernel-execution.md) §6 ·
> [../spec/security-resources.md](../spec/security-resources.md) §5 ·
> [../spec/adapter-conformance.md](../spec/adapter-conformance.md) §7

## Context

The v2 specs normalized **time** (pump budgets, the worst-case occupancy formula)
and **retention** (every store bounded) but never **allocation**: the word does not
appear in the original set, and the implementation followed the spec's silence —
no pooling, no span paths, and per-pump work proportional to retained state. The
owner's mandate for the performance track ("Cysharp-quality performance and
allocation") required deciding *where* performance claims live. Measurement came
first: a benchmark suite and an exact per-thread allocation meter recorded the
pre-optimization baseline (`bench/v2/BASELINE.md`) — a quiescent pump allocating
280 B and degrading 2,550× with retained terminals, one revision advance with 256
armed waits costing 108 MB — before any optimization was designed, so the work
list was ranked by evidence, not intuition.

## Decision

- **Four claim layers, three artifacts.** Portable number-free guarantees
  (quiescence, proportionality) live in [spec/performance.md](../spec/performance.md);
  exact allocation counters gate as regression canaries in kernel model tests;
  measured numbers live in versioned `PerformanceConformanceProfile` documents;
  the engine-host tier stays explicitly unmeasured until the engine adapter
  exists. `ResourceProfile` keys remain what they were — bounds enforceable at an
  owning boundary with a refusal taxonomy — and never carry managed-allocation
  caps, which no boundary can pre-enforce.
- **Idle-zero is a canary, not the headline.** The user-visible cost lives on the
  busy path (materialization, encoding, admission); the first-class acceptance
  numbers are busy-path deltas against the baseline. The idle-zero and
  retained-state-equality gates exist because they are *exact* and catch
  structural regressions cheaply.
- **Gates land with their fixes.** A gate asserts only properties the code
  actually has: each performance fix ships the gate that makes its property
  permanent, and the consolidated quiescent-zero gate arrives with the final
  consolidation of the track. The suite is therefore always green on honest
  grounds — never aspirationally red, never retroactively binding.
- **Pooling and scratch rules.** Kernel-internal scratch is runtime-owned
  exclusive arrays reset by index, never long-lived `ArrayPool` rentals (a
  lifetime rental privatizes the shared pool); short-lived rentals return in
  `finally`. Scratch that held redacted-adjacent material is cleared before
  reuse ([security-resources.md](../spec/security-resources.md) §3). Retention
  high-water is bounded and trimmable: allocation-zero and resident-set-minimal
  are different goals, and arenas must not convert GC pressure into permanent
  RSS. Buffers never escape: everything handed to a caller is an owned,
  exact-sized, immutable value.
- **Sampled reads stay per-materialization.** The evaluation-read sharing
  introduced for armed waits applies only where a materialization is a pure
  function of the revision — a domain whose family exposes a sampled source
  keeps one fresh read per wait ([observation-state.md](../spec/observation-state.md)
  §7.1). The alternative semantics (sample once per pump, share the epoch) was
  considered and rejected: it would change observable behavior for a
  convenience the current model does not need.
- **`ValueList<T>`'s successor defaults to empty.** When the aggregate
  representation moves to a struct-backed `ValueArray<T>`, `default` is the
  empty list — indistinguishable from `Empty`. The characterization suite pins
  the remaining contract (defensive copy, null-element rejection,
  order-sensitive equality, dictionary-key behavior); "missing versus empty" is
  expressed by the surrounding type (nullable or a presence flag), never by a
  distinguished default sentinel.
- **The dependency axiom's scope is production code.** The
  PackageReference-zero rule of [adr 0007](0007-codec-and-package-boundaries.md)
  binds the shipped `src/v2` assemblies. Test hosts and the benchmark host are
  leaf executables outside every consumer's restore path; their tooling
  dependencies (NUnit, BenchmarkDotNet) are not violations.

## Consequences

- Performance work is falsifiable: every optimization PR cites the baseline row
  it moves and lands the gate that keeps it moved. The track's measured effect
  so far — quiescent pump 222 ns/280 B → 81 ns/56 B, idle at 4096 retained
  terminals 636 µs/714 KB → 82 ns/56 B, a 256-wait revision advance
  53.4 ms/108 MB → 543 µs/1.19 MB — is recorded as profile input, not as spec.
- The remaining structural costs (aggregate construction copies, codec staging)
  are the representation phase's work list, gated the same way.
- Kernel model tests gain a Release-configuration CI job; Debug builds cannot
  host exact-counter gates.

## Rejected alternatives

- **Raising the TFM / language version for performance.** Everything required
  (`Span<T>`, `ArrayPool<T>`, struct enumerators, `TryComputeHash`) exists on
  netstandard2.1 + C# 9; a TFM change would trade the Unity consumption story
  for nothing.
- **A structure-of-arrays rewrite of the observation model.** Discards a
  verified implementation and its spec-transcribed oracle suite for wins the
  targeted fixes achieve without a rewrite; the object model *is* the Contracts
  semantic surface.
- **Total-allocation-zero as a goal.** Owned immutable results are the product;
  zero would force pooling of caller-visible values and the lifetime bugs that
  come with it.
- **Managed-allocation caps in `ResourceProfile`.** No owning boundary can
  refuse an allocation before it happens; a cap that cannot be enforced at a
  boundary with a structured refusal is not a resource bound, it is a wish.
- **A blanket "no O(retained) work per pump" rule.** Overbroad: checkpoint
  feeds, fence drains, and simultaneous expiry legitimately touch what is due.
  Proportionality-to-due-work is the enforceable form.
- **Aspirational gates (asserting idle-zero before the fixes).** A permanently
  red suite trains everyone to ignore it; gates that land with their fixes stay
  meaningful.
