# PerformanceConformanceProfile — `default@1.0`

The measurement record of the completed performance track and the first
released instance of the profile schema of
[`docs/spec/performance.md`](../../docs/spec/performance.md) §4. Rows
marked **L1** are gated in CI by exact allocation counters; **L2** rows are
informational measurements. The pre-optimization record is
[`BASELINE.md`](BASELINE.md); every row below cites its baseline counterpart.

## Identity

| | |
|---|---|
| Profile | `default@1.0` |
| Measured commit | `2c51fa9cd04984568041af9cbfd1feebb162e012` (main at the end of the performance track, after the codec staging PR #62) |
| Measured on | 2026-07-28 |
| Machine | Intel Core i9-9900K (Coffee Lake, 8C/16T), Windows 11 25H2 |
| Runtime | .NET 10.0.10, X64 RyuJIT x86-64-v3, Concurrent Workstation GC, no PGO pinning |
| Build | Release |
| ResourceProfile in force | `default@1` ([security-resources.md](../../docs/spec/security-resources.md) §5.1), bench world overrides: observation/materialization ceilings 8 MiB / 4096 nodes |
| Tools | BenchmarkDotNet 0.15.8 + MemoryDiagnoser (timings, L2); `AllocationMeter` exact per-thread counters (L1) |
| Command | `dotnet run -c Release --project bench/SignalRouter.Benchmarks -- --filter '*'` (one invocation covering all 15 benchmarks; BenchmarkDotNet runs each case in its own workload process) |

**Scope note — identifier representation.** The conditional kernel-internal
handle phase ([ADR 0014](../../docs/adr/0014-two-layer-identifier-representation.md))
was **not implemented**: its evidence bar required profiles showing identifier
comparison/lookup cost as a significant factor, and no measurement in this
profile isolates identifier cost as a leading factor — the dominant measured
costs are materialization and encoding, and the identifier-heavy lookup row
gates at zero allocation. Absence of that evidence leaves the bar unmet;
Contracts identifiers therefore remain string-backed throughout.

## Rows

Allocation values are MemoryDiagnoser per-operation figures as reported
(1 KB = 1024 B), except **L1** rows, which are exact
`GC.GetAllocatedBytesForCurrentThread` counter values.

| Operation | Workload | Time (L2) | Allocated (L1/L2) | Baseline row |
|---|---|---:|---:|---:|
| Idle pump | 0 retained terminals | 75.81 ns | **L1: exactly 0 B** | 222.3 ns / 280 B |
| Idle pump | 4096 retained terminals | 74.85 ns | **L1: exactly 0 B + exact equality with the 0-terminal row** | 636.0 µs / 714,103 B |
| Trace-ring emit at capacity | 8192-entry ring | 46.52 ns | **L1: exactly 0 B** | 43.15 µs / 65,720 B |
| Materialization lookups (six shapes) | 512-node snapshot + completeness walk | — | **L1: exactly 0 B** | split per access |
| Node construction from canonical input | 2 attributes + 1 capability | — | **L1: exactly 72 B** (the aggregate object) | ~5 copies of the data |
| Submit → zero-effect terminal | 64-node world | 74.60 µs | 111.15 KB | 150.1 µs / 270.38 KB |
| Snapshot pin + release | 64 nodes | 14.27 µs | 20.77 KB | 49.6 µs / 109.94 KB |
| Snapshot pin + release | 64 nodes, codec | 32.55 µs | 29.79 KB | 81.1 µs / 152.52 KB |
| Snapshot pin + release | 512 nodes | 132.96 µs | 160.77 KB | 462.9 µs / 858.13 KB |
| Snapshot pin + release | 512 nodes, codec | 264.83 µs | 229.33 KB | 783.4 µs / 1,198.31 KB |
| Snapshot pin + release | 2048 nodes | 643.16 µs | 640.76 KB | 2.625 ms / 3,435.25 KB |
| Snapshot pin + release | 2048 nodes, codec | 1.384 ms | 913.95 KB | 4.805 ms / 4,715.60 KB |
| Publish + wait reevaluation | 1 armed wait, 256-node world | 200.7 µs | 197.33 KB | 717.6 µs / 1.07 MB |
| Publish + wait reevaluation | 256 armed waits | 242.5 µs | 263.08 KB — **L1: < 2× the 1-wait row** | 53.380 ms / 108.08 MB |
| Canonical encode (codec alone) | 64 nodes | 17.77 µs | 9.06 KB | 28.58 µs / 42.58 KB |
| Canonical encode (codec alone) | 512 nodes | 130.31 µs | 68.56 KB | 203.85 µs / 340.16 KB |
| Canonical encode (codec alone) | 2048 nodes | 586.51 µs | 272.64 KB | 1.180 ms / 1,360.30 KB |

## Gated properties (L1, in `tests/SignalRouter.Performance.Tests`)

Each gate runs the exact workload of its row above:

- The quiescent pump allocates **exactly zero bytes** (`PumpReport` is a value
  type — the L0 quiescence obligation of performance.md §2, realized).
- Quiescent-pump allocation is independent of retained terminals (exact
  equality, 0 vs **4096** retained — the default terminal capacity).
- Trace-ring emission at capacity allocates exactly zero bytes (**8192**-entry
  ring — the default ring capacity).
- Six representative materialization lookups (incl. the completeness
  longest-prefix walk) allocate exactly zero bytes.
- Constructing a node from canonically-ordered immutable input allocates
  exactly the 72 B aggregate object (input arrays are kept, never re-copied).
- A 256-wait revision advance allocates less than 2× a 1-wait advance
  (proportionality; per-wait re-materialization would be ~96×).

## Revision policy

A row moves only together with the change that moved it, in the same review
(performance.md §4). CI never edits this file.
