# SignalRouter v2 Specification — Adapters and Conformance

> **Status:** v2 design draft — normative once the v2 set is accepted
> **Applies to:** SignalRouter v2 (clean-slate design)
> **Companion specs:** [guarantees.md](guarantees.md) · [semantic-model.md](semantic-model.md) ·
> [kernel-execution.md](kernel-execution.md) · [observation-state.md](observation-state.md)

The core is engine-agnostic; an **adapter** binds it to a concrete UI runtime (Unity is
the first adapter). This spec defines the Adapter SDK contract, completion profiles,
the ManagedIntent/ObservedExternal split, and the three-tier conformance regime. The
key words MUST, MUST NOT, SHOULD, and MAY follow RFC 2119.

## 1. Adapter responsibilities

An adapter:

1. registers nodes (identity, role, capabilities, attributes) and keeps their observable
   state current (`SourceRevision` bumps);
2. executes effect requests for the capabilities it declares, on the engine's required
   thread, within its declared execution-time bounds;
3. produces completion evidence per the bound completion profile;
4. normalizes capturable human input into ManagedIntent submissions (§6);
5. reports uncapturable changes as ObservedExternal events (§6);
6. hosts the pump ([kernel-execution.md](kernel-execution.md) §6) and provides frame
   phases;
7. supplies the replay environment factory for isolated replay runtimes
   ([recording-replay.md](recording-replay.md) §6), including state-source fixture,
   reset, and update wiring per the case fixture contract
   ([verification.md](verification.md) §5.3);
8. provides the headless `VerificationHost` — batch engine lifecycle, pump driving,
   fixture/reset execution — used by CI verification runs
   ([verification.md](verification.md) §6.1).

Adapters never touch kernel state directly; everything crosses the mailbox.

## 2. Adapter SDK surface

The Adapter SDK is BCL-only ([architecture.md](../architecture.md)). Its contract
surface (names indicative, shapes normative — **member-level signatures are carried by
the versioned `AdapterSdk` assembly**, this spec pins the behavioral obligations):

- `INodeSource` — registration/unregistration, attribute updates, revision reporting;
- `IEffectExecutor` — receives effect requests `(invocation, NodeRef, permitToken)`,
  returns adoption synchronously, delivers the fence and completion evidence as
  mailbox messages (§3);
- `IIngressSource` — emits ManagedIntent submissions and ObservedExternal events;
- `IPumpHost` — drives `Pump` and supplies frame phases, deadlines, and the two host
  clocks ([kernel-execution.md](kernel-execution.md) §6).

The kernel hands each source its implemented counterpart at attach time: the node
registry (bootstrap builder before start, receipt-answered control messages after,
[kernel-execution.md](kernel-execution.md) §4), the ingress sink, and the
effect-completion sink. `IReplayEnvironmentFactory` (isolated replay runtimes) is
declared with the recording/replay module, not in the initial SDK surface.

All SDK calls declare threading requirements explicitly in the assembly; the SDK
never assumes a synchronization context.

## 3. Effect protocol

An effect request carries a kernel-minted, single-use **permit token** and is issued
only after the permit evidence is ready ([guarantees.md](guarantees.md) §5.3,
[adr 0010](../adr/0010-effect-protocol-and-kernel-host-contract.md)). The executor:

- MUST adopt or refuse synchronously within its declared sync bound: the answer is
  `Adopted` or `Refused(faultCode)`, and **no effect may begin before `Adopted` is
  returned** — this is what makes a synchronous refusal a zero-effect terminal
  (`Faulted`, `effectStarted = false`). An executor exception cannot prove that rule
  was honored and is treated as possibly effected
  ([kernel-execution.md](kernel-execution.md) §5);
- MUST, for every **adopted** permit token, report the **effect fence**
  (`EffectFenceReached(permit)`) — the point after which the effect can no longer
  mutate application state — and, separately, deliver terminal completion evidence
  (`EffectCompletion(permit, resolution)`) **exactly once**, as mailbox messages,
  even after cooperative cancellation. A refused permit emits no messages — the
  synchronous refusal is its terminal answer;
- MAY omit the fence message only for profiles whose completion entails it
  (`Applied@1`, `FrameCommitted@1`, `PostconditionSatisfied@1`);
  `AdapterAcknowledged@1` never implies a fence (§4);
- MUST NOT invoke other capabilities re-entrantly from inside an effect; follow-up
  work is **committed as continuations through the completion message**, which carries
  the parent's ordered continuation declarations
  ([kernel-execution.md](kernel-execution.md) §9);
- MUST report faults with a stable application fault code where one exists; exception
  internals never leave the adapter boundary unredacted.

**Permit-token lifecycle (normative):** `issued → adopted → fenced → completed`.
Refusal ends the lifecycle at `adopted`; profiles with completion-implied fences may
collapse `fenced` into `completed`. A duplicate fence or completion message for a
token, a message for an unknown token, a successful completion whose evidence names a
profile other than the one bound at admission (or, for the standard profiles of §4,
an evidence kind other than the profile's own), and a message carrying a token from a
previous `RuntimeIncarnationId` are protocol violations: the kernel rejects the message,
traces it, and never lets it alter interaction state. The guarantee this protocol
provides is at-most-once dispatch within an incarnation plus exactly-once completion
messaging — never effect-exactly-once across crashes
([guarantees.md](guarantees.md) §6.1).

## 4. Completion profiles

Standard capabilities bind versioned completion profiles
([semantic-model.md](semantic-model.md) §2.2):

| Profile | Terminal evidence |
|---|---|
| `Applied@1` | The semantic value change is committed in the node store |
| `FrameCommitted@1` | The effect survived the declared frame phase fence |
| `PostconditionSatisfied@1` | The capability's postcondition evaluated true (final evaluation embeds in E4) |
| `AdapterAcknowledged@1` | The engine acknowledged the operation (weakest; for effects the engine cannot fence) |

An adapter declares, per capability, which profiles it supports; recordings pin the
binding in E1 so replay compares like against like across engines. Adapters MUST NOT
invent bespoke termination semantics for standard capabilities; custom capabilities
define their profiles in their own namespace.

`AdapterAcknowledged@1` never implies the effect fence — it exists precisely for
effects the engine cannot fence. A **mutating** capability therefore MUST NOT be bound
to `AdapterAcknowledged@1` unless the adapter still reports a genuine fence for it; an
effect that can provide neither acknowledgment-with-fence nor any stronger profile is
not a conformant `ManagedIntent` mutation and belongs to `ObservedExternal` (§6). The
TCK checks declared bindings against this rule.

Adapters also declare, in their **adapter descriptor**: their frame-phase vocabulary
and fence phase; their synchronous execution-time bound — a **normative wall-clock
bound** with an equally normative logical obligation (adopt-or-refuse without
blocking waits); and their maximum effect-completion latency per profile, declared as
a **frame/pump count** (`MaxFrames`). Enforcement is tiered by what each tier can
measure deterministically: the TCK enforces the logical forms (synchronous return,
declared frame counts on a synthetic host); tier 3 measures the wall-clock bound on
the real engine, and a supported adapter MUST meet it. The kernel's deadline promise
and its worst-case occupancy formula are **conditional on the adapter honoring its
declared sync bound** — the kernel cannot preempt a synchronous call; a
non-conformant adapter breaks exactly the guarantee the bound exists to protect
([kernel-execution.md](kernel-execution.md) §2, §6,
[adr 0010](../adr/0010-effect-protocol-and-kernel-host-contract.md)).

## 5. Distribution constraints

The adapter package owns every engine-specific constraint (e.g. Unity's C# level,
bundled System.Text.Json version, restore mechanics). Core packages MUST remain
consumable without engine-conditional builds: BCL-only kernel/contracts, codec leaves
carrying the only serializer dependencies ([architecture.md](../architecture.md)).
Distribution verification is a conformance tier (§7.3), not a hope.

## 6. Human input: ManagedIntent vs ObservedExternal

Every human-caused change is classified per adapter and capability:

### 6.1 `ManagedIntent`

Input the adapter can capture **before** its application-level effect:

```text
physical/accessibility input → normalize → capability invocation → admission → same executor
```

ManagedIntent goes through the same admission, validation, effect, and observation path
as agent input — this is the equivalence axiom's scope
([semantic-model.md](semantic-model.md) §6). Draft-only widget state (e.g. an edit
buffer before commit) is not an effect; the committed transition is the intent.

### 6.2 `ObservedExternal`

Effects the adapter cannot prevent or pre-capture: native widget side effects,
unmanaged listeners, IME/draft internals, engine-autonomous mutations. These are traced
(source hint, node identity when known, before/after state references, conflict with
any active interaction) but are **never promoted to replayable evidence**. When one
intersects an active interaction or recording, contamination rules apply
([guarantees.md](guarantees.md) §5.5).

Each adapter MUST document which input classes are Managed vs Observed for its engine;
the TCK verifies the declared classification behaves as declared.

### 6.3 Gating

During replay or exclusive automation, ManagedIntent admission is gated with visible UI
indication; refused intents trace as `HumanIntentBlocked`
([kernel-execution.md](kernel-execution.md) §7).

## 7. Conformance: three tiers

Testing is layered so "works with the SDK" and "works on the real engine" are never
conflated:

### 7.1 Kernel model tests

Engine-independent tests of the kernel, stores, comparator, and protocol session logic.
Run in plain .NET CI on every change. This tier also hosts the **performance
gates** ([performance.md](performance.md) §3): exact
kernel-owner-thread allocation counters and proportionality checks, certified
in Release configuration.
They live here and never in the TCK — the TCK path interleaves adapter, clock,
and callback work whose allocations are not the kernel's to promise.

### 7.2 The TCK (technology compatibility kit)

A **versioned, black-box** suite that drives any adapter through the SDK surface:
registration/identity rules, effect adoption/completion/cancellation, completion-profile
evidence, ManagedIntent/ObservedExternal classification behavior, gating and
`HumanIntentBlocked`, pump budget compliance, execution-time bounds, ObservedExternal
contamination, replay-environment isolation, revision-bound source publication
atomicity and `SourceRevision` advance, fixture/reset contract execution, and custom
predicate contract obligations (purity, bounds, no ambient inputs —
[verification.md](verification.md) §2.2). The TCK runs against the in-process
reference adapter in CI (fast feedback) — and that run proves **SDK contract
compliance only**.

The TCK is versioned, and each version documents exactly which of the obligations
above it covers; coverage MAY be staged while the corresponding modules land. A
required obligation a TCK version cannot yet check is reported
**skipped-with-reason**, and any such skip makes the run's aggregate answer
`Incomplete` — never `Passed`. A run with required skips MUST NOT be presented as
tier-2 completion or SDK conformance; it demonstrates only the checked subset.

### 7.3 Engine integration and distribution tests

Adapter-specific tests on the real engine: real thread/frame phases, real input
conversion, domain-reload/incarnation behavior, suppression/recursion prevention, real
transport, and package restore/distribution verification.

**Support claim rule:** an adapter may be labeled *supported* only if the same versioned
TCK passes against the real engine in CI, plus its integration and distribution suites.
An adapter verified only against the reference environment MUST be labeled
*experimental*. This rule is the design-level answer to v1's local-only Unity test gap:
the split between spec-level and engine-level verification is explicit, and the support
label tells the truth about which tier ran where.

An application's verification suite ([verification.md](verification.md)) reuses this
tier's **infrastructure** — engine launcher, pump integration, report plumbing — but is
not adapter conformance and MUST NOT be presented as such: conformance proves the
adapter honors the SDK contract; a verification suite proves one application's
scenarios still hold.
