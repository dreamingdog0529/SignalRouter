using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// The complete reader classification of one artifact's durable evidence:
    /// the §6.3 outcome, the per-interaction classifications, the rule violations,
    /// the recomputable closure check, and the static replay hazards.
    /// </summary>
    public sealed class ArtifactClassification
    {
        public ArtifactClassification(
            RecordingOutcome outcome,
            ValueArray<InteractionClassification> interactions,
            ValueArray<RuleViolation> violations,
            ClosureCheckResult closure,
            ValueArray<StaticReplayHazard> replayHazards)
        {
            Outcome = outcome;
            Interactions = interactions;
            Violations = violations;
            Closure = closure;
            ReplayHazards = replayHazards;
        }

        public RecordingOutcome Outcome { get; }

        public ValueArray<InteractionClassification> Interactions { get; }

        public ValueArray<RuleViolation> Violations { get; }

        public ClosureCheckResult Closure { get; }

        public ValueArray<StaticReplayHazard> ReplayHazards { get; }
    }
}
