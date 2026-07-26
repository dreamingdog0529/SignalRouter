# ADR 0012 (v2): Canonical-State Representation v1 and Digest Policy

> **Status:** Accepted (v2 design)
> **Date:** 2026-07-26
> **Normative reference:** [../spec/semantic-model.md](../spec/semantic-model.md) §5 ·
> [../spec/observation-state.md](../spec/observation-state.md) §2, §5.1 ·
> [../spec/guarantees.md](../spec/guarantees.md) §5.3 ·
> [../spec/security-resources.md](../spec/security-resources.md) §4 ·
> [0007](0007-codec-and-package-boundaries.md) · [0011](0011-observation-materialization-and-state-store.md)

## Context

`Codec.CanonicalState` must fix what ADR 0011 deferred: the canonical byte form of an
`ObservationMaterialization`, the digest algorithm behind `ContentId`, and the
keyed-digest open point — plus how much of ADR 0007's three-leaf plan ships now.
Three constraints shape the answers: `ContentId` equality must imply semantic equality
(semantic-model.md §5); a reader must verify a blob against its `ContentId` before use
without trusting the writer; and [guarantees.md](../spec/guarantees.md) §5.3 permits
reusing a previous checkpoint blob — the same visible state re-materialized at a later
revision must produce the same `ContentId`, or the reuse path is dead on arrival.

## Decision

- **Representation v1 is a hand-rolled, length-framed binary grammar, BCL-only.**
  Length-prefixed strict UTF-8 for text, fixed big-endian for 64-bit values, IEEE-754
  bit patterns for floats, minimal-form LEB128 varints for counts and lengths, and
  ordinal ordering everywhere (already enforced by the Contracts constructors).
  Although ADR 0007 permits a serializer dependency in this leaf, v1 takes none: the
  grammar is the `InvocationCanonicalizer` framing discipline generalized to bytes.
  The exact grammar is fixed in this ADR (below) and is immutable once shipped;
  a change is a new `canonicalRepresentationVersion` and a new ADR. Byte stability is
  enforced mechanically by golden-vector tests with a committed byte-offset worksheet,
  not by prose.
- **The temporal legs stay out of the payload.** The canonical content embeds the
  *projection identity* — `ViewContractRef`, `SecurityDomainId`, scope — and the
  observation body; it embeds neither `RuntimeIncarnationId` nor `SourceRevision`.
  Those are authoritative in the surrounding snapshot/cut tuple
  (observation-state.md §2). This is what makes guarantees.md §5.3 blob reuse hold:
  an unchanged visible state re-materialized at a later revision yields byte-identical
  content and therefore the same `ContentId`. `Decode` takes the two temporal legs as
  arguments and reconstructs the basis.
- **Canonical text is well-formed UTF-8.** Encoding uses exception-fallback UTF-8;
  a string containing an unpaired surrogate is invalid input, never a silent U+FFFD
  substitution (which would collide two distinct inputs). The Contracts layer enforces
  Unicode scalar well-formedness at construction (identifier grammar and string field
  values), so every constructor-valid materialization within the kernel's bounds
  encodes without an input-derived exception.
- **Digest policy: unkeyed, unsalted SHA-256 over the entire payload, header
  included; the digest is the raw 32 octets.**
  `ContentId = ("sha256", 1, SHA-256(payload))`. The ADR 0011 open point closes as
  *no keyed digest*, on a deliberately recorded design constraint: **portable,
  offline, keyless verification** — any authorized reader of a blob can verify it
  against its `ContentId` with nothing but the bytes (semantic-model.md §5), including
  artifact readers with no access to runtime secrets. SHA-256 is an integrity
  primitive here, not a confidentiality primitive: concealment of low-entropy content
  is carried by the authorization layers (security-resources.md §4) — the store's
  domain-keyed lookup, release-surface authorization, and the rule that every surface
  exposing a `ContentId` demands the same authorization as reading the blob. A future
  profile that needs reader-concealed identifiers registers a new keyed
  `digestAlgorithmId`; it does not reinterpret `"sha256"`.
- **Decode and Verify ship in the same leaf.** Item-5 replay comparison needs the
  round trip; §5 verification needs digest recomputation. Decode enforces canonical
  form by re-encoding: a payload that parses but does not re-encode to the identical
  bytes is malformed (`Encode(Decode(b)) == b` holds by construction). The public
  Contracts constructors are the reconstruction path — they sort, deduplicate, and
  validate without transforming values, so reconstruction is lossless.
- **Redacted-argument digests stay Contracts-owned.** Invocation fingerprints and the
  sensitive-argument HMAC (semantic-model.md §2.2, ADR 0010) are the kernel's
  canonicalizer's concern; this leaf encodes observation materializations only.
- **Scope staging (amends ADR 0007 in implementation scope only).** Item 4 ships
  `Codec.CanonicalState` alone. `Codec.Recording` lands with the recording module
  (item 5) and `Codec.Protocol` with the protocol work (item 6) — each with its first
  real consumer, mirroring ADR 0011's delta staging. The three-leaf logical boundary
  of ADR 0007 is unchanged, and the interim state is not "v2 complete".

## Canonical representation v1 — the byte grammar

All multi-byte integers big-endian. `varuint` is minimal-form unsigned LEB128, at most
5 bytes, value ≤ `int.MaxValue`; a non-minimal form, an unterminated sequence, or an
overflow is malformed. `str` is `varuint(utf8ByteLength)` followed by that many bytes
of strict UTF-8 (overlong forms, encoded surrogates, out-of-range scalars, and
truncated sequences are malformed). `bool` is `0x00 | 0x01`; `opt(T)` is
`0x00 | 0x01 T`; any other discriminator byte is malformed. Every list is
`varuint(count)` followed by exactly `count` items in ascending
`string.CompareOrdinal` order of its stated key — UTF-16 code-unit order, exactly the
order the Contracts constructors produce, which can differ from UTF-8 byte order; a
decoder verifies ordering via the re-encode comparison, never with its own comparator.

```text
payload      := header identity nodes sources completeness    (no trailing bytes)
header       := 0x53 0x52 0x43 0x53 ("SRCS") varuint(=1)
identity     := contract(view) str(domain) str(scope)         (no temporal legs)
contract     := str(id) varuint(major) varuint(minor)
nodes        := varuint(count) node*                          (key ascending)
node         := str(key) str(role) opt(str(parentKey)) attrs caps varuint(childCount)
attrs        := varuint(count) attr*                          (name ascending)
attr         := str(name) (0x01 | 0x00 value)                 (0x01 = redacted, no value)
caps         := varuint(count) cap*                           ("id@major.minor" ascending)
cap          := contract bool(available)
value        := 0x01 str                                      (String)
              | 0x02 i64be                                    (Integer, two's complement)
              | 0x03 bool                                     (Boolean)
              | 0x04 f64bits                                  (Float, IEEE-754 bits, BE)
              | 0x05                                          (Null — tag only)
sources      := varuint(count) source*                        (key ascending)
source       := str(key) contract opt(str(omissionCode)) fields redactedNames
fields       := varuint(count) (str(name) value)*             (name ascending)
redactedNames:= varuint(count) str*                           (ascending)
completeness := bool(rootTruncated) varuint(count) (str(region) str(reasonCode))*
                                                              (region ascending)
```

- `0.0` and `-0.0` have distinct bit patterns and therefore distinct payloads —
  permitted, because `ContentId` inequality implies nothing (semantic-model.md §5).
  NaN is rejected upstream by `FieldValue.Of` and rejected again at decode.
- Closed reason vocabularies (`CompletenessReason` omission and reason codes) encode
  as their stable code strings via an explicit two-way table — never enum integers
  and never `Enum.ToString` (the guarantees.md §3.5 / ADR 0009 discipline). `NodeRole`
  is an open string vocabulary and encodes verbatim. `FieldValueKind` is a purely
  structural discriminator, not a vocabulary, and uses the fixed byte tags above.
- Injectivity holds structurally: the parser's position uniquely determines what a
  byte means, and every variable-length region is bounded by its own length or count
  prefix, so no two distinct well-formed materializations share an encoding.

## Consequences

- The kernel's `RunCheckpointFeed`/`StateStore` interplay benefits directly: an
  attribute update that leaves the visible state identical produces the same
  `ContentId`, the idempotent `Put` deduplicates, and pin reference-counting carries
  multiple timeline entries over one blob.
- **Deduplication makes the cached object's temporal legs provenance, not
  authority.** The `StateStore` retains the first-retained materialization; after a
  same-`ContentId` re-materialization at a later revision, a lookup by `ContentId`
  returns an object whose in-memory basis still carries the first retention's
  incarnation/revision. That is consistent with this ADR, not a bug: any consumer
  resolving state by `ContentId` MUST reattach the temporal legs from the
  referencing tuple or cut and MUST NOT read them from the cached object — exactly
  what `Decode(payload, incarnation, revision)` forces on the durable path
  (observation-state.md §5.1).
- `ICanonicalStateCodec`'s documentation is synchronized with this ADR (digest over
  the canonical bytes embedding the projection identity; temporal legs
  tuple-authoritative; the no-throw contract scoped to Contracts-valid, in-bounds
  materializations — environmental failures like out-of-memory excluded).
- Contracts hardening lands with the leaf: unpaired-surrogate rejection in the
  identifier grammar and string field values, `MaterializedSource` rejecting
  default fields, malformed redacted names, and a name appearing in both the field
  and redacted sets, and `CompletenessEntry` rejecting undefined reasons.

## Rejected alternatives

- **Embedding the temporal legs (incarnation/revision) in the payload** — the same
  visible state would re-address at every revision, killing the guarantees.md §5.3
  checkpoint-reuse path and duplicating basis authority between blob and cut.
- **Keyed digest (per-domain HMAC)** — distributing the key to every legitimate
  reader collapses the verification boundary keyless verify-before-use provides;
  artifact readers (CI verification, later surfaces) would need runtime secrets;
  migration back would re-address every artifact. Concealment is enforced at the
  authorization layers instead (security-resources.md §4).
- **Canonical JSON subset via System.Text.Json** — reintroduces the serializer-pin
  drag ADR 0007 quarantined (Unity's bundled STJ line) and still needs bespoke
  key-ordering, escaping, and float rules; the serializer buys ambiguity, not
  simplicity.
- **Text rendering hashed from strings (the fingerprint shape as-is)** — UTF-16
  char-count framing makes the digest input differ from the stored bytes; a
  persistent artifact wants byte-exact framing of exactly the bytes it stores.
- **Shipping all three codec leaves now** — Recording and Protocol codecs have no
  consumers until items 5/6; designing them blind repeats the mistake ADR 0011's
  delta-staging rejection avoided.
- **Enum integers on the wire** — a C# enum reorder would silently change bytes;
  closed vocabularies use stable code strings, structural discriminators use tags
  fixed in this ADR.
