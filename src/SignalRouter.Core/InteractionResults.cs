using System;
using System.Collections.Generic;

namespace SignalRouter
{
    public enum InteractionStatus
    {
        Succeeded = 0,
        Rejected = 1,
        Faulted = 2,
        Cancelled = 3,
    }

    public enum InteractionRejectionCode
    {
        TargetNotFound = 0,
        DuplicateTargetId = 1,
        NotVisible = 2,
        Disabled = 3,
        OperationNotAvailable = 4,
        InvalidArguments = 5,
        CommandNotRegistered = 6,
        ReentrantDispatch = 7,
        ReleaseBuildDisabled = 8,
    }

    public enum InteractionStageStatus
    {
        Completed = 0,
        Faulted = 1,
        Cancelled = 2,
    }

    public sealed record RejectionInfo
    {
        public RejectionInfo(InteractionRejectionCode code, string message)
        {
            InteractionContract.RequireDefinedEnum(code, nameof(code));
            InteractionContract.RequireMessage(message, nameof(message));
            Code = code;
            Message = message;
        }

        public InteractionRejectionCode Code { get; }

        public string Message { get; }
    }

    public sealed record InteractionStageProgress
    {
        public InteractionStageProgress(string id, int index, InteractionStageStatus status)
        {
            InteractionContract.RequireIdentifier(id, nameof(id));
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be non-negative.");
            }

            InteractionContract.RequireDefinedEnum(status, nameof(status));
            Id = id;
            Index = index;
            Status = status;
        }

        public string Id { get; }

        public int Index { get; }

        public InteractionStageStatus Status { get; }
    }

    public sealed record StageProgress
    {
        private static readonly StageProgress EmptyInstance =
            new StageProgress(Array.Empty<InteractionStageProgress>());

        public StageProgress(IEnumerable<InteractionStageProgress> stages)
        {
            if (stages == null)
            {
                throw new ArgumentNullException(nameof(stages));
            }

            var copy = new List<InteractionStageProgress>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stage in stages)
            {
                if (stage == null)
                {
                    throw new ArgumentException("Stage progress must not contain null.", nameof(stages));
                }

                if (stage.Index != copy.Count)
                {
                    throw new ArgumentException(
                        "Stage indexes must be contiguous and zero-based.",
                        nameof(stages));
                }

                if (!ids.Add(stage.Id))
                {
                    throw new ArgumentException("Stage IDs must be unique.", nameof(stages));
                }

                if (copy.Count > 0
                    && copy[copy.Count - 1].Status != InteractionStageStatus.Completed)
                {
                    throw new ArgumentException(
                        "Only the final stage may be faulted or cancelled.",
                        nameof(stages));
                }

                copy.Add(stage);
            }

            Stages = EquatableList<InteractionStageProgress>.CreateOwned(copy);
        }

        public static StageProgress Empty
        {
            get { return EmptyInstance; }
        }

        public EquatableList<InteractionStageProgress> Stages { get; }
    }

    public sealed record FaultInfo
    {
        public FaultInfo(
            string exceptionType,
            string message,
            string? stackTrace,
            string? applicationCode,
            string failedStageId,
            int failedStageIndex,
            IEnumerable<string> completedStageIds)
        {
            InteractionContract.RequireIdentifier(exceptionType, nameof(exceptionType));
            InteractionContract.RequireMessage(message, nameof(message));
            InteractionContract.RequireOptionalIdentifier(applicationCode, nameof(applicationCode));
            InteractionContract.RequireIdentifier(failedStageId, nameof(failedStageId));
            if (failedStageIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(failedStageIndex),
                    failedStageIndex,
                    "Index must be non-negative.");
            }

            if (completedStageIds == null)
            {
                throw new ArgumentNullException(nameof(completedStageIds));
            }

            var copy = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stageId in completedStageIds)
            {
                InteractionContract.RequireIdentifier(stageId, nameof(completedStageIds));
                if (!seen.Add(stageId))
                {
                    throw new ArgumentException(
                        "Completed stage IDs must be unique.",
                        nameof(completedStageIds));
                }

                copy.Add(stageId);
            }

            ExceptionType = exceptionType;
            Message = message;
            StackTrace = stackTrace;
            ApplicationCode = applicationCode;
            FailedStageId = failedStageId;
            FailedStageIndex = failedStageIndex;
            CompletedStageIds = EquatableList<string>.CreateOwned(copy);
        }

        public string ExceptionType { get; }

        public string Message { get; }

        public string? StackTrace { get; }

        public string? ApplicationCode { get; }

        public string FailedStageId { get; }

        public int FailedStageIndex { get; }

        public EquatableList<string> CompletedStageIds { get; }
    }

    // The public channel for stages to fault with a stable application code
    // (design §12.2): the dispatcher copies ApplicationCode into FaultInfo, and the
    // recorder persists that code — never the exception type or message — as the
    // recording's faultCode. Faults raised through any other exception type carry
    // no application code and record faultCode null.
    public sealed class InteractionFaultException : Exception
    {
        public InteractionFaultException(string applicationCode, string message)
            : this(applicationCode, message, null)
        {
        }

        public InteractionFaultException(string applicationCode, string message, Exception? innerException)
            : base(ValidateMessage(message), innerException)
        {
            InteractionContract.RequireIdentifier(applicationCode, nameof(applicationCode));
            ApplicationCode = applicationCode;
        }

        public string ApplicationCode { get; }

        private static string ValidateMessage(string message)
        {
            InteractionContract.RequireMessage(message, nameof(message));
            return message;
        }
    }

    public sealed record StateProbeObservation
    {
        public StateProbeObservation(string probeId, string hash)
        {
            InteractionContract.RequireIdentifier(probeId, nameof(probeId));
            InteractionContract.RequireIdentifier(hash, nameof(hash));
            ProbeId = probeId;
            Hash = hash;
        }

        public string ProbeId { get; }

        public string Hash { get; }
    }

    public sealed record StateObservation
    {
        private static readonly StateObservation EmptyInstance =
            new StateObservation(Array.Empty<StateProbeObservation>());

        public StateObservation(IEnumerable<StateProbeObservation> probes)
        {
            Probes = EquatableList<StateProbeObservation>.CreateSortedUniqueByKey(
                probes,
                nameof(probes),
                probe => probe.ProbeId,
                "Observations must not contain null.",
                "Probe IDs must be unique.");
        }

        public static StateObservation Empty
        {
            get { return EmptyInstance; }
        }

        public EquatableList<StateProbeObservation> Probes { get; }
    }

    public enum StatePropertyChangeKind
    {
        Modified = 0,
        Added = 1,
        Removed = 2,
    }

    public sealed record StatePropertyChange
    {
        public StatePropertyChange(string path, InteractionValue? before, InteractionValue? after)
        {
            InteractionContract.RequireIdentifier(path, nameof(path));
            if (before == null && after == null)
            {
                throw new ArgumentException(
                    "At least one of before or after must be present.");
            }

            if (before != null && after != null && before.Equals(after))
            {
                throw new ArgumentException("Before and after values must differ.");
            }

            Before = before;
            After = after;
            Kind = before == null
                ? StatePropertyChangeKind.Added
                : after == null
                    ? StatePropertyChangeKind.Removed
                    : StatePropertyChangeKind.Modified;
            Path = path;
        }

        public string Path { get; }

        // Null on the absent side of an Added (Before) or Removed (After) change; both sides are
        // present and differ for a Modified change.
        public InteractionValue? Before { get; }

        public InteractionValue? After { get; }

        public StatePropertyChangeKind Kind { get; }
    }

    public sealed record StateProbeDiff
    {
        public StateProbeDiff(
            string probeId,
            string beforeHash,
            string afterHash,
            IEnumerable<StatePropertyChange> changes)
        {
            InteractionContract.RequireIdentifier(probeId, nameof(probeId));
            InteractionContract.RequireIdentifier(beforeHash, nameof(beforeHash));
            InteractionContract.RequireIdentifier(afterHash, nameof(afterHash));
            if (string.Equals(beforeHash, afterHash, StringComparison.Ordinal))
            {
                throw new ArgumentException("A probe diff requires different hashes.");
            }

            Changes = EquatableList<StatePropertyChange>.CreateSortedUniqueByKey(
                changes,
                nameof(changes),
                change => change.Path,
                "Changes must not contain null.",
                "Change paths must be unique.");
            ProbeId = probeId;
            BeforeHash = beforeHash;
            AfterHash = afterHash;
        }

        public string ProbeId { get; }

        public string BeforeHash { get; }

        public string AfterHash { get; }

        public EquatableList<StatePropertyChange> Changes { get; }
    }

    public sealed record StateDiff
    {
        private static readonly StateDiff EmptyInstance =
            new StateDiff(Array.Empty<StateProbeDiff>());

        public StateDiff(IEnumerable<StateProbeDiff> probes)
        {
            Probes = EquatableList<StateProbeDiff>.CreateSortedUniqueByKey(
                probes,
                nameof(probes),
                probe => probe.ProbeId,
                "Diffs must not contain null.",
                "Probe diff IDs must be unique.");
        }

        public static StateDiff Empty
        {
            get { return EmptyInstance; }
        }

        public EquatableList<StateProbeDiff> Probes { get; }
    }

    public sealed record InteractionResult
    {
        public InteractionResult(
            long sequence,
            string requestId,
            string targetId,
            string commandName,
            int commandVersion,
            InteractionOrigin origin,
            InteractionStatus status,
            RejectionInfo? rejection,
            FaultInfo? fault,
            StageProgress stages,
            StateObservation before,
            StateObservation after,
            StateDiff diff)
        {
            if (sequence < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequence),
                    sequence,
                    "Sequence must be positive.");
            }

            InteractionContract.RequireIdentifier(requestId, nameof(requestId));
            InteractionContract.RequireTargetId(targetId, nameof(targetId));
            InteractionContract.RequireIdentifier(commandName, nameof(commandName));
            if (commandVersion < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandVersion),
                    commandVersion,
                    "Command version must be positive.");
            }

            InteractionContract.RequireDefinedEnum(origin, nameof(origin));
            InteractionContract.RequireDefinedEnum(status, nameof(status));
            Stages = stages ?? throw new ArgumentNullException(nameof(stages));
            Before = before ?? throw new ArgumentNullException(nameof(before));
            After = after ?? throw new ArgumentNullException(nameof(after));
            Diff = diff ?? throw new ArgumentNullException(nameof(diff));

            ValidateStateConsistency(Before, After, Diff);
            ValidateStatus(status, rejection, fault, Stages, Before, After, Diff);

            Sequence = sequence;
            RequestId = requestId;
            TargetId = targetId;
            CommandName = commandName;
            CommandVersion = commandVersion;
            Origin = origin;
            Status = status;
            Rejection = rejection;
            Fault = fault;
        }

        public long Sequence { get; }

        public string RequestId { get; }

        public string TargetId { get; }

        public string CommandName { get; }

        public int CommandVersion { get; }

        public InteractionOrigin Origin { get; }

        public InteractionStatus Status { get; }

        public RejectionInfo? Rejection { get; }

        public FaultInfo? Fault { get; }

        public StageProgress Stages { get; }

        public StateObservation Before { get; }

        public StateObservation After { get; }

        public StateDiff Diff { get; }

        private static void ValidateStatus(
            InteractionStatus status,
            RejectionInfo? rejection,
            FaultInfo? fault,
            StageProgress stages,
            StateObservation before,
            StateObservation after,
            StateDiff diff)
        {
            switch (status)
            {
                case InteractionStatus.Succeeded:
                    RequireNoError(rejection, fault);
                    for (var index = 0; index < stages.Stages.Count; index++)
                    {
                        if (stages.Stages[index].Status != InteractionStageStatus.Completed)
                        {
                            throw new ArgumentException(
                                "A succeeded result may contain only completed stages.");
                        }
                    }

                    return;
                case InteractionStatus.Rejected:
                    if (rejection == null || fault != null)
                    {
                        throw new ArgumentException(
                            "A rejected result requires rejection information and no fault.");
                    }

                    if (stages.Stages.Count != 0)
                    {
                        throw new ArgumentException("A rejected result must not contain stages.");
                    }

                    if (!before.Equals(after) || diff.Probes.Count != 0)
                    {
                        throw new ArgumentException(
                            "A rejected result must have identical before and after state hashes.");
                    }

                    return;
                case InteractionStatus.Faulted:
                    if (rejection != null || fault == null)
                    {
                        throw new ArgumentException(
                            "A faulted result requires fault information and no rejection.");
                    }

                    RequireTerminalStage(stages, InteractionStageStatus.Faulted);
                    var failed = stages.Stages[stages.Stages.Count - 1];
                    if (failed.Index != fault.FailedStageIndex
                        || !string.Equals(failed.Id, fault.FailedStageId, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "Fault information must identify the final faulted stage.");
                    }

                    if (fault.CompletedStageIds.Count != stages.Stages.Count - 1)
                    {
                        throw new ArgumentException(
                            "Fault completed-stage IDs must match stage progress.");
                    }

                    for (var index = 0; index < fault.CompletedStageIds.Count; index++)
                    {
                        if (!string.Equals(
                            fault.CompletedStageIds[index],
                            stages.Stages[index].Id,
                            StringComparison.Ordinal))
                        {
                            throw new ArgumentException(
                                "Fault completed-stage IDs must match stage progress.");
                        }
                    }

                    return;
                case InteractionStatus.Cancelled:
                    RequireNoError(rejection, fault);
                    if (stages.Stages.Count == 0)
                    {
                        if (!before.Equals(after) || diff.Probes.Count != 0)
                        {
                            throw new ArgumentException(
                                "Cancellation before execution must not change state.");
                        }
                    }
                    else
                    {
                        RequireTerminalStage(stages, InteractionStageStatus.Cancelled);
                    }

                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        private static void RequireNoError(RejectionInfo? rejection, FaultInfo? fault)
        {
            if (rejection != null || fault != null)
            {
                throw new ArgumentException(
                    "Succeeded and cancelled results must not contain rejection or fault information.");
            }
        }

        private static void RequireTerminalStage(
            StageProgress stages,
            InteractionStageStatus expected)
        {
            if (stages.Stages.Count == 0
                || stages.Stages[stages.Stages.Count - 1].Status != expected)
            {
                throw new ArgumentException(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "The final stage must be {0}.",
                        expected));
            }
        }

        private static void ValidateStateConsistency(
            StateObservation before,
            StateObservation after,
            StateDiff diff)
        {
            if (before.Probes.Count != after.Probes.Count)
            {
                throw new ArgumentException(
                    "Before and after observations must contain the same probes.");
            }

            var diffs = new Dictionary<string, StateProbeDiff>(StringComparer.Ordinal);
            for (var index = 0; index < diff.Probes.Count; index++)
            {
                diffs.Add(diff.Probes[index].ProbeId, diff.Probes[index]);
            }

            for (var index = 0; index < before.Probes.Count; index++)
            {
                var beforeProbe = before.Probes[index];
                var afterProbe = after.Probes[index];
                if (!string.Equals(beforeProbe.ProbeId, afterProbe.ProbeId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Before and after observations must contain the same probes.");
                }

                var changed = !string.Equals(beforeProbe.Hash, afterProbe.Hash, StringComparison.Ordinal);
                StateProbeDiff probeDiff;
                var hasDiff = diffs.TryGetValue(beforeProbe.ProbeId, out probeDiff);
                if (changed != hasDiff)
                {
                    throw new ArgumentException(
                        "State diff entries must exactly match changed probe hashes.");
                }

                if (hasDiff
                    && (!string.Equals(probeDiff.BeforeHash, beforeProbe.Hash, StringComparison.Ordinal)
                        || !string.Equals(probeDiff.AfterHash, afterProbe.Hash, StringComparison.Ordinal)))
                {
                    throw new ArgumentException(
                        "State diff hashes must match before and after observations.");
                }
            }
        }
    }
}
