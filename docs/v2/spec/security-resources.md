# SignalRouter v2 Specification — Security and Resources

> **Status:** v2 design draft — normative once the v2 set is accepted
> **Applies to:** SignalRouter v2 (clean-slate design)
> **Companion specs:** [guarantees.md](guarantees.md) · [semantic-model.md](semantic-model.md) ·
> [observation-state.md](observation-state.md) · [recording-replay.md](recording-replay.md) ·
> [protocol-topology.md](protocol-topology.md)

External interaction control is a privileged capability. This spec defines the threat
model, redaction, exposure, resource bounds, artifact trust, and release gating. The
key words MUST, MUST NOT, SHOULD, and MAY follow RFC 2119.

## 1. Threat model

Defended boundary (default profile):

- one OS user, one machine, developer workflow;
- adversaries: remote network peers (excluded by loopback-only + authentication),
  other OS users on the same machine (excluded by owner-only descriptor ACLs), and
  accidental misuse (excluded by bounds, gating, and explicit exposure).

Explicit non-goals of the default profile:

- a malicious process running as the same OS user;
- a rogue gateway impersonating the real one (mutual auth is a reserved extension,
  [protocol-topology.md](protocol-topology.md) §5);
- confidentiality against a debugger or memory inspection on the same machine.

Any deployment profile that widens the boundary (e.g. in-engine HTTP for headless
hosts) MUST re-derive this analysis; the default posture never silently extends.

## 2. Transport and authentication posture

- Channels are local-only: loopback-literal binds, no remote configuration surface.
- Per-instance 256-bit tokens via owner-only discovery descriptors; fixed-time
  comparison; auth evaluated before version negotiation; fixed `unauthorized` answer;
  credentials, hellos, and exception content never logged
  ([protocol-topology.md](protocol-topology.md) §5).
- Authentication failures and policy rejections are counted and rate-limited in
  diagnostics without echoing secrets.

## 3. Redaction

- Sensitivity is declared at contract/registration level
  ([semantic-model.md](semantic-model.md) §7); redaction executes **at value
  production** for every observation, materialization, trace, recording, and diagnostic
  surface — no store, log, or observation codec ever holds an unredacted sensitive
  value. The live submission path (submitter → executor) is the sole exception and
  carries sensitive arguments under protected handling: in memory only, bounded
  lifetime, never logged, never echoed in errors, never entering any store.
- Recording artifacts store secret **references**; resolution happens only in memory at
  replay time, and unresolvable references stop replay before the affected entry
  ([recording-replay.md](recording-replay.md) §7).
- Fault information crossing any boundary carries stable application codes only —
  never exception types, messages, or stack traces.
- `KernelTrace` diagnostics MUST NOT leak into recording artifacts
  ([observation-state.md](observation-state.md) §6).

## 4. Exposure and domains

- Nothing is agent-visible by default: nodes, capabilities, and state surfaces require
  explicit exposure at registration.
- Observation views are security-domain-scoped; `StateStore` namespaces are
  domain-separated, cross-domain `ContentId` probes fail, and low-entropy values are
  not confirmable by content-address guessing
  ([observation-state.md](observation-state.md) §5).
- The protocol accepts no type names, no reflection, no code, and no unconstrained
  filesystem paths. Artifact paths are normalized and confined to one configured root.

## 5. Resource bounds

Every surface is bounded, with defaults specified at design time and every bound
enforced fail-fast at its owning boundary:

| Surface | Bound |
|---|---|
| Mailbox | per-class capacity; control sized for worst case, mutation refusal on overflow ([kernel-execution.md](kernel-execution.md) §4) |
| Observation views | per-field lengths, node cardinality, graph depth/cycle validation, aggregate byte ceilings — enforced at registration, at materialization, and re-validated on receive |
| Pump | per-pump turn/deadline/materialization budgets ([kernel-execution.md](kernel-execution.md) §6) |
| `RecoveryIndex` | capacity with non-evictable pending entries → admission refusal; terminal retention window ([guarantees.md](guarantees.md) §8) |
| `RecordingSink` | declared-at-open capacity policy ([guarantees.md](guarantees.md) §8) |
| `StateStore` | blob size, chain length, total budget, pin-aware GC |
| `KernelTrace` | ring capacity; loss always permitted, gap-marked |
| Protocol | per-direction message size, pending-handshake slots separated from authenticated slots, per-session in-flight caps |
| Timeline | retention budget ([observation-state.md](observation-state.md) §8) |

Exhaustion behavior is the guarantees taxonomy — refuse admission, degrade to
`Incomplete`, or mark trace gaps — never silent drop, never unbounded growth
([guarantees.md](guarantees.md) §8).

## 6. Artifact trust

Replay artifacts execute real capabilities and are treated as untrusted input:

- integrity: manifest closure and `ContentId` verification before execution;
- structural limits: size, depth, node count, event count, blob bytes — enforced
  before and during the pre-scan;
- capability allowlist: only contracts registered in the target runtime at compatible
  versions execute; unknown contracts refuse the artifact;
- provenance: by default only artifacts produced by the local installation replay;
  overriding this is an explicit, logged operator decision
  ([recording-replay.md](recording-replay.md) §7).

## 7. Release gating

- The adapter's bridge/ingress surface is disabled by default in release builds of the
  host application and requires an explicit, auditable opt-in at the adapter's start
  surface.
- The gateway is a developer tool distributed separately from any shipped application;
  it is off-by-absence, not compile-gated.
- Diagnostic surfaces (timeline inspection, trace export) follow the same release
  gating as the ingress surface.
