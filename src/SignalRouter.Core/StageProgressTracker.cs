using System;
using System.Collections.Generic;

namespace SignalRouter
{
    // Records the stages executed by a StagePipeline so the dispatcher can build real
    // StageProgress and FaultInfo instead of the synthetic single "execute" stage. A stage is
    // marked pending immediately before invocation (design §10.1) and moved to completed after a
    // successful return; a pending stage that never completes becomes the terminal faulted or
    // cancelled stage.
    internal sealed class StageProgressTracker
    {
        private readonly List<InteractionStageProgress> completed =
            new List<InteractionStageProgress>();
        private string? pendingId;
        private int pendingIndex;
        private bool hasPending;

        public bool RecordedAnything
        {
            get { return completed.Count > 0 || hasPending; }
        }

        public bool HasPending
        {
            get { return hasPending; }
        }

        // Set when a StagePipeline drives execution. It lets the dispatcher tell a stage pipeline
        // that was cancelled before its first stage (no stage ran, so the result must carry no
        // stages and stay out of the idempotency cache) apart from an opaque pipeline that ran and
        // observed cancellation (represented by the synthetic single stage).
        public bool IsStageDriven { get; private set; }

        public void MarkStageDriven()
        {
            IsStageDriven = true;
        }

        public string PendingStageId
        {
            get
            {
                RequirePending();
                return pendingId!;
            }
        }

        public int PendingStageIndex
        {
            get
            {
                RequirePending();
                return pendingIndex;
            }
        }

        public void BeginStage(string id, int index)
        {
            InteractionContract.RequireIdentifier(id, nameof(id));
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be non-negative.");
            }

            if (hasPending)
            {
                throw new InvalidOperationException(
                    "A stage is already in progress; complete it before beginning another.");
            }

            if (index != completed.Count)
            {
                throw new ArgumentException(
                    "Stage indexes must be contiguous and zero-based.",
                    nameof(index));
            }

            pendingId = id;
            pendingIndex = index;
            hasPending = true;
        }

        public void CompleteStage()
        {
            RequirePending();
            completed.Add(
                new InteractionStageProgress(pendingId!, pendingIndex, InteractionStageStatus.Completed));
            hasPending = false;
            pendingId = null;
        }

        public StageProgress BuildCompleted()
        {
            if (hasPending)
            {
                throw new InvalidOperationException(
                    "Cannot build completed progress while a stage is still in progress.");
            }

            return new StageProgress(completed);
        }

        // Completed stages followed by the pending stage marked with the terminal status.
        public StageProgress BuildTerminal(InteractionStageStatus terminalStatus)
        {
            RequirePending();
            var stages = new List<InteractionStageProgress>(completed)
            {
                new InteractionStageProgress(pendingId!, pendingIndex, terminalStatus),
            };
            return new StageProgress(stages);
        }

        public IReadOnlyList<string> CompletedStageIds()
        {
            var ids = new string[completed.Count];
            for (var index = 0; index < completed.Count; index++)
            {
                ids[index] = completed[index].Id;
            }

            return ids;
        }

        private void RequirePending()
        {
            if (!hasPending)
            {
                throw new InvalidOperationException("No stage is currently in progress.");
            }
        }
    }
}
