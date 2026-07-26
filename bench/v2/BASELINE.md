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
  attributes and one capability per node; interactions terminate by executor
  refusal (zero-effect). Hierarchy-depth cost and effect/completion
  choreography are not exercised yet.
- `Allocated` is bytes per single operation (1 KB = 1024 B).

## Idle pump — quiescent cost vs. retained state

`IdlePumpBenchmarks`: one `Pump()` with an empty mailbox and no due work.

| RetainedTerminals | Mean | Allocated |
|---:|---:|---:|
| 0 | 221.0 ns | 280 B |
| 4096 | 604.2 µs | 714,103 B |

The quiescent pump is **not** zero-allocation (280 B: `PumpReport` plus status
publication machinery), and its cost scales with retained terminals —
**2,700× slower and 2,550× more allocation** at the default terminal capacity,
with Gen2 collections. This is findings A1/A4 (unconditional `PublishStatus`
dictionary rebuild + full `ExpireTerminals` scan per pump) measured. Plan
targets: idle-zero (L1 canary) and retained-state independence.

## Submit → zero-effect terminal

`SubmitToTerminalBenchmarks`: admission, canonicalization, state machine,
terminal, status publication; fresh world per iteration, 512 ops/iteration
(the RecoveryIndex grows 0→512 within an iteration, so this number includes a
mild accumulation component).

| Mean | Allocated |
|---:|---:|
| 165.7 µs | 272.91 KB |

A quarter megabyte to refuse one interaction. Includes per-admission
`SHA256.Create()`/HMAC churn (B5) and the growing `PublishStatus` rebuild (A1).

## Snapshot pin + release

`SnapshotBenchmarks`: request one revision-consistent snapshot, pump to
delivery, release the pin, pump. The busy-path headline (acceptance
criterion 1).

| Nodes | Codec | Mean | Allocated |
|---:|---|---:|---:|
| 64 | none | 49.5 µs | 109.58 KB |
| 64 | canonical v1 | 82.7 µs | 152.32 KB |
| 512 | none | 452.3 µs | 857.77 KB |
| 512 | canonical v1 | 800.4 µs | 1,178.11 KB |
| 2048 | none | 1.594 ms | 2,308.85 KB |
| 2048 | canonical v1 | 11.124 ms | 9,569.02 KB |

~1.7 KB of allocation per materialized node before encoding (the multi-copy
`ValueList` construction chain, findings B1/B4). At 2048 nodes with the codec
the operation allocates **9.6 MB and triggers Gen2** — the with-codec delta
(7,260 KB) is ~5.3× the direct encode cost below; the statically visible extra
copies are the `PayloadWriter.ToArray` + `CanonicalStateResult` defensive-copy
chain (finding B5, `Contracts/Observation/CanonicalState.cs:36-51`) plus the
StateStore lease path.

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
| 1 | 691.2 µs | 1.07 MB |
| 256 | 52.649 ms | 108.08 MB |

**One revision advance with 256 armed waits costs 52.6 ms and 108 MB** —
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
| Submit → terminal | 206,256 B/op¹ | 279,459 B/op¹ |
| Snapshot 512 nodes + codec | 1,227,003 B/op | 1,206,384 B/op |

¹ Both include RecoveryIndex accumulation during measurement, at different
retained counts (128 vs 512 ops per window) — the growth term differs, the
shape agrees.

## What these numbers already prove

1. **Idle is not zero** (280 B) — B3 (`PumpReport` per pump) plus A1.
2. **Idle cost is O(retained)** (280 B → 714 KB) — A1 + A4.
3. **Wait reevaluation is O(waits × nodes)** (1.07 MB → 108 MB) — A3.
4. **Materialization allocates ~1.7 KB/node before encoding** — B1/B4.
5. **The with-codec snapshot pays multiples of the direct encode** — B5.

The P1 (algorithm) and P3/P4 (representation/codec) PRs each cite the rows
they claim to move.
