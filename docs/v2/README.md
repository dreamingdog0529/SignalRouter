# SignalRouter v2 Design Set

This directory contains the **clean-slate v2 design** of SignalRouter: a from-first-
principles redesign of the same mission — semantic UI projection, structured commands,
deterministic record/replay, and MCP agent control — with an engine-agnostic,
dependency-free core.

**Status: design only.** Nothing in this directory is implemented. The v1 documents
([../design.md](../design.md), [../adr/](../adr/)) remain the sole authority for the
current implementation and are intentionally untouched; v2 shares no compatibility
with v1's API, protocol, or artifacts, and this set renumbers its ADRs from 0001.

## Reading order

| Read | To learn |
|---|---|
| [philosophy.md](philosophy.md) | The stable "why": mission, axioms, projection philosophy, determinism tiers, honest uncertainty, the scoped equivalence axiom, non-goals |
| [architecture.md](architecture.md) | The integrated shape: topology, module boundaries, data flows, store ownership, trust boundaries |
| [spec/](spec/) | The only normative source (MUST/SHOULD). Start with [spec/guarantees.md](spec/guarantees.md) — the failure matrix and evidence rules everything else is built around |
| [adr/](adr/) | Why each load-bearing choice was made, and what was rejected |

## Normative specs

1. [spec/guarantees.md](spec/guarantees.md) — outcome taxonomies, determinism tiers,
   ReplayEvidence cuts E1–E8, terminal shapes, the failure matrix
2. [spec/semantic-model.md](spec/semantic-model.md) — nodes, roles vs. capabilities,
   three-way identity, the identifier family, the `ContentId` contract
3. [spec/kernel-execution.md](spec/kernel-execution.md) — single-owner kernel, lanes,
   mailbox, pump contract, cancellation, continuations, arbitration
4. [spec/observation-state.md](spec/observation-state.md) — views, completeness,
   snapshot/delta/resync, the four stores, the state timeline
5. [spec/recording-replay.md](spec/recording-replay.md) — artifact structure, commit
   order, `ReplayComparisonProfile`, replay modes, the trust boundary
6. [spec/protocol-topology.md](spec/protocol-topology.md) — gateway/runtime authority
   split, split-phase protocol, discovery/auth, MCP mapping
7. [spec/adapter-conformance.md](spec/adapter-conformance.md) — Adapter SDK, completion
   profiles, ManagedIntent/ObservedExternal, the three conformance tiers
8. [spec/security-resources.md](spec/security-resources.md) — threat model, redaction,
   exposure, bounds, artifact trust, release gating
9. [spec/verification.md](spec/verification.md) — predicate model, assertions (E8),
   fidelity vs. verdict, verification cases, seal conditions, CI runner and reports

## Architecture decision records

1. [adr/0001-single-owner-kernel.md](adr/0001-single-owner-kernel.md)
2. [adr/0002-observation-view-and-identity.md](adr/0002-observation-view-and-identity.md)
3. [adr/0003-store-separation-and-commit-order.md](adr/0003-store-separation-and-commit-order.md)
4. [adr/0004-external-state-minimal-gateway.md](adr/0004-external-state-minimal-gateway.md)
5. [adr/0005-semantic-replay-comparison.md](adr/0005-semantic-replay-comparison.md)
6. [adr/0006-human-intent-ingress.md](adr/0006-human-intent-ingress.md)
7. [adr/0007-codec-and-package-boundaries.md](adr/0007-codec-and-package-boundaries.md)
8. [adr/0008-mcp-verification-surface.md](adr/0008-mcp-verification-surface.md)
9. [adr/0009-evidence-ordering-and-open-reason-vocabularies.md](adr/0009-evidence-ordering-and-open-reason-vocabularies.md)
10. [adr/0010-effect-protocol-and-kernel-host-contract.md](adr/0010-effect-protocol-and-kernel-host-contract.md)

## Provenance

This set was produced from a clean-slate redesign mandate (2026-07-25): mission and the
engine-agnostic / zero-dependency axioms fixed by the owner; the architecture developed
against the v1 implementation's lessons and iteratively stress-tested through three
rounds of adversarial design review before writing. Implementation planning, migration
strategy, and v1's eventual disposition are explicitly out of scope here.
