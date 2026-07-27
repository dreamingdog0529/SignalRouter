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
| Profile | `default@0.2` (draft) |
| Measured commit | `d13fe2850140c01b2f264b7010c3a1154aac78f0` (head of the P3 series: FieldPath spans, ValueArray, aggregate normalization, PumpReport value type; the "post-P1" baseline column cites the `default@0.1` rows measured at `7cf7654c`) |
| Measured on | 2026-07-27 |
| Machine | Intel Core i9-9900K (Coffee Lake, 8C/16T), Windows 11 25H2 |
| Runtime | .NET 10.0.10, X64 RyuJIT x86-64-v3, Concurrent Workstation GC, no PGO pinning |
| Build | Release |
| ResourceProfile in force | `default@1` ([security-resources.md](../../docs/v2/spec/security-resources.md) §5.1), bench world overrides: observation/materialization ceilings 8 MiB / 4096 nodes |
| Tools | BenchmarkDotNet 0.15.8 + MemoryDiagnoser (timings, L2); `AllocationMeter` exact per-thread counters (L1) |
| Command | `dotnet run -c Release --project bench/v2/SignalRouter.V2.Benchmarks -- --filter '*'` |

## Rows

| Operation | Workload | Time (L2) | Allocated (L1/L2) | Baseline row |
|---|---|---:|---:|---:|
| Idle pump | 0 retained terminals | 75.4 ns | **L1: exactly 0 B** | 222.3 ns / 280 B |
| Idle pump | 4096 retained terminals | 75.9 ns | **L1: exactly 0 B + exact equality with the 0-terminal row** | 636.0 µs / 714,103 B |
| Trace-ring emit at capacity | 8192-entry ring | 52.0 ns | **L1: exactly 0 B** | 43.15 µs / 65,720 B |
| Materialization lookups (six shapes) | 512-node snapshot + completeness walk | — | **L1: exactly 0 B** | split per access |
| Node construction from canonical input | 2 attributes + 1 capability | — | **L1: exactly 72 B** (the aggregate object) | ~5 copies of the data |
| Submit → zero-effect terminal | 64-node world | 80.9 µs | 158,443 B | 150.1 µs / 276,869 B |
| Snapshot pin + release | 512 nodes, codec | 327.3 µs | 597,514 B | 783.4 µs / 1,227,069 B (post-P1) |
| Snapshot pin + release | 2048 nodes, codec | 2.070 ms | 2,400,881 B | 4.805 ms / 4,828,774 B (post-P1) |
| Publish + wait reevaluation | 1 armed wait, 256-node world | 231.4 µs | 444,252 B | 691.2 µs / 1.07 MB |
| Publish + wait reevaluation | 256 armed waits | 277.3 µs | 511,570 B — **L1: < 2× the 1-wait row** | 53,380.2 µs / 108.08 MB |
| Canonical encode (codec alone) | 2048 nodes | 825 µs | 1,228,964 B | 1.180 ms / 1,392,947 B |

Bytes are exact counter values where an L1 gate exists and MemoryDiagnoser
values (1 KB = 1024 B in the source reports, converted to bytes here)
otherwise.

## Gated properties (L1, in `tests/v2/SignalRouter.V2.Performance.Tests`)

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
