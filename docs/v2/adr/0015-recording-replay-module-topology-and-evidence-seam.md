# ADR 0015 (v2): Recording/Replay Module Topology and the Evidence-Seam Extension

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-28
> **Normative reference:** [../spec/recording-replay.md](../spec/recording-replay.md) ·
> [../spec/guarantees.md](../spec/guarantees.md) §5–§9 ·
> [../spec/verification.md](../spec/verification.md) §3, §5 ·
> [../spec/observation-state.md](../spec/observation-state.md) §5–§7 ·
> [0003-store-separation-and-commit-order.md](0003-store-separation-and-commit-order.md) ·
> [0010-effect-protocol-and-kernel-host-contract.md](0010-effect-protocol-and-kernel-host-contract.md) ·
> [0011-observation-materialization-and-state-store.md](0011-observation-materialization-and-state-store.md) ·
> [0012-canonical-state-representation-and-digest-policy.md](0012-canonical-state-representation-and-digest-policy.md)

## Context

The recording module is the durable coordinator ADR 0010 staged and the seam
extension ADR 0011 declared. What exists today: the three-method
`IEvidenceCoordinator` gate (E2/E3/E4) with Ready/Pending/Fault control flow
implemented end to end in the kernel, `IRecordObservationServices`
(materialize/lease/after-basis, pump-thread-only), the complete Contracts cut
vocabulary E1–E8, and the reader decision tables (`EvidenceSemantics`) over
`ArtifactFacts`. What does not exist: authoritative hooks for E1/E5/E6/E7/E8,
any durable writer, the artifact format, the comparison profile's content
model, and every replay-side component.

Design review before implementation surfaced four constraints the module
boundaries must answer, each verified against the code:

1. **E2 is not replayable as recorded today.** `CapabilityInvocation` carries
   arguments only as their redacted digest — sufficient for identity and
   dedup, insufficient to re-admit the invocation on a replay runtime. The
   spec requires E2 to store redacted arguments with sensitive values as
   secret references resolved in memory at replay
   ([guarantees.md](../spec/guarantees.md) §5.2,
   [recording-replay.md](../spec/recording-replay.md) §7). `PredicateArmed`
   and `AssertionEvaluated` have the same gap for predicate operands, and the
   E6 cuts do not carry the scope string their re-evaluation needs. A portable
   replay-input contract must therefore land **before** any evidence is
   written durably, or the first artifacts would be structurally unreplayable.
2. **The coordinator needs kernel services the constructor cannot provide.**
   The durable coordinator consumes `IRecordObservationServices`, which the
   runtime creates internally after construction; the coordinator itself is a
   constructor argument.
3. **The exclusive-control gate is not a recording fence.** It admits the
   holder domain's own work and acknowledges nothing; the open/close fences
   need a full mutation freeze whose completion the kernel itself observes.
4. **Teardown runs in an order the close path cannot use.** The runtime marks
   itself torn down (making `CanAddress` false) before wait resolution, so a
   teardown-time E7 attempt would be impossible without reordering.

## Decision

### 1. Module topology

Four new assemblies, each following the established `src/v2/<Module>` →
`SignalRouter.V2.<Module>` convention:

| Module | References | Owns |
|---|---|---|
| `Codec.Recording` | Contracts only, zero packages | `RecordingEventSchema@1.0` writer/reader, `IArtifactStore` (file + test memory), `ArtifactFacts` production ([adr 0016](0016-recording-event-schema.md)) |
| `Recording` | Contracts, Kernel, AdapterSdk, Codec.Recording | `DurableEvidenceCoordinator`, `RecordingOpenOptions`, capacity/contamination policies |
| `Comparison` | Contracts only | `SemanticComparator`, value normalization, profile migration registry |
| `Replay` | + Codec.CanonicalState, Codec.Recording, Comparison, Recording | pre-scan, trust boundary, `ReplayDriver`, `IReplayEnvironmentFactory`, seal evaluator |

- The **declarative** comparison-profile content model and the semantic-diff
  shape live in **Contracts**: the recording codec embeds the profile document
  in the artifact and references Contracts alone, so the DTOs cannot live
  anywhere else. The **executable** comparison machinery lives in the
  `Comparison` leaf so profile-evolution churn stays out of the Contracts
  surface.
- `IReplayEnvironmentFactory` is declared in `Replay` — the staged position
  the SDK contract test already pins (`IReplayEnvironmentFactory` MUST NOT
  appear in AdapterSdk).
- `KernelTrace` remains diagnostics-only; the recording path never subscribes
  to it (reaffirming [adr 0003](0003-store-separation-and-commit-order.md)).

### 2. Portable replay-input contracts

`RecordedArguments` becomes the E2-carried argument representation: an ordered
set of typed, post-redaction argument values in which every sensitive value
appears only as a `SecretReference` (stable identifier plus digest — never the
value). The kernel projects the live submission payload into this form at
admission, alongside the existing digest; the digest remains the identity and
dedup authority, and the recorded projection must re-digest to it (a
consistency the kernel asserts). `PredicateArmed` and `AssertionEvaluated`
gain the same recorded-operand representation, and the E6 cuts gain the
evaluation scope. Replay resolves `SecretReference`s through an
`ISecretReferenceResolver` in memory only; an unresolvable reference stops
replay before the affected entry ([recording-replay.md](../spec/recording-replay.md) §7).
Redaction itself stays where it is — before materialization, in the
observation path — this ADR adds no second redaction stage.

### 3. The seam: one coordinator object, a wider optional interface

```
IRecordingCoordinator : IEvidenceCoordinator          // E2/E3/E4 unchanged
    Bind(IRecordObservationServices services)         // once, at Start, before any callback
    PrepareOpenEvidence(OpenEvidence) : EvidenceReadiness          // E1
    CommitCloseEvidence(CloseEvidence) : EvidenceReadiness         // E7
    CommitExternalMutation(BarrierEvidence) : BarrierAnswer        // E5: readiness + Continue | RequestClose(reason)
    CommitWaitArmed(WaitArmedEvidence) : EvidenceReadiness         // E6a
    CommitWaitResolved(WaitResolvedEvidence) : EvidenceReadiness   // E6b
    CommitAssertionEvidence(AssertionEvidence) : EvidenceReadiness // E8
    NotifyTeardown()
    CloseRequested : IncompleteReason?                // polled once per pump
    AdmissionPolicy : RecordingAdmissionPolicy        // UnkeyedTarget refusal
```

- **A single object is the single source of recording truth.** The interface
  extends `IEvidenceCoordinator` rather than arriving as a second constructor
  dependency, so the E2/E3/E4 gate and the lifecycle can never disagree about
  whether a recording is active. Every existing coordinator implementation and
  test double survives unchanged; a runtime without recording pays one type
  test at construction, a null check per hook site, and one `CloseRequested`
  read per pump.
- **Every hook returns `EvidenceReadiness`.** `Pending` on E6/E8 parks the
  evidence kernel-side and retries on later pumps — the same discipline the
  E4 commit already uses — so transient sink pressure degrades latency, never
  the artifact. This closes the silent-loss hole a fire-and-forget hook would
  open: a ReplayEvidence event either becomes durable or the artifact ceases
  to be `Completed` ([guarantees.md](../spec/guarantees.md) §5, §8).
- **The coordinator never initiates a fence.** Fence linearization is the
  kernel state machine's job. The coordinator requests degradation through
  exactly two channels: the E5 answer's disposition
  (`Continue | RequestClose(reason)`) and the polled `CloseRequested`
  property (the shared path for `SizeLimit`, `SinkFault`, and any internal
  fault). The kernel reacts by driving the ordinary close fence; the
  coordinator writing an E7 on its own initiative is a protocol violation.
- **Failure routing is fixed per lane** (the transition table): E1 `Fault` →
  open answers `OpenFailed`; E2 `Fault` → admission refused (rejection reason
  `"EvidenceUnavailable"`); E3 `Fault` → `Faulted(EvidenceUnavailable,
  effectPermitted: false)`; E4 `Fault` → the true terminal stands, the
  recording alone degrades; E5/E6/E8 `Fault` → the coordinator records the
  degradation and raises `CloseRequested` with the matching reason. The two
  `EvidenceUnavailable` vocabularies (rejection reason at E2, fault code at
  E3) remain distinct.
- `Bind` is the assembly seam: the kernel hands the coordinator its
  `IRecordObservationServices` during `Start`, before any callback can run.
  `IRecordObservationServices` itself gains `SnapshotCatalog()` (the contract
  tables E1 pins) and cut-level `ReleaseLease(ContentId, OperationId)`; a
  cut's blob pin is held from lease until that cut is durable, then released —
  `ReleaseRecording` remains the whole-artifact terminal release.

### 4. The kernel recording state machine

`NotRecording → OpeningDraining → OpeningCommitting → Active →
ClosingDraining → ClosingCommitting → NotRecording`, driven only on the pump
thread, exposed as split-phase control operations on a new `IRecordingControl`
facade (`OpenRecording`/`CloseRecording` with observers answering
`Opened | OpenRefused | Closed | Failed` — `Failed` is the control operation's
answer and never an artifact state).

- **Draining is a dedicated admission freeze**, not the exclusive-control
  gate: all new mutations are held, human-intent refusals trace
  `HumanIntentBlocked`, and the fence completes when in-flight work — active
  interactions, parked E4 commits, and any admission stalled on E2 `Pending`
  from before the fence — has reached its terminal cut.
- The close fence resolves still-armed waits as `Cancelled` (each emitting its
  E6b hook) before the final snapshot and `CommitCloseEvidence`
  ([guarantees.md](../spec/guarantees.md) §5.9).
- **Teardown is reordered** to: resolve waits (E6b) → `NotifyTeardown` — with
  `RecordObservation` still addressable — so the coordinator can attempt a
  durable `Incomplete(IncarnationChanged)` close → clear stores and mark torn
  down. A coordinator that cannot complete the attempt leaves an artifact the
  reader classifies `Interrupted`, which is the honest answer.
- E5 has two kernel sites: the `ObservedExternal` processing path and the
  externally-caused state-source publication path; both feed
  `CommitExternalMutation`.

### 5. Open policies and defaults

Declared per open in `RecordingOpenOptions`; a policy the implementation does
not support is refused at open, never improvised later
([guarantees.md](../spec/guarantees.md) §8):

| Policy | Options (default first) |
|---|---|
| Unkeyed target | **Refuse admission** (`Rejected(UnkeyedTarget)`, enforced by the kernel via `AdmissionPolicy` before E2) — the artifact-closing variant is declared but unsupported in v2.0 |
| External mutation (E5) | **Barrier-continue** · Terminate (`Incomplete(ExternalMutation)` via `RequestClose`) |
| Capacity | **Close `Incomplete(SizeLimit)`** · Refuse new admissions · Bounded normal close · Chunk rollover (declared, refused at open in v2.0) |
| Contract change | Not a policy: contract registration is bootstrap-only in this kernel, so `Incomplete(ContractChanged)` is unreachable; the reason code stays reserved for a future dynamic-registration kernel |

### 6. Staging and non-goals

- **Checkpoint-first.** Every cut references a full materialization; E3 blob
  reuse uses the same-`ContentId` re-materialization condition of
  [guarantees.md](../spec/guarantees.md) §5.3 (sound because ADR 0012 keeps
  the temporal legs out of the payload). Delta production, invalidation
  tokens, chain bounds (`min(profile, StateStore.MaxChainLength)`), and the
  TimelineTrack lane land as the module's final PR against a record kind the
  schema reserves from day one — no schema major bump.
- `AdaptiveGoal` mode is declared and refused as unsupported in v2.0; strict
  surfaces never label it "strict".
- Self-contained artifacts only: the external-`StateStore`-dependency close is
  declared in the format but unsupported in v2.0.
- The artifact store's memory backend is test-only (`IsDurable = false`); the
  coordinator refuses a non-durable store at open unless the caller opts in
  explicitly (test harnesses).
- Storage writes are synchronous, flushed per evidence append, on the pump
  thread. Group commit is future work behind the storage answer vocabulary
  (`Committed | InFlight | Fault`), which already reserves the asynchronous
  shape; nothing in v2.0 produces `InFlight` outside scripted tests.
- Cancellation orders for E4: `requested` is captured when the cancel message
  is processed, `observed` at the terminal decision; a queue-time cancel
  legally has `requested == observed`.

## Consequences

- The implementation order becomes: portable replay-input contracts →
  kernel evidence-material sufficiency → artifact codec → kernel state
  machine → durable coordinator (with a vertical record-and-classify test) →
  E5/E6/E8 hooks → comparison leaf → replay pre-scan → replay driver with the
  two required end-to-end proofs (record → isolated replay all-`Equal`;
  injected divergence stops at first non-`Equal` with a typed diff) → TCK
  lift and seal evaluator → deltas. Item 5 is complete only when those
  end-to-end proofs and the full recording-side failure matrix are green —
  lifting the two TCK skips alone does not certify
  [guarantees.md](../spec/guarantees.md) §9.
- `TerminalEvidence` grows to E4 sufficiency: the completion evidence and
  cancellation disposition the adapter already reports stop being dropped,
  and continuation commitments (ordinal + fingerprint) are computed at the
  terminal decision — all-or-nothing, so commitments and later admissions can
  never disagree.
- Wait arming/resolution and assertion evaluation gain addressed record-view
  materializations **only while a recording is active**; the non-recording
  cost model of [adr 0013](0013-performance-normativity-and-allocation-policy.md)
  is unchanged.
- The Contracts, Kernel, and Tck public surfaces take reviewed breaking
  changes; each lands with its regenerated API baseline in the same PR. New
  assemblies join the solution file and the API-surface test enumeration in
  the PR that creates them.

## Rejected alternatives

- **Two constructor dependencies (evidence coordinator + recording
  lifecycle).** Two objects can disagree about whether a recording is active;
  the inheritance design makes that state unrepresentable and keeps every
  existing double compiling.
- **One fat interface replacing `IEvidenceCoordinator`.** Breaks every
  implementation for seven members most of them would stub; the hot E2/E3/E4
  gate contract deserves stability.
- **Fire-and-forget (void) E5/E6/E8 hooks.** A dropped append would be
  invisible to the reader — the event would simply not exist — violating the
  non-droppable contract's "durable or not `Completed`" dichotomy.
- **Comparator and profile content both in Contracts.** Every profile-model
  refinement would break the Contracts baseline; only the declarative DTOs
  the codec must serialize belong there.
- **Comparator inside Replay.** The seal evaluator, recording-side
  validation, and the future verification CLI all need pure comparison
  without the replay execution stack.
- **Profile resolution from a runtime registry at replay time.** Registry
  drift would silently change what an old artifact means; the artifact
  embeds the declarative profile document plus digest and is judged against
  what it declared.
- **Reusing the exclusive-control gate as the recording fence.** It admits
  the holder's own mutations and acknowledges nothing; the fence needs a full
  freeze the kernel can observe completing.
- **Recording as a `KernelTrace` subscriber.** Rejected again for the record:
  the trace is lossy by design ([adr 0003](0003-store-separation-and-commit-order.md)).
