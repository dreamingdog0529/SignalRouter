# SignalRouter v2 Specification — Performance

The key words MUST, MUST NOT, SHOULD, and MAY are to be interpreted as described
in RFC 2119.

> **Status: in force.** The performance track completed on 2026-07-28: every L0
> obligation of §2 is MUST, realized in the implementation and enforced by the
> kernel model gates (`tests/SignalRouter.Performance.Tests`). The layer
> model and its misquoting prohibitions (§1, §5), the measurement contract
> (§3), and the profile schema (§4) were normative from acceptance.

The execution specs already bound **time** (the pump budget and the computable
worst-case occupancy, [kernel-execution.md](kernel-execution.md) §6) and
**retention** (every store bounded, [security-resources.md](security-resources.md)
§5). This document adds the third leg: **work and allocation** — how much a pump or
an operation may do and allocate, and how those claims are measured without lying.

## 1. Guarantee layers

Performance claims live in four layers with different portability and different
enforcement, and no claim may quote a layer it does not belong to:

| Layer | Claim | Enforcement |
|---|---|---|
| **L0 — portable guarantees** | This section's number-free obligations: quiescence, proportionality | The specification itself; gates in kernel model tests |
| **L1 — regression canaries** | Exact allocation counters on CoreCLR (e.g. idle-zero, retained-state equality) | Kernel model tests; each landed gate runs on every CI run |
| **L2 — profile numbers** | Measured time/bytes for named operations on a named environment | A versioned `PerformanceConformanceProfile` (§4) |
| **L3 — host tier** | Behavior on a real engine host (frame budget, GC pressure on Mono/IL2CPP) | Engine integration tier; **unmeasured until the engine adapter exists**, and MUST be labeled so |

## 2. L0 — portable guarantees

- **Quiescence.** A pump with no processable work, no due deadline, no revision
  advance awaiting wait reevaluation, and no checkpoint due performs O(1)
  kernel work and allocates **zero managed bytes on the kernel owner thread**.
  The pump report is inside this obligation (it is a value type). Conditional
  exactly like the deadline promise
  ([kernel-execution.md](kernel-execution.md) §6): the host-injected clock and
  any host observer callbacks must themselves be allocation-free on this path.
- **Proportionality.** The work and allocation of an operation scale only with
  its **admitted work**, its **due items**, and its **explicitly budgeted
  visited/output size** — never with unrelated retained state. Retained
  terminals, armed waits not due, retained blobs, and trace occupancy MUST NOT
  appear as a factor in an unrelated operation's cost. (Batch effects that the
  spec itself defines — a fence draining in-flight work, simultaneous deadline
  expiry — are due items, not violations.)
- **Loss-path cost.** Bounded lossy surfaces (the trace ring) evict in amortized
  O(1) per event; loss handling never rebuilds retained state.
- **The time leg is unchanged.** `maxTurns × (step bound + adapter sync bound)`
  remains the host-computable worst-case occupancy; nothing here weakens it.

Total allocation zero is a **non-goal**: immutable evidence values, materialized
observations handed to callers, and durable artifacts are allocated and owned.
The guarantees above are about waste — allocation proportional to nothing the
caller asked for.

## 3. Measurement contract

- **Scope: kernel owner-thread managed allocation.** The counter is
  `GC.GetAllocatedBytesForCurrentThread` around the measured operation, executed
  synchronously on a dedicated thread. Adapter, clock, and observer allocations
  on other threads are outside the measured claim; native allocation and engine
  objects are outside it on any thread.
- **Counters gate; timings inform.** Allocation counters are deterministic and
  gate with **exact equality** (no tolerances). Wall-clock numbers are never a
  CI gate — they are recorded in profiles (§4).
- **The harness proves itself first.** A measurement harness MUST demonstrate,
  in the same suite, that it reads a clean operation as exactly zero and detects
  a known allocation at full size; a harness that cannot is not a gate.
- **Placement.** Performance gates are **kernel model tests**
  ([adapter-conformance.md](adapter-conformance.md) §7.1), Release
  configuration. The adapter TCK never hosts allocation numbers: the TCK path
  interleaves adapter, clock, and callback work whose allocations are not the
  kernel's to promise.
- **Runtime honesty.** Counter-based results describe CoreCLR. They MUST NOT be
  quoted as Mono or IL2CPP behavior (L3).

## 4. `PerformanceConformanceProfile`

Measured numbers belong to a versioned profile document, never to the spec:

- **Identity:** `ProfileId@major.minor`, the commit measured, build
  configuration, TFM, runtime version, GC mode, PGO state, OS/CPU.
- **Reference:** the `ResourceProfile` in force during measurement
  ([security-resources.md](security-resources.md) §5.1) — numbers are
  meaningless without the bounds they ran under.
- **Rows:** named operation, workload parameters, measured time and allocated
  bytes, and which layer each column belongs to (L1 exact / L2 informational).
- **Revision policy:** a profile revision accompanies the change that moved a
  number, in the same review; CI never auto-edits a profile. Regression is a
  failing L1 gate, not a drifted L2 number.
- **Claim form:** conformance to a profile is the independent claim
  *performance-certified under `ProfileId@version`*. It is never folded into
  the adapter support tiers ([adapter-conformance.md](adapter-conformance.md)
  §7): *supported* and *experimental* speak about correctness conformance only.

The first recorded profile input is the pre-optimization baseline
(`bench/BASELINE.md`), which is explicitly non-normative.

## 5. Prohibited claims

- Quoting L1/L2 numbers as engine-host behavior (L3) before the engine adapter's
  integration tier measures them.
- Presenting a timing as a guarantee, or an allocation ceiling as a promise on a
  runtime it was not measured on.
- Calling a run with disabled gates "performance-certified".
