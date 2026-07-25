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
   ([recording-replay.md](recording-replay.md) §6).

Adapters never touch kernel state directly; everything crosses the mailbox.

## 2. Adapter SDK surface

The Adapter SDK is BCL-only ([architecture.md](../architecture.md)). Its contract
surface (names indicative, shapes normative):

- `INodeSource` — registration/unregistration, attribute updates, revision reporting;
- `IEffectExecutor` — receives effect requests `(invocation, NodeRef, permitToken)`,
  returns adoption synchronously, delivers completion evidence as mailbox messages;
- `IIngressSource` — emits ManagedIntent submissions and ObservedExternal events;
- `IPumpHost` — drives `Pump` and supplies frame phases and deadlines;
- `IReplayEnvironmentFactory` — builds isolated replay runtimes.

All SDK calls declare threading requirements explicitly; the SDK never assumes a
synchronization context.

## 3. Effect protocol

An effect request is issued only after the permit ([guarantees.md](guarantees.md)
§5.3). The executor:

- MUST adopt or refuse synchronously within its declared sync bound;
- MUST deliver terminal completion evidence exactly once, as a mailbox message, even
  after cooperative cancellation;
- MUST NOT invoke other capabilities re-entrantly from inside an effect (follow-ups are
  continuations, [kernel-execution.md](kernel-execution.md) §9);
- MUST report faults with a stable application fault code where one exists; exception
  internals never leave the adapter boundary unredacted.

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

Adapters also declare their synchronous execution-time bound and their maximum
effect-completion latency per profile; the TCK enforces both, which is what makes the
kernel's control-lane responsiveness claim real
([kernel-execution.md](kernel-execution.md) §2).

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
Run in plain .NET CI on every change.

### 7.2 The TCK (technology compatibility kit)

A **versioned, black-box** suite that drives any adapter through the SDK surface:
registration/identity rules, effect adoption/completion/cancellation, completion-profile
evidence, ManagedIntent/ObservedExternal classification behavior, gating and
`HumanIntentBlocked`, pump budget compliance, execution-time bounds, ObservedExternal
contamination, and replay-environment isolation. The TCK runs against the in-process
reference adapter in CI (fast feedback) — and that run proves **SDK contract
compliance only**.

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
