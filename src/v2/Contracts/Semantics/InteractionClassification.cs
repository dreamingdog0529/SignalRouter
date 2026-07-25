using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The four per-interaction terminal shapes of guarantees.md §6.1, in table
    /// order. Shape is determined by which cuts are durable for the interaction.
    /// </summary>
    public enum InteractionShape
    {
        /// <summary>E2 + E4 with effectPermitted = false: rejection, pre-effect cancellation, or pre-effect evidence failure; zero effects.</summary>
        TerminalWithoutEffect,

        /// <summary>E2 + E3 + E4: effect permitted, terminal known — the normal replayable shape.</summary>
        TerminalWithEffect,

        /// <summary>E2 only: no effect was permitted (E3 is the permit); evidence-incomplete.</summary>
        AdmittedOnly,

        /// <summary>E2 + E3, no E4: effect may or may not have occurred — OutcomeUnknown.</summary>
        PermittedWithoutTerminal,
    }

    /// <summary>
    /// The reader's classification of one admitted interaction from its durable
    /// evidence (guarantees.md §6.1, §7).
    /// </summary>
    public sealed class InteractionClassification : IEquatable<InteractionClassification>
    {
        public InteractionClassification(
            RequestId requestId,
            InteractionShape shape,
            InteractionOutcome readerOutcome,
            bool evidenceIncomplete,
            bool contaminated,
            bool strictReplayStopsBeforeEffect)
        {
            if (requestId.IsDefault)
            {
                throw new ArgumentException(
                    "Classification requires a non-default RequestId.", nameof(requestId));
            }

            RequestId = requestId;
            Shape = shape;
            ReaderOutcome = readerOutcome;
            EvidenceIncomplete = evidenceIncomplete;
            Contaminated = contaminated;
            StrictReplayStopsBeforeEffect = strictReplayStopsBeforeEffect;
        }

        public RequestId RequestId { get; }

        public InteractionShape Shape { get; }

        /// <summary>
        /// The outcome the reader answers: the E4 outcome where one is durable,
        /// otherwise <see cref="InteractionOutcome.OutcomeUnknown"/> — never an
        /// invented terminal (guarantees.md §2).
        /// </summary>
        public InteractionOutcome ReaderOutcome { get; }

        public bool EvidenceIncomplete { get; }

        /// <summary>True when an E5 contamination interval marked this interaction (guarantees.md §5.5).</summary>
        public bool Contaminated { get; }

        /// <summary>True when strict replay stops before permitting this interaction's effect.</summary>
        public bool StrictReplayStopsBeforeEffect { get; }

        public bool Equals(InteractionClassification? other) =>
            other != null &&
            RequestId.Equals(other.RequestId) &&
            Shape == other.Shape &&
            ReaderOutcome == other.ReaderOutcome &&
            EvidenceIncomplete == other.EvidenceIncomplete &&
            Contaminated == other.Contaminated &&
            StrictReplayStopsBeforeEffect == other.StrictReplayStopsBeforeEffect;

        public override bool Equals(object? obj) => Equals(obj as InteractionClassification);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(RequestId.GetHashCode(), (int)Shape);
            hash = ContractGrammar.CombineHashes(hash, (int)ReaderOutcome);
            hash = ContractGrammar.CombineHashes(hash, EvidenceIncomplete ? 1 : 0);
            hash = ContractGrammar.CombineHashes(hash, Contaminated ? 1 : 0);
            return ContractGrammar.CombineHashes(hash, StrictReplayStopsBeforeEffect ? 1 : 0);
        }

        public override string ToString() => $"{RequestId}: {Shape} -> {ReaderOutcome}";
    }
}
