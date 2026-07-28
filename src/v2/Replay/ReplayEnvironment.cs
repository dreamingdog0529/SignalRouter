using System;
using SignalRouter.V2.Contracts;
using SignalRouter.V2.Kernel;

namespace SignalRouter.V2.Replay
{
    /// <summary>
    /// One isolated replay runtime (recording-replay.md §6): replay-only nodes,
    /// stages, and stores with no shared static or singleton state. The driver
    /// owns pumping — the environment never pumps on its own — and disposal
    /// tears the twin down.
    /// </summary>
    public interface IReplayEnvironment : IDisposable
    {
        /// <summary>Bootstrapped and started; the driver is the single pump consumer.</summary>
        KernelRuntime Runtime { get; }
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

        /// <summary>In-memory resolution at entry execution.</summary>
        bool TryResolve(SecretReference reference, out FieldValue value);
    }
}
