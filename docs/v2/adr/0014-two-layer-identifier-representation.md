# ADR 0014 (v2): Two-Layer Identifier Representation

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-27
> **Normative reference:** [../spec/semantic-model.md](../spec/semantic-model.md) §3–§5 ·
> [../spec/kernel-execution.md](../spec/kernel-execution.md) §10 ·
> [../adr/0012-canonical-state-representation-and-digest-policy.md](0012-canonical-state-representation-and-digest-policy.md)

## Context

The performance track's original directive was to move the identifier family onto
a symbol table — interned strings or opaque handles — end to end. Design review
broke two premises of that plan. First, **`AuthorKey` is not a bounded
vocabulary**: nodes unregister, and a register/unregister churn mints unboundedly
many distinct keys over a runtime's life, so permanent interning is a memory-growth
attack surface, not an optimization. Second, **a table ordinal in a portable value
is a cross-runtime hazard**: replay executes on an isolated runtime with its own
table ([kernel-execution.md](../spec/kernel-execution.md) §10), and an
`array[symbol.id]` indexed with a foreign ordinal fails silently — string-fallback
equality protects `Equals`, never indexing. Meanwhile the baseline showed
identifier comparison nowhere near the hot-path leaders (materialization, status
publication), removing the urgency that motivated the original directive.

## Decision

- **Layer 1 — portable values are string-backed, permanently.** Contracts
  identifiers, canonical payloads, artifacts, and the wire keep the validated
  string-backed `readonly struct` representation. Ordering and encoding are
  always `string.CompareOrdinal` ([adr 0012](0012-canonical-state-representation-and-digest-policy.md));
  a table ordinal never participates in any ordering, encoding, or digest.
- **Layer 2 — kernel-internal handles, conditional on evidence.** The kernel MAY
  additionally index its own stores with opaque `(index, generation)` handles.
  This layer is implemented only if profiling shows identifier lookup as a
  material cost after the representation phase — the current baseline says it is
  not — and under these invariants when it is:
  - **H0** This layer indexes identifier-keyed internal stores only. `NodeRef`
    ([semantic-model.md](../spec/semantic-model.md) §3.1) is a distinct public
    concept — unique within an incarnation and **never reused** — and is not
    implemented by these reusable slots.
  - **H1** Handles never cross the kernel boundary: not into artifacts, evidence,
    codecs, the wire, trace detail codes, error text, or any diagnostic surface a
    reader could mistake for a stable identifier.
  - **H2** A handle is resolved only by the store that issued it (owner-bound
    resolution or an owner token checked on every resolve); `(index, generation)`
    alone detects staleness within one table, never a foreign table's handle.
  - **H3** Generations increment checked; a slot at generation ceiling retires
    permanently. Wrap-around ABA is structurally impossible, not improbable.
    Retired slots consume capacity against the semantic ceilings of
    [security-resources.md](../spec/security-resources.md) §5.2, and a table
    whose ceiling is reached refuses further registration with an answer from
    the guarantees taxonomy — it never grows unbounded and never recycles a
    retired slot.
  - **H4** Handles are acquired and released only on the pump thread as portable
    messages commit — producers and decoders never mint handles.
  - **H5** Multi-registration commits are reserve → validate-all → commit with
    full rollback on failure.
  - **H6** Release clears both the reverse map entry and the slot's string
    references, or the table has not released anything.
  - **H7** Every kernel structure that can hold a handle across turns (waits,
    pins, retained bases, continuations) revalidates generation at lookup.
  - **H8** Tests must cover cross-table rejection, stale and double release,
    generation retirement and ceiling refusal, rollback, re-registration,
    incarnation teardown invalidating the whole table, and retained-memory
    plateau under churn.
- **Parent re-registration semantics are pinned as string rebind — in the
  semantic model, not only here.** `AuthorKey` is *the* persistent identity
  ([semantic-model.md](../spec/semantic-model.md) §3.2, which now states the
  rule normatively): a node registered under a previously-used key **is** the
  same logical node, and children whose parent key resolves again resolve to
  it. A handle layer must preserve it: handles bind records, keys bind
  identity — a child's parent reference is by key.
- **No validation bypass across assemblies.** Kernel stores hold the validated
  portable values they were given and re-emit them without reconstruction;
  neither `InternalsVisibleTo` nor an unchecked construction path exists.
  Boundary validation stays exactly where the constructors put it.

## Consequences

- The public API, the canonical byte grammar, artifact portability, and replay
  isolation are untouched by anything this ADR permits.
- The conditional layer has a standing evidence bar: a profile row showing
  identifier lookup as a leading cost. Until that row exists, the layer does not.
- Dictionary keying on string-backed structs stays the kernel's lookup story;
  the non-boxing generic comparer already makes it allocation-free.

## Rejected alternatives

- **A public `Symbol { text, hash, id }`** — embeds a process-local ordinal in a
  portable value: the replay-isolation hazard above, plus a doubled identifier
  footprint on every materialized node.
- **Permanent interning of `AuthorKey` and other open vocabularies** — unbounded
  growth under churn; interning is safe only for closed reserved vocabularies,
  which are cached as validated statics instead.
- **Handle ordinals as sort keys or canonical-encoding order** — silently changes
  `ContentId` bytes with registration order; ADR 0012's ordinal-by-string rule
  is load-bearing.
- **An ambient/global symbol table** — breaks replay's isolated-runtime contract
  and makes handle provenance unverifiable.
- **`InternalsVisibleTo` for unchecked fast paths** — distributes the right to
  skip validation beyond the assembly that owns the invariant.
