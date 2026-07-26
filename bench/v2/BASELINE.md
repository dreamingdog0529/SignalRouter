# v2 performance baseline — before the performance track

The pre-optimization record the performance-track PRs (algorithm fixes,
representation rework) are measured against. Every later change reports its
effect as a delta from these tables.

**These numbers are non-normative.** They describe one machine, one runtime,
one build, at one commit — they are not a promise, a budget, or a conformance
surface. The normative performance guarantees land with `spec/performance.md`
(plan P2) and are deliberately number-free; measured numbers belong to a
`PerformanceConformanceProfile`, and this file is merely the first recorded
input to it.

## Provenance

| | |
|---|---|
| Commit | `c7a8ead` (main after PR #48) + the P0b measurement harness |
| Date | 2026-07-27 |
| Machine | Intel Core i9-9900K (Coffee Lake, 8C/16T), Windows 11 25H2 |
| Runtime | .NET 10.0.10, X64 RyuJIT x86-64-v3, Concurrent Workstation GC |
| Tool | BenchmarkDotNet v0.15.8, DefaultJob, MemoryDiagnoser |
| Command | `dotnet run -c Release --project bench/v2/SignalRouter.V2.Benchmarks -- --filter '*'` |

**Scope caveats.**
- Managed allocations on CoreCLR only. Mono/IL2CPP behavior, native
  allocations, and Unity engine objects are outside every number here; the
  Unity-host tier (plan L3) is **unmeasured** until Adapter.Unity exists.
- The bench world is flat (no parent hierarchy), all nodes visible, two
  attributes and one capability per node, one revision-bound source published
  once at setup; interactions terminate by executor refusal (zero-effect).
  Hierarchy-depth cost and effect/completion choreography are not exercised
  yet.
- Setup settles the initial checkpoint and verifies one **complete**
  (untruncated) snapshot at the advertised node count before anything is
  measured — an earlier draft of this baseline measured a budget-truncated
  2048-node projection with per-pump checkpoint retries and overstated that
  row by ~2× (caught in review; the world now raises the materialization
  ceilings and verification would throw).
- `Allocated` is bytes per single operation (1 KB = 1024 B).

## Idle pump — quiescent cost vs. retained state

`IdlePumpBenchmarks`: one `Pump()` with an empty mailbox and no due work.

| RetainedTerminals | Mean | Allocated |
|---:|---:|---:|
| 0 | 222.3 ns | 280 B |
| 4096 | 636.0 µs | 714,103 B |

The quiescent pump is **not** zero-allocation (280 B: `PumpReport` plus status
publication machinery), and its cost scales with retained terminals —
**~2,860× slower and ~2,550× more allocation** at the default terminal
capacity, with Gen2 collections. This is findings A1/A4 (unconditional
`PublishStatus` dictionary rebuild + full `ExpireTerminals` scan per pump)
measured. Plan targets: idle-zero (L1 canary) and retained-state independence.

## Submit → zero-effect terminal

`SubmitToTerminalBenchmarks`: admission, canonicalization, state machine,
terminal, status publication; fresh settled world per iteration, 512
ops/iteration (the RecoveryIndex grows 0→512 within an iteration, so this
number includes a mild accumulation component).

| Mean | Allocated |
|---:|---:|
| 150.1 µs | 270.38 KB |

A quarter megabyte to refuse one interaction. Includes per-admission
`SHA256.Create()`/HMAC churn (B5) and the growing `PublishStatus` rebuild (A1).

## Snapshot pin + release

`SnapshotBenchmarks`: request one revision-consistent snapshot, pump to
delivery, release the pin, pump. Setup verifies the snapshot is complete at
the advertised node count. The busy-path headline (acceptance criterion 1).

| Nodes | Codec | Mean | Allocated |
|---:|---|---:|---:|
| 64 | none | 49.6 µs | 109.94 KB |
| 64 | canonical v1 | 81.1 µs | 152.52 KB |
| 512 | none | 462.9 µs | 858.13 KB |
| 512 | canonical v1 | 783.4 µs | 1,198.31 KB |
| 2048 | none | 2.625 ms | 3,435.25 KB |
| 2048 | canonical v1 | 4.805 ms | 4,715.60 KB |

**~1.7 KB of allocation per materialized node before encoding** (the
multi-copy `ValueList` construction chain, findings B1/B4), linear in node
count. The with-codec delta tracks the direct encode cost below (at 2048
nodes: 1,280 KB vs 1,360 KB direct) — the codec's own staging and copy chain
(B5) dominates that delta, and Gen2 collections appear at 2048 nodes.

## Canonical-state encode (codec alone, no kernel)

`CanonicalEncodeBenchmarks` (acceptance criterion 2):

| Nodes | Mean | Allocated |
|---:|---:|---:|
| 64 | 28.58 µs | 42.58 KB |
| 512 | 203.85 µs | 340.16 KB |
| 2048 | 1.180 ms | 1,360.30 KB |

Per-string intermediate `byte[]`s in `WriteString` and the `List<byte>`
staging buffer (D7 targets: pooled staging + span writes; the exact-sized
result array is the only intended allocation).

## Wait reevaluation on revision advance

`WaitReevaluationBenchmarks`: one source publication (unchanged content, so
StateStore fully dedupes) + pump, with armed never-satisfied waits; 256-node
world (acceptance criterion 3).

| ArmedWaits | Mean | Allocated |
|---:|---:|---:|
| 1 | 717.6 µs | 1.07 MB |
| 256 | 53.380 ms | 108.08 MB |

**One revision advance with 256 armed waits costs 53 ms and 108 MB** —
finding A3 measured: every armed wait triggers a full node-store
materialization after the turn. Plan target: one revision-bound
materialization per domain, sampled overlay regenerated per read (D4) — the
allocation should drop by roughly the wait count.

## Cross-validation — AllocationMeter (exact per-thread counter)

`tests/v2/SignalRouter.V2.Performance.Tests` measures the same world with
`GC.GetAllocatedBytesForCurrentThread` on a dedicated thread (Release, this
machine). Agreement with MemoryDiagnoser confirms both instruments:

| Operation | AllocationMeter | MemoryDiagnoser |
|---|---:|---:|
| Idle pump (0 terminals) | 280 B/op | 280 B/op |
| Idle pump (1024 terminals) | 168,208 B/op | — (BDN row is 4096: 714,103 B) |
| Submit → terminal | 206,487 B/op¹ | 276,869 B/op¹ |
| Snapshot 512 nodes + codec | 1,227,235 B/op | 1,227,069 B/op |

¹ Both include RecoveryIndex accumulation during measurement, at different
retained counts (128 vs 512 ops per window) — the growth term differs, the
shape agrees.

## What these numbers already prove

1. **Idle is not zero** (280 B) — B3 (`PumpReport` per pump) plus A1.
2. **Idle cost is O(retained)** (280 B → 714 KB) — A1 + A4.
3. **Wait reevaluation is O(waits × nodes)** (1.07 MB → 108 MB) — A3.
4. **Materialization allocates ~1.7 KB/node before encoding** — B1/B4.
5. **A with-codec snapshot pays approximately one full encode on top** — B5.

The P1 (algorithm) and P3/P4 (representation/codec) PRs each cite the rows
they claim to move.
