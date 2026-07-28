# ADR 0016 (v2): RecordingEventSchema@1.0 — Artifact Format

> **Status:** Accepted (v2 design). The per-cut payload grammar is frozen in
> the codec leaf (`src/v2/Codec.Recording/RecordingPayloadCodec.cs` — field
> order is constructor order, closed vocabularies as code strings) and pinned
> by the golden vectors and byte worksheet in
> `tests/v2/SignalRouter.V2.Codec.Recording.Tests`, with the same discipline
> as [adr 0012](0012-canonical-state-representation-and-digest-policy.md)
> **Date:** 2026-07-28
> **Normative reference:** [../spec/recording-replay.md](../spec/recording-replay.md) §2, §4 ·
> [../spec/guarantees.md](../spec/guarantees.md) §5.9, §10 ·
> [../spec/observation-state.md](../spec/observation-state.md) §6 ·
> [0007-codec-and-package-boundaries.md](0007-codec-and-package-boundaries.md) ·
> [0015-recording-replay-module-topology-and-evidence-seam.md](0015-recording-replay-module-topology-and-evidence-seam.md)

## Context

The spec constrains the artifact format to exactly three properties — a
public, versioned schema identity (`RecordingEventSchema@major.minor`);
rejection of an unsupported major version; and an explicit commit marker so a
torn final record is detectable and discarded (the v1 newline-commit lesson as
a format obligation, not a JSON-Lines commitment). Everything else is this
ADR's to fix. The owner chose a self-describing deterministic binary over a
JSON representation: zero dependencies in the codec leaf, the ADR 0012 framing
discipline reused, and native embedding of canonical blob bytes without a
transcoding layer.

## Decision

- **One append-only file per artifact.** Layout:

  ```
  artifact   := fileHeader record*
  fileHeader := "SRRE" varuint(major) varuint(minor) str(artifactId) str(incarnationId)
  record     := kind:byte varuint(payloadLength) payload crc32c commit:0xC7
  kind       := 0x01 evidence cut · 0x02 blob · 0x03 timeline (reserved) · 0x04 comparison profile
  ```

  `crc32c` covers `kind ‖ payloadLength ‖ payload` (CRC32C in software —
  netstandard2.1 has no BCL CRC and the codec leaf takes no packages; the
  marker's job is torn-write detection only).

- **The format is tamper-evident, not authenticated — stated plainly.**
  Checksums, `ContentId` verification, and recomputed closure catch
  corruption and internal inconsistency; they do not stop an author who
  rewrites a cut payload and recomputes every check, because nothing is
  keyed. That is exactly why the replay trust boundary's provenance policy
  defaults to artifacts produced by the local installation and makes every
  override an explicit, logged operator decision
  ([recording-replay.md](../spec/recording-replay.md) §7). Keyed
  authentication (a signature record over the stream) is future work and is
  additive — a reserved record kind, a schema minor.

- **Commit rule.** A record exists iff it is fully framed and
  checksum-valid and carries the commit byte. The first torn or invalid
  record truncates the artifact at that point: everything before it is the
  artifact, everything from it on is discarded, and the reader reports the
  truncation so classification can answer `Interrupted` honestly. Append
  durability is a flush per evidence record ([adr 0015](0015-recording-replay-module-topology-and-evidence-seam.md):
  synchronous, pump-thread, group commit deferred).

- **Blob-before-reference file order.** A blob record is appended and flushed
  before the first cut that references its `ContentId` — the StateStore-first
  commit order of [observation-state.md](../spec/observation-state.md) §5.1
  becomes byte order. The writer deduplicates by written `ContentId` (blob
  reuse writes no second copy). Blob payloads are the canonical bytes of
  [adr 0012](0012-canonical-state-representation-and-digest-policy.md),
  embedded verbatim. Artifacts are **self-contained only** in this schema:
  the external-`StateStore` dependency close has no representation, and
  supporting it is a schema revision, not a latent header bit. The profile
  record and the base-snapshot blob both precede E1; their mutual order is
  unconstrained.

- **The comparison profile is a record, not a cut.** Record kind 0x04 embeds
  the declarative `ReplayComparisonProfile` document, exactly once, before
  E1; E1 continues to pin only the `ReplayComparisonProfileRef`. The reader
  enforces single occurrence, pre-E1 position, and agreement between the
  document's identity and E1's pinned reference — a violation degrades the
  artifact. A separate embedded digest of the document would be recomputable
  by any author and adds nothing beyond the record's commit checksum and the
  reference check, so there is none. A reader judges the artifact against
  the embedded document (registry drift cannot reinterpret an old artifact)
  and cross-checks the target runtime's catalog at pre-scan.

- **Secret references.** Sensitive values appear in cut payloads only as
  `SecretReference` (identifier + digest) per
  [adr 0015](0015-recording-replay-module-topology-and-evidence-seam.md);
  the format has no representation for a raw sensitive value at all —
  unencodable by construction, not by policy.

- **Primitive discipline, shared source.** Payload primitives are exactly the
  ADR 0012 set — minimal-form LEB128 varuints, length-framed strict UTF-8
  strings, fixed-width big-endian integers, IEEE-754 bit patterns, one-byte
  booleans, explicit presence bytes for optionals — and closed vocabularies
  encode as their stable code strings, never enum ordinals. The primitive
  writers/readers are compiled into both codec leaves from shared source
  (ADR 0007 permits single-source sharing of primitive invariants); the
  schema grammars stay independent and independently versioned.

- **Bounded reading.** The reader enforces caller-supplied
  `ArtifactReadLimits` (record count, payload length, blob bytes, string
  length, nesting depth) before allocating from any untrusted length field —
  the resource-limit leg of the replay trust boundary applied at decode time,
  not after it.

- **Reader-recomputed closure.** The writer fills E7's declared event count
  and reachable-`ContentId` set faithfully, and the reader still recomputes
  both and verifies every referenced blob by digest before trusting the
  artifact ([guarantees.md](../spec/guarantees.md) §5.9): writer
  self-declaration is evidence about the writer, never the verdict. The
  event count counts **evidence-cut records (kind 0x01) only**, E1 and E7
  included — blob, profile, and timeline records never count. The reader
  also verifies `EvidenceSequence` contiguity over the cut stream (strictly
  monotonic from zero; a gap is a structural violation). The reader's output
  is `ArtifactFacts`; classification stays in `EvidenceSemantics`. A torn
  tail is reported as truncation — the facts simply end early and
  classification answers per [guarantees.md](../spec/guarantees.md) §6.3
  (typically `Interrupted`); the external-integrity flag is reserved for
  digest and closure verification failures, never mere truncation.

- **Versioning.** An unsupported major is rejected, never guessed at. Minor
  revisions are additive (new record kinds from the reserved space, new
  reserved code strings — readers present unknown codes verbatim). Any change
  that alters an outcome taxonomy, a cut's durability rule, a terminal shape,
  or the failure matrix is a schema **major** plus an ADR
  ([guarantees.md](../spec/guarantees.md) §10). Writers emit no timeline
  (0x03) records until the delta/timeline PR lands; a 1.0 reader accepts the
  reserved kind and excludes it from closure.
- **Durability is defined at the store seam.** `IArtifactStore` append
  answers `Committed` only after an operating-system-level flush of the
  written record; the test memory backend is `IsDurable = false` and is
  refused by the coordinator outside explicit test opt-in
  ([adr 0015](0015-recording-replay-module-topology-and-evidence-seam.md)).

- **Golden-vector discipline.** The codec PR commits hand-derived byte
  vectors with a byte-offset worksheet (the ADR 0012 method), a torn-write
  matrix truncating the final record at every byte boundary, and a primitive
  parity suite pinning the shared primitives to the ADR 0012 worksheet bytes.

## Consequences

- The per-cut payload grammar appendix is frozen in the codec PR — after the
  portable replay-input contracts land — so the grammar tables are written
  once against the final cut shapes instead of churning here.
- A dump tool (binary → human-readable listing) is deliberately unbundled;
  the format is stable enough to add one later without schema impact.
- Torn-tail truncation plus per-record checksums make crash recovery entirely
  a reader concern; the writer keeps no recovery state
  ([guarantees.md](../spec/guarantees.md) §7 rows fall out of reader
  classification).

## Rejected alternatives

- **JSON records (with framed commits).** Legal under ADR 0007 — the leaf may
  own a serializer dependency — but the owner chose binary: deterministic
  encoding discipline would have to be re-normalized on top of JSON, blob
  bytes would need base64 or a side file, and size/speed lose for no
  verification gain since artifacts are read by tools, not people.
- **JSON-Lines with newline commits.** Explicitly retired by the spec; the
  commit property survives as the marker, the representation does not.
- **A CRC package dependency.** The leaf stays dependency-zero; a software
  CRC32C is ~30 lines and the marker is not a security boundary.
- **A shared framing assembly.** A third assembly shared by both codec leaves
  couples their versioning for ~250 lines of primitives; shared source gives
  single-source invariants without the coupling.
- **An index/manifest structure inside the file.** Artifacts are bounded by
  declared capacity and read once at pre-scan; E1 is the manifest header and
  a linear verified scan is simpler than keeping an index consistent under
  torn writes.
