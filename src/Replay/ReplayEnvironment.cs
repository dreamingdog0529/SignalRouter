using System;
using SignalRouter.Contracts;
using SignalRouter.Kernel;

namespace SignalRouter.Replay
{
    /// <summary>
    /// One isolated replay runtime (recording-replay.md §6): replay-only nodes,
    /// stages, and stores with no shared static or singleton state. The driver
    /// owns advancement — the environment never advances on its own — and
    /// disposal tears the twin down.
    /// </summary>
    public interface IReplayEnvironment : IDisposable
    {
        /// <summary>Bootstrapped and started; the driver is the single consumer.</summary>
        KernelRuntime Runtime { get; }

        /// <summary>
        /// Advances the twin by ONE bounded step — the finest grain the world
        /// honestly supports: a single pump turn for pump-driven worlds, one
        /// whole frame for frame-phased adapters. The driver checks its stop
        /// conditions between steps, so a coarser grain widens the window in
        /// which the twin can run past a boundary; keep it as fine as the
        /// adapter allows. Answers false when the step observed no remaining
        /// work (the driver grants an idle grace window for asynchronous
        /// completions before declaring a stall).
        /// </summary>
        bool Advance();

        /// <summary>
        /// Advances admission-lane work ONLY — one bare pump turn with no
        /// engine frame hooks, so queued submissions admit while no effect can
        /// start. Every world supports this: admission is kernel work, not
        /// frame work. The driver uses it to hold the pre-effect boundary — the
        /// synthetic pre-cancel choreography (guarantees.md §5.7) and the
        /// admission wait when recorded cuts sit between E2 and the effect.
        /// </summary>
        void AdvanceAdmissionOnly();
    }

    /// <summary>
    /// The application-supplied twin builder (recording-replay.md §6; ADR 0015):
    /// builds an isolated runtime whose bootstrap registrations reproduce the
    /// artifact's E1-pinned world. The factory is the adapter's obligation — the
    /// replay layer never fabricates an environment. The runtime MUST be
    /// constructed with the supplied evidence coordinator: it is the driver's
    /// observation seam — the same E2/E3/E4 material the recording captured is
    /// what replay compares against.
    /// </summary>
    public interface IReplayEnvironmentFactory
    {
        IReplayEnvironment Create(RecordingOpened opened, IEvidenceCoordinator evidence);
    }

    /// <summary>
    /// Resolves recorded secret references in memory only (recording-replay.md
    /// §7; security-resources.md §3): the resolved value never touches the
    /// artifact or any durable surface, and the driver re-digests it against
    /// the recorded keyed digest before use — a mismatch stops before the
    /// affected entry, never a silent substitution.
    /// </summary>
    public interface ISecretReferenceResolver
    {
        /// <summary>Answers resolvability without materializing the value (pre-scan planning).</summary>
        bool CanResolve(SecretReference reference);

        /// <summary>
        /// In-memory resolution at entry execution, keyed by the reference plus
        /// the recorded keyed digest (ADR 0015). The driver re-digests the
        /// answer with the shared redaction material regardless of what the
        /// resolver claims.
        /// </summary>
        bool TryResolve(SecretReference reference, ArgumentDigest expectedDigest, out FieldValue value);
    }
}
