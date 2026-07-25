using System;

namespace SignalRouter.V2.Contracts
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
            ValueList<InteractionClassification> interactions,
            ValueList<RuleViolation> violations,
            ClosureCheckResult closure,
            ValueList<StaticReplayHazard> replayHazards)
        {
            Outcome = outcome;
            Interactions = interactions ?? throw new ArgumentNullException(nameof(interactions));
            Violations = violations ?? throw new ArgumentNullException(nameof(violations));
            Closure = closure;
            ReplayHazards = replayHazards ?? throw new ArgumentNullException(nameof(replayHazards));
        }

        public RecordingOutcome Outcome { get; }

        public ValueList<InteractionClassification> Interactions { get; }

        public ValueList<RuleViolation> Violations { get; }

        public ClosureCheckResult Closure { get; }

        public ValueList<StaticReplayHazard> ReplayHazards { get; }
    }
}
