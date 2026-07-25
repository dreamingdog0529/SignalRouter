# ADR 0002 (v2): Observation as Projection; Three-Way Identity

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-25
> **Normative specs:** [../spec/semantic-model.md](../spec/semantic-model.md) ·
> [../spec/observation-state.md](../spec/observation-state.md)

## Context

v1 treated the semantic tree as an authoritative snapshot sorted by one stable ID, with
role-implied operations and a single session epoch + registry revision. Mature
accessibility stacks (UI Automation, AT-SPI) converged elsewhere: multiple views over
one tree, control patterns separate from control types, cache + signal + resync
delivery, and computed names that are explicitly not identity. v1's single ID also
conflated three needs — runtime addressing, persistent identity, and search.

## Decision

1. **Observation is a projection**: views are governed by `ViewContractId@version`,
   scoped, security-domain-bound, and completeness-mapped per region (virtualized /
   redacted / out-of-scope / budget-truncated). Delivery is snapshot + sequence-checked
   delta + mandatory resync on gap.
2. **Role and capability are separated**: roles classify; versioned capability
   contracts (with argument schemas and completion profiles) authorize operations.
3. **Identity splits three ways**: `NodeRef` (opaque, incarnation-scoped),
   `AuthorKey` (author-assigned persistent identity; required for strict-replay scope),
   `Locator` (a query, never identity).
4. **The generation concept splits**: `RuntimeIncarnationId`, `SourceRevision`,
   `ViewContractId@version`, `ViewSequence`, and `ContentId` each answer one question;
   `ContentId` is a versioned artifact contract (algorithm ID + representation version).

## Alternatives considered

- **Authoritative materialized tree (v1 model):** rejected — cannot express
  virtualization, redaction, per-consumer scope, or budget truncation without lying.
- **Role-implied operations:** rejected — breaks the moment a role has optional
  operations, and blocks capability versioning.
- **One "generation" number:** rejected — cannot distinguish raw-state change from
  projection change from delivery order; every consumer would over- or under-invalidate.
- **Locator-based persistent identity (path/label):** rejected — v1's own lesson;
  recordings must not silently rebind after hierarchy or localization changes.

## Consequences

- Agents, recordings, and diffs stop competing for one view's semantics; each pins its
  own contract.
- Strict replay gains a precise scope rule (AuthorKey-required) instead of a global ID
  mandate.
- Pagination, virtualization, and truncation become honest completeness states instead
  of silent omissions.
- The `ContentId` contract adds up-front cost (algorithm/migration rules) in exchange
  for artifact integrity and deduplication.
