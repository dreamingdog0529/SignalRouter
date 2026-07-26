# PerformanceConformanceProfile — `default@0.1` (draft)

The running post-change measurement record of the performance track and the
first instance of the profile schema of
[`docs/v2/spec/performance.md`](../../docs/v2/spec/performance.md) §4. Draft:
the profile is versioned `0.x` until the track's consolidation PR promotes the
L0 obligations to MUST. Rows marked **L1** are gated in CI by exact allocation
counters; **L2** rows are informational measurements. The pre-optimization
record is [`BASELINE.md`](BASELINE.md).

## Identity

| | |
|---|---|
| Profile | `default@0.1` (draft) |
| Measured at | 2026-07-27, branch of the P1 series (post status-publish-skip, trace-ring, deadline-index, wait-shared-read, hot-path cleanups) |
| Machine | Intel Core i9-9900K (Coffee Lake, 8C/16T), Windows 11 25H2 |
| Runtime | .NET 10.0.10, X64 RyuJIT x86-64-v3, Concurrent Workstation GC, no PGO pinning |
| Build | Release |
| ResourceProfile in force | `default@1` ([security-resources.md](../../docs/v2/spec/security-resources.md) §5.1), bench world overrides: observation/materialization ceilings 8 MiB / 4096 nodes |
| Tools | BenchmarkDotNet 0.15.8 + MemoryDiagnoser (timings, L2); `AllocationMeter` exact per-thread counters (L1) |
| Command | `dotnet run -c Release --project bench/v2/SignalRouter.V2.Benchmarks -- --filter '*'` |

## Rows

| Operation | Workload | Time (L2) | Allocated (L1/L2) | Baseline row |
|---|---|---:|---:|---:|
| Idle pump | 0 retained terminals | 81.5 ns | 56 B | 222.3 ns / 280 B |
| Idle pump | 4096 retained terminals | 82.5 ns | 56 B — **L1: exact equality with the 0-terminal row** | 636.0 µs / 714,103 B |
| Trace-ring emit at capacity | 8192-entry ring | 52.0 ns | **L1: exactly 0 B** | 43.15 µs / 65,720 B |
| Submit → zero-effect terminal | 64-node world | 109.5 µs | 239,073 B | 150.1 µs / 276,869 B |
| Snapshot pin + release | 512 nodes, codec | 783.4 µs | 1,227,069 B | unchanged by P1 (representation-phase target) |
| Publish + wait reevaluation | 1 armed wait, 256-node world | 464.0 µs | 1,035,817 B | 691.2 µs / 1.07 MB |
| Publish + wait reevaluation | 256 armed waits | 543.1 µs | 1,217,372 B | 53,380.2 µs / 108.08 MB |
| Canonical encode (codec alone) | 2048 nodes | 1.180 ms | 1,392,947 B | unchanged (untouched by P1) |

Bytes are exact counter values where an L1 gate exists and MemoryDiagnoser
values (1 KB = 1024 B in the source reports, converted to bytes here)
otherwise.

## Gated properties (L1, in `tests/v2/SignalRouter.V2.Performance.Tests`)

- Quiescent-pump allocation is independent of retained terminals (exact
  equality, 0 vs 1024 retained).
- Trace-ring emission at capacity allocates exactly zero bytes.

## Revision policy

A row moves only together with the change that moved it, in the same review
(performance.md §4). CI never edits this file.
