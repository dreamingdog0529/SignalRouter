using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>The answer of an evidence-coordination step (ADR 0010).</summary>
    public enum EvidenceReadiness
    {
        /// <summary>The obligation is durable (or vacuous); execution may proceed.</summary>
        Ready,

        /// <summary>Durability is in flight; retry at a later turn. Execution MUST NOT proceed.</summary>
        Pending,

        /// <summary>The obligation cannot be met (e.g. sink fault). The interaction path answers per the failure matrix.</summary>
        Fault,
    }

    /// <summary>
    /// The recording seam (ADR 0010): the kernel prepares and commits its evidence
    /// obligations through this coordinator and never through fire-and-forget
    /// notification, because E3 is a durability gate — the permit token is minted
    /// only after <see cref="PrepareEffectPermit"/> answers <see cref="EvidenceReadiness.Ready"/> —
    /// and an E4 commit fault fails the recording alone while the true terminal
    /// stays in the RecoveryIndex (guarantees.md §7). With no recording active the
    /// coordinator is the explicit no-op that answers Ready immediately; the
    /// recording module (item 5) supplies the durable implementation. No placeholder
    /// ContentIds exist at this seam — the durable coordinator owns the cut payloads.
    /// </summary>
    public interface IEvidenceCoordinator
    {
        /// <summary>The E2 obligation: durable before any UI effect of the interaction (guarantees.md §5.2).</summary>
        EvidenceReadiness PrepareAdmissionEvidence(RequestId request);

        /// <summary>The E3 obligation: the durable permit that gates the adapter invocation (guarantees.md §5.3).</summary>
        EvidenceReadiness PrepareEffectPermit(RequestId request);

        /// <summary>The E4 obligation. A fault here fails the recording alone; the terminal is already committed.</summary>
        EvidenceReadiness CommitTerminalEvidence(RequestId request, InteractionOutcome outcome);
    }

    /// <summary>The explicit recording-off coordinator: every obligation is vacuous and immediately ready.</summary>
    public sealed class NoOpEvidenceCoordinator : IEvidenceCoordinator
    {
        public static NoOpEvidenceCoordinator Instance { get; } = new NoOpEvidenceCoordinator();

        private NoOpEvidenceCoordinator()
        {
        }

        public EvidenceReadiness PrepareAdmissionEvidence(RequestId request) => EvidenceReadiness.Ready;

        public EvidenceReadiness PrepareEffectPermit(RequestId request) => EvidenceReadiness.Ready;

        public EvidenceReadiness CommitTerminalEvidence(RequestId request, InteractionOutcome outcome) =>
            EvidenceReadiness.Ready;
    }
}
