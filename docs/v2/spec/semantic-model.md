# SignalRouter v2 Specification — Semantic Model

> **Status:** v2 design draft — normative once the v2 set is accepted
> **Applies to:** SignalRouter v2 (clean-slate design)
> **Companion specs:** [guarantees.md](guarantees.md) · [kernel-execution.md](kernel-execution.md) ·
> [observation-state.md](observation-state.md) · [recording-replay.md](recording-replay.md) ·
> [adapter-conformance.md](adapter-conformance.md)

This spec defines the vocabulary every other v2 document builds on: nodes, roles,
capabilities, the identity system, the identifier family, and the `ContentId` artifact
contract. The key words MUST, MUST NOT, SHOULD, and MAY follow RFC 2119.

## 1. Nodes

The unit of the semantic model is the **node**: an application-registered element that
can be observed, and optionally operated, through SignalRouter. A node carries:

- an identity (§3);
- a **role** — a descriptive classification (§2.1);
- a set of declared **capabilities** — the operations it supports (§2.2);
- observable attributes (label, value, visibility, enablement, focus, parent link, and
  capability-specific attributes), surfaced through observation views
  ([observation-state.md](observation-state.md));
- an exposure policy (which principals and views may see it,
  [security-resources.md](security-resources.md) §4).

Nodes form a forest via parent links. A parent link MAY reference a non-interactive
container node; a dangling parent reference is a registration error within one
incarnation, but a parent outside the observed scope is a completeness condition, not an
error ([observation-state.md](observation-state.md) §3).

## 2. Role and capability

### 2.1 Roles are descriptive

A role (`button`, `textbox`, `list`, `listitem`, `container`, …) classifies what a node
*is* for human and agent comprehension. Roles carry **no operational authority**: nothing
may be invoked "because the role implies it". Roles are an open, versioned vocabulary;
unknown roles are presentable but never executable.

### 2.2 Capabilities are operational

A **capability** is a versioned contract declaring one operation family a node supports.
Standard capabilities (initial set):

| Capability | Operation |
|---|---|
| `Invoke` | Activate the node (click-like) |
| `SetValue` | Replace the node's committed value |
| `Select` | Choose among the node's options/children |
| `Focus` | Move input focus to the node |
| `Scroll` | Bring content or a child into view |

Each **capability contract** (`CapabilityContractId@version`) defines:

- its argument schema (typed, with sensitivity annotations);
- its validation preconditions;
- its **completion profile** (`CompletionProfileId@version`): what evidence terminates an
  invocation — `Applied`, `FrameCommitted`, `PostconditionSatisfied`, or
  `AdapterAcknowledged` — so the same capability terminates identically across engines.
  Completion profiles are defined by the standard capability, and adapters declare which
  profiles they support; adapters MUST NOT define their own termination for standard
  capabilities ([adapter-conformance.md](adapter-conformance.md) §4).

Custom capabilities are permitted under a reverse-DNS namespace
(`com.example.app:Rotate@1`) with the same contract obligations.

A **capability invocation** — the v2 successor of the v1 command — is pure data:
`(CapabilityContractId@version, target reference, typed arguments)`. The **target
reference** is either a `NodeRef` (runtime form, §3.1) or an `AuthorKey` (persistent
form, §3.2); admission resolves it to exactly one node and records the resolved
identity. The semantic fingerprint covers the capability contract, the resolved target
identity (the `AuthorKey` when the node has one, otherwise the `NodeRef`), and the
redacted-argument digest. The invocation contains no callbacks, tasks, engine objects,
transport metadata, timestamps, or identity envelope; those live in the admission
envelope ([kernel-execution.md](kernel-execution.md) §3).

Availability is per capability, not per node: a node may be visible while a capability
is currently unavailable. Availability and validation preconditions are **two disjoint
gates**: availability is state the adapter/application declares (enabled/disabled);
a validation precondition is a predicate the capability contract declares, evaluated
at `Validating`. Invoking an unavailable capability is `Rejected(CapabilityUnavailable)`
and a failed precondition is `Rejected(PreconditionFailed)`
([guarantees.md](guarantees.md) §3.5) — never a silent no-op.

## 3. Identity: three distinct concepts

v2 splits what v1's single stable ID conflated:

### 3.1 `NodeRef` — runtime handle

An opaque reference minted by the runtime, unique within one `RuntimeIncarnationId`, and
meaningless outside it. `NodeRef`s are cheap, never persisted into recording artifacts,
and never reused within an incarnation. Agents use them for within-session operations.

### 3.2 `AuthorKey` — persistent identity

An application-author-assigned stable key. It is the only identity that persists across
incarnations, and therefore the identity recordings and tests bind to.

- Uniqueness within an incarnation is enforced at registration; duplicates fail
  immediately, and are never resolved by "first match".
- `AuthorKey`s are compared ordinally and never normalized.
- **Strict-replay obligation:** every node that a record view includes for strict
  comparison MUST have an `AuthorKey`. Nodes without one are excluded from strict
  comparison scope (they may still appear in agent views). Dynamic collections MUST use
  item keys stable within their scope.
- **Invocation targets:** a `NodeRef`-targeted invocation on a keyless node is a normal
  within-session operation, but it cannot enter strict-replay scope: an active strict
  recording either refuses its admission or the artifact closes
  `Incomplete(UnkeyedTarget)`, per the recording's open policy
  ([guarantees.md](guarantees.md) §5.2).
- Hierarchy paths, labels, and sibling indexes MUST NOT serve as fallback persistent
  identity.

### 3.3 `Locator` — a query, not an identity

A search expression over observable attributes (role, label, value, ancestry). Locators
*resolve to* nodes; they never *are* the node's identity. Resolution yields zero, one, or
many matches; zero or many is a normal query answer for agents, and a divergence or
environment incompatibility during adaptive replay
([recording-replay.md](recording-replay.md) §5.3). Recordings MAY carry locators as
diagnostics and migration hints only.

Computed display text (the accessible-name analogue) is an attribute, and is expressly
not identity.

## 4. The identifier family

v2 replaces v1's single session epoch with distinct identifiers, each with one meaning:

| Identifier | Meaning | Lifetime |
|---|---|---|
| `RuntimeIncarnationId` | One live runtime instance; the namespace of `NodeRef`s and request identity | From runtime creation to teardown (a domain reload creates a new incarnation) |
| `SourceRevision` | Monotonic revision of the **observation store** — the node store plus all revision-bound state-source documents (§8) — within an incarnation | Advanced by every observable mutation of either; there is no separate source-revision namespace |
| `ViewContractId@version` | The projection rules producing an observation view | Versioned contract |
| `ViewSequence` | Delta ordering within one view subscription | Per subscription |
| `LogicalOrder` | Total admission order of mutation interactions | Per incarnation |
| `EvidenceSequence` | Monotonic append position of a ReplayEvidence cut within one recording artifact ([recording-replay.md](recording-replay.md) §2) | Per artifact |
| `RequestId` | One submitted request, assigned by the caller before dispatch | Deduplicated within incarnation + retention window |
| `OperationId` | A long-running operation (wait, recording, replay) | Until resolved + retention |
| `ContentId` | Content address of a materialized observation blob | Artifact-scoped (§5) |
| `StateSourceKey` | One registered domain state source (§8) | Stable across incarnations |
| `StateSourceContractId@version` | The schema contract of a state-source document (§8) | Versioned contract |
| `PredicateContractId@version` | One registered predicate ([verification.md](verification.md) §2) | Versioned contract |

Rules:

- A transport reconnect preserves `RuntimeIncarnationId`; recovery scoping binds to the
  incarnation, never to the connection ([protocol-topology.md](protocol-topology.md) §5).
- Requests stranded by an incarnation change are never re-executed automatically; their
  status is `OutcomeUnknown` after retention ([guarantees.md](guarantees.md) §7).
- No identifier participates in capability-invocation equality; identity is runtime
  metadata ([kernel-execution.md](kernel-execution.md) §3).
- **`ViewWatermark` is not a distinct identifier**: it is the role a `SourceRevision`
  plays as a view-side high-water mark — the highest revision a materialization or
  delta subscription has fully applied ([observation-state.md](observation-state.md)
  §4). Evidence cuts that record a watermark (E3, E8) record a `SourceRevision`.

## 5. `ContentId` — the artifact contract

A `ContentId` is a content-addressed reference to a materialized observation blob in the
`StateStore` ([observation-state.md](observation-state.md) §5). Because it is a
persistent reference inside recording artifacts, it is a versioned contract, not an
implementation detail:

- **Structure:** `(digestAlgorithmId, canonicalRepresentationVersion, digest)`. Both the
  algorithm and the canonical representation are explicit and versioned.
- **Role:** integrity verification, lookup, deduplication, and a fast equality path.
  `ContentId` equality implies semantic equality; **inequality implies nothing** — a
  differing `ContentId` routes to the typed semantic comparator, never directly to
  `Diverged` ([recording-replay.md](recording-replay.md) §5).
- **Verification:** a reader MUST verify a blob against its `ContentId` before use;
  mismatch is an integrity failure (`Interrupted`/refused artifact), not a divergence.
- **Migration:** algorithm or representation changes require re-addressing; artifacts
  remain comparable if their payloads project onto a common comparison profile,
  otherwise `Incomparable`.
- **Namespacing:** content addresses are namespaced per security domain; a blob's
  `ContentId` MUST NOT be observable from a domain that may not read the blob
  ([observation-state.md](observation-state.md) §5, [security-resources.md](security-resources.md) §4).

## 6. The identity envelope: Principal / Ingress / Provenance / Causality

v2 abolishes the v1 `Origin` enum (`Human | Agent | Replay | Test`) in favor of four
orthogonal fields carried in the admission envelope:

| Field | Question | Example values |
|---|---|---|
| `Principal` | On whose authority? | local user, named agent session, test harness |
| `Ingress` | Through which entry path? | physical input, accessibility, MCP, replay, in-process API |
| `Provenance` | Directed by whom? | human-directed, automation, unknown |
| `Causality` | Caused by what? | parent `RequestId` + continuation ordinal, external trigger |

Semantics MUST NOT branch on this envelope after admission: given the same principal
authority, preconditions, and capability invocation, the post-admission effect path and
observation contract are ingress-independent (the equivalence axiom,
[philosophy.md](../philosophy.md)). The envelope exists for authorization at admission,
auditing, and evidence — it is recorded in E2 ([guarantees.md](guarantees.md) §5.2) and
is never an execution input.

## 7. Sensitivity and redaction position

Sensitivity is declared where values are defined: capability contracts annotate sensitive
arguments; node registrations annotate sensitive attributes. A node may raise sensitivity
relative to the contract but never lower it, and cannot change argument names, types, or
requiredness.

Redaction is applied **at value production** for every observation, materialization,
trace, recording, and diagnostic surface — no store, log, or observation codec ever
receives an unredacted sensitive value. The **live submission path is the sole
exception**: a sensitive argument travels from submitter to executor under protected
handling — in memory only, bounded lifetime, never logged, never echoed in errors,
never entering any store — and recordings persist only secret references, resolved in
memory at replay ([observation-state.md](observation-state.md) §5,
[security-resources.md](security-resources.md) §3).

## 8. State source identity and contracts

Domain state sources ([observation-state.md](observation-state.md) §7) separate three
concerns that v1's probe ID conflated:

- **`StateSourceKey`** — the stable, ordinal-compared identity of one registered
  source (`inventory`, `navigation`). It names the `sources/<key>` scope in views and
  persists across incarnations.
- **`StateSourceContractId@version`** — the document schema contract: field types and
  stable field paths, collection key and ordering rules, migration rules, and the
  unknown-field policy. Contract identity is independent of the key, so two
  applications may bind the same contract under different keys.
- **Display name** — a human label; never identity.

Source contracts declare sensitivity per field (§7 rules apply unchanged) and two
independent exposure flags — agent-visible and record-visible
([observation-state.md](observation-state.md) §7.2). Registration validates the
contract before any document is accepted; duplicate keys fail immediately, mirroring
node-registration discipline (§3.2).

Predicate contracts (`PredicateContractId@version`) follow the same registration
pattern; their semantics live in [verification.md](verification.md) §2.
