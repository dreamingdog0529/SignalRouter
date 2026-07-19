using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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

    public sealed class RejectionInfo : IEquatable<RejectionInfo>
    {
        public RejectionInfo(InteractionRejectionCode code, string message)
        {
            InteractionContract.RequireDefinedEnum(code, nameof(code));
            InteractionContract.RequireIdentifier(message, nameof(message));
            Code = code;
            Message = message;
        }

        public InteractionRejectionCode Code { get; }

        public string Message { get; }

        public bool Equals(RejectionInfo? other)
        {
            return other != null
                && Code == other.Code
                && string.Equals(Message, other.Message, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as RejectionInfo);
        }

        public override int GetHashCode()
        {
            return InteractionContract.CombineHashCodes(
                (int)Code,
                StringComparer.Ordinal.GetHashCode(Message));
        }
    }

    public sealed class InteractionStageProgress : IEquatable<InteractionStageProgress>
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

        public bool Equals(InteractionStageProgress? other)
        {
            return other != null
                && string.Equals(Id, other.Id, StringComparison.Ordinal)
                && Index == other.Index
                && Status == other.Status;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as InteractionStageProgress);
        }

        public override int GetHashCode()
        {
            return InteractionContract.CombineHashCodes(
                StringComparer.Ordinal.GetHashCode(Id),
                Index,
                (int)Status);
        }
    }

    public sealed class StageProgress : IEquatable<StageProgress>
    {
        private static readonly StageProgress EmptyInstance =
            new StageProgress(Array.Empty<InteractionStageProgress>());

        private readonly ReadOnlyCollection<InteractionStageProgress> stages;

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

            this.stages = new ReadOnlyCollection<InteractionStageProgress>(copy.ToArray());
        }

        public static StageProgress Empty
        {
            get { return EmptyInstance; }
        }

        public IReadOnlyList<InteractionStageProgress> Stages
        {
            get { return stages; }
        }

        public bool Equals(StageProgress? other)
        {
            return other != null && InteractionContract.SequenceEqual(Stages, other.Stages);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as StageProgress);
        }

        public override int GetHashCode()
        {
            return InteractionContract.GetSequenceHashCode(Stages);
        }
    }

    public sealed class FaultInfo : IEquatable<FaultInfo>
    {
        private readonly ReadOnlyCollection<string> completedStageIds;

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
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

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
            this.completedStageIds = new ReadOnlyCollection<string>(copy.ToArray());
        }

        public string ExceptionType { get; }

        public string Message { get; }

        public string? StackTrace { get; }

        public string? ApplicationCode { get; }

        public string FailedStageId { get; }

        public int FailedStageIndex { get; }

        public IReadOnlyList<string> CompletedStageIds
        {
            get { return completedStageIds; }
        }

        public bool Equals(FaultInfo? other)
        {
            return other != null
                && string.Equals(ExceptionType, other.ExceptionType, StringComparison.Ordinal)
                && string.Equals(Message, other.Message, StringComparison.Ordinal)
                && string.Equals(StackTrace, other.StackTrace, StringComparison.Ordinal)
                && string.Equals(ApplicationCode, other.ApplicationCode, StringComparison.Ordinal)
                && string.Equals(FailedStageId, other.FailedStageId, StringComparison.Ordinal)
                && FailedStageIndex == other.FailedStageIndex
                && InteractionContract.SequenceEqual(
                    CompletedStageIds,
                    other.CompletedStageIds,
                    StringComparer.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as FaultInfo);
        }

        public override int GetHashCode()
        {
            var hash = InteractionContract.CombineHashCodes(
                StringComparer.Ordinal.GetHashCode(ExceptionType),
                StringComparer.Ordinal.GetHashCode(Message),
                FailedStageIndex);
            hash = InteractionContract.CombineHashCodes(
                hash,
                StackTrace == null ? 0 : StringComparer.Ordinal.GetHashCode(StackTrace));
            hash = InteractionContract.CombineHashCodes(
                hash,
                ApplicationCode == null ? 0 : StringComparer.Ordinal.GetHashCode(ApplicationCode));
            hash = InteractionContract.CombineHashCodes(
                hash,
                StringComparer.Ordinal.GetHashCode(FailedStageId));
            return InteractionContract.CombineHashCodes(
                hash,
                InteractionContract.GetSequenceHashCode(CompletedStageIds, StringComparer.Ordinal));
        }
    }

    public sealed class StateProbeObservation : IEquatable<StateProbeObservation>
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

        public bool Equals(StateProbeObservation? other)
        {
            return other != null
                && string.Equals(ProbeId, other.ProbeId, StringComparison.Ordinal)
                && string.Equals(Hash, other.Hash, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as StateProbeObservation);
        }

        public override int GetHashCode()
        {
            return InteractionContract.CombineHashCodes(
                StringComparer.Ordinal.GetHashCode(ProbeId),
                StringComparer.Ordinal.GetHashCode(Hash));
        }
    }

    public sealed class StateObservation : IEquatable<StateObservation>
    {
        private static readonly StateObservation EmptyInstance =
            new StateObservation(Array.Empty<StateProbeObservation>());

        private readonly ReadOnlyCollection<StateProbeObservation> probes;
        private readonly Dictionary<string, string> hashes;

        public StateObservation(IEnumerable<StateProbeObservation> probes)
        {
            if (probes == null)
            {
                throw new ArgumentNullException(nameof(probes));
            }

            var copy = new List<StateProbeObservation>();
            hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var probe in probes)
            {
                if (probe == null)
                {
                    throw new ArgumentException("Observations must not contain null.", nameof(probes));
                }

                if (hashes.ContainsKey(probe.ProbeId))
                {
                    throw new ArgumentException("Probe IDs must be unique.", nameof(probes));
                }

                hashes.Add(probe.ProbeId, probe.Hash);
                copy.Add(probe);
            }

            copy.Sort((left, right) => StringComparer.Ordinal.Compare(left.ProbeId, right.ProbeId));
            this.probes = new ReadOnlyCollection<StateProbeObservation>(copy.ToArray());
        }

        public static StateObservation Empty
        {
            get { return EmptyInstance; }
        }

        public IReadOnlyList<StateProbeObservation> Probes
        {
            get { return probes; }
        }

        public bool TryGetHash(string probeId, out string? hash)
        {
            if (probeId == null)
            {
                throw new ArgumentNullException(nameof(probeId));
            }

            return hashes.TryGetValue(probeId, out hash);
        }

        public bool HasSameHashes(StateObservation? other)
        {
            return Equals(other);
        }

        public bool Equals(StateObservation? other)
        {
            return other != null && InteractionContract.SequenceEqual(Probes, other.Probes);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as StateObservation);
        }

        public override int GetHashCode()
        {
            return InteractionContract.GetSequenceHashCode(Probes);
        }
    }

    public sealed class StatePropertyChange : IEquatable<StatePropertyChange>
    {
        public StatePropertyChange(string path, InteractionValue before, InteractionValue after)
        {
            InteractionContract.RequireIdentifier(path, nameof(path));
            Before = before ?? throw new ArgumentNullException(nameof(before));
            After = after ?? throw new ArgumentNullException(nameof(after));
            if (Before.Equals(After))
            {
                throw new ArgumentException("Before and after values must differ.");
            }

            Path = path;
        }

        public string Path { get; }

        public InteractionValue Before { get; }

        public InteractionValue After { get; }

        public bool Equals(StatePropertyChange? other)
        {
            return other != null
                && string.Equals(Path, other.Path, StringComparison.Ordinal)
                && Before.Equals(other.Before)
                && After.Equals(other.After);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as StatePropertyChange);
        }

        public override int GetHashCode()
        {
            return InteractionContract.CombineHashCodes(
                StringComparer.Ordinal.GetHashCode(Path),
                Before.GetHashCode(),
                After.GetHashCode());
        }
    }

    public sealed class StateProbeDiff : IEquatable<StateProbeDiff>
    {
        private readonly ReadOnlyCollection<StatePropertyChange> changes;

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

            if (changes == null)
            {
                throw new ArgumentNullException(nameof(changes));
            }

            var copy = new List<StatePropertyChange>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                if (change == null)
                {
                    throw new ArgumentException("Changes must not contain null.", nameof(changes));
                }

                if (!paths.Add(change.Path))
                {
                    throw new ArgumentException("Change paths must be unique.", nameof(changes));
                }

                copy.Add(change);
            }

            copy.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
            ProbeId = probeId;
            BeforeHash = beforeHash;
            AfterHash = afterHash;
            this.changes = new ReadOnlyCollection<StatePropertyChange>(copy.ToArray());
        }

        public string ProbeId { get; }

        public string BeforeHash { get; }

        public string AfterHash { get; }

        public IReadOnlyList<StatePropertyChange> Changes
        {
            get { return changes; }
        }

        public bool Equals(StateProbeDiff? other)
        {
            return other != null
                && string.Equals(ProbeId, other.ProbeId, StringComparison.Ordinal)
                && string.Equals(BeforeHash, other.BeforeHash, StringComparison.Ordinal)
                && string.Equals(AfterHash, other.AfterHash, StringComparison.Ordinal)
                && InteractionContract.SequenceEqual(Changes, other.Changes);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as StateProbeDiff);
        }

        public override int GetHashCode()
        {
            var hash = InteractionContract.CombineHashCodes(
                StringComparer.Ordinal.GetHashCode(ProbeId),
                StringComparer.Ordinal.GetHashCode(BeforeHash),
                StringComparer.Ordinal.GetHashCode(AfterHash));
            return InteractionContract.CombineHashCodes(
                hash,
                InteractionContract.GetSequenceHashCode(Changes));
        }
    }

    public sealed class StateDiff : IEquatable<StateDiff>
    {
        private static readonly StateDiff EmptyInstance =
            new StateDiff(Array.Empty<StateProbeDiff>());

        private readonly ReadOnlyCollection<StateProbeDiff> probes;

        public StateDiff(IEnumerable<StateProbeDiff> probes)
        {
            if (probes == null)
            {
                throw new ArgumentNullException(nameof(probes));
            }

            var copy = new List<StateProbeDiff>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var probe in probes)
            {
                if (probe == null)
                {
                    throw new ArgumentException("Diffs must not contain null.", nameof(probes));
                }

                if (!ids.Add(probe.ProbeId))
                {
                    throw new ArgumentException("Probe diff IDs must be unique.", nameof(probes));
                }

                copy.Add(probe);
            }

            copy.Sort((left, right) => StringComparer.Ordinal.Compare(left.ProbeId, right.ProbeId));
            this.probes = new ReadOnlyCollection<StateProbeDiff>(copy.ToArray());
        }

        public static StateDiff Empty
        {
            get { return EmptyInstance; }
        }

        public IReadOnlyList<StateProbeDiff> Probes
        {
            get { return probes; }
        }

        public bool Equals(StateDiff? other)
        {
            return other != null && InteractionContract.SequenceEqual(Probes, other.Probes);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as StateDiff);
        }

        public override int GetHashCode()
        {
            return InteractionContract.GetSequenceHashCode(Probes);
        }
    }

    public sealed class InteractionResult : IEquatable<InteractionResult>
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

        public bool Equals(InteractionResult? other)
        {
            return other != null
                && Sequence == other.Sequence
                && string.Equals(RequestId, other.RequestId, StringComparison.Ordinal)
                && string.Equals(TargetId, other.TargetId, StringComparison.Ordinal)
                && string.Equals(CommandName, other.CommandName, StringComparison.Ordinal)
                && CommandVersion == other.CommandVersion
                && Origin == other.Origin
                && Status == other.Status
                && Equals(Rejection, other.Rejection)
                && Equals(Fault, other.Fault)
                && Stages.Equals(other.Stages)
                && Before.Equals(other.Before)
                && After.Equals(other.After)
                && Diff.Equals(other.Diff);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as InteractionResult);
        }

        public override int GetHashCode()
        {
            var hash = InteractionContract.CombineHashCodes(
                Sequence.GetHashCode(),
                StringComparer.Ordinal.GetHashCode(RequestId),
                StringComparer.Ordinal.GetHashCode(TargetId));
            hash = InteractionContract.CombineHashCodes(
                hash,
                StringComparer.Ordinal.GetHashCode(CommandName),
                CommandVersion);
            hash = InteractionContract.CombineHashCodes(hash, (int)Origin, (int)Status);
            hash = InteractionContract.CombineHashCodes(hash, Rejection == null ? 0 : Rejection.GetHashCode());
            hash = InteractionContract.CombineHashCodes(hash, Fault == null ? 0 : Fault.GetHashCode());
            hash = InteractionContract.CombineHashCodes(hash, Stages.GetHashCode());
            hash = InteractionContract.CombineHashCodes(hash, Before.GetHashCode());
            hash = InteractionContract.CombineHashCodes(hash, After.GetHashCode());
            return InteractionContract.CombineHashCodes(hash, Diff.GetHashCode());
        }

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

                    if (!before.HasSameHashes(after) || diff.Probes.Count != 0)
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
                        if (!before.HasSameHashes(after) || diff.Probes.Count != 0)
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
