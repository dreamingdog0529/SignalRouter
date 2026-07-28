# ADR 0016 (v2): RecordingEventSchema@1.0 — Artifact Format

> **Status:** Accepted (v2 design); the per-cut payload grammar appendix is
> **staged** — it is frozen in the codec PR after the portable replay-input
> contracts fix the cut shapes, with the same committed-worksheet discipline
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
  marker's job is torn-write detection, not tamper resistance, which belongs
  to `ContentId` verification and reader-recomputed closure).

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
  embedded verbatim; artifacts are self-contained in v2.0 (the
  external-`StateStore` dependency close is representable in the header but
  unsupported).

- **The comparison profile is a record, not a cut.** Record kind 0x04 embeds
  the declarative `ReplayComparisonProfile` document and its digest once,
  before E1; E1 continues to pin only the `ReplayComparisonProfileRef`. A
  reader judges the artifact against the embedded document (registry drift
  cannot reinterpret an old artifact) and cross-checks the target runtime's
  catalog at pre-scan.

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
  reader's output is `ArtifactFacts`; classification stays in
  `EvidenceSemantics`.

- **Versioning.** An unsupported major is rejected, never guessed at. Minor
  revisions are additive (new record kinds from the reserved space, new
  reserved code strings — readers present unknown codes verbatim). Any change
  that alters an outcome taxonomy, a cut's durability rule, a terminal shape,
  or the failure matrix is a schema **major** plus an ADR
  ([guarantees.md](../spec/guarantees.md) §10).

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
