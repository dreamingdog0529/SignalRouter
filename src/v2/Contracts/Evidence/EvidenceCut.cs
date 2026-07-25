namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The nine ReplayEvidence cut kinds (guarantees.md §5). E6 is one numbered cut
    /// pair with two kinds; CancellationEvidence and ContinuationCommitment are
    /// embedded in E4, not cut kinds. Control-lane operations are deliberately absent
    /// (rule R2, guarantees.md §6.2).
    /// </summary>
    public enum EvidenceCutKind
    {
        /// <summary>E1 — the manifest header and open fence.</summary>
        RecordingOpened,

        /// <summary>E2 — one per admitted mutation interaction.</summary>
        AdmissionCut,

        /// <summary>E3 — the durable effect permit ("BeforeCut").</summary>
        EffectPermit,

        /// <summary>E4 — one per provable terminal.</summary>
        TerminalCut,

        /// <summary>E5 — a contamination interval.</summary>
        ExternalMutationBarrier,

        /// <summary>E6 (armed half) — an explicit wait was armed.</summary>
        PredicateArmed,

        /// <summary>E6 (resolved half) — the wait resolved.</summary>
        PredicateResolved,

        /// <summary>E7 — the close fence.</summary>
        RecordingClosed,

        /// <summary>E8 — one standalone assertion evaluation; atomic and closure-free.</summary>
        AssertionEvaluated,
    }

    /// <summary>
    /// One ReplayEvidence cut. Every cut carries its artifact-local
    /// <see cref="EvidenceSequence"/> (recording-replay.md §2); constructors enforce
    /// single-cut well-formedness only — cross-cut spec rules are
    /// <see cref="EvidenceSemantics"/> results, never exceptions, because a reader
    /// must classify malformed artifacts honestly rather than crash.
    /// </summary>
    public abstract class EvidenceCut
    {
        private protected EvidenceCut(EvidenceSequence sequence)
        {
            Sequence = sequence;
        }

        public abstract EvidenceCutKind Kind { get; }

        /// <summary>The artifact-local append position of this cut.</summary>
        public EvidenceSequence Sequence { get; }
    }
}
