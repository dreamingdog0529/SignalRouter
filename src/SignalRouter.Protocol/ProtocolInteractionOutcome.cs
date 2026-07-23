using System;
using System.Collections.Generic;

namespace SignalRouter.Protocol
{
    // The wire projection of a terminal InteractionResult (design §2, §18.2). This
    // is deliberately a protocol-owned type rather than a reuse of the recording's
    // RecordedOutcome: the recording schema and the wire protocol are versioned
    // independently, so they must not share a serialized contract (ADR 0007).
    // Exception types, messages, stack traces, rejection messages, and state diffs
    // never cross the wire in v1 — only stable codes and probe hashes do,
    // matching ADR 0005 and pre-empting the item-9 redaction pass (design §19).
    public sealed class ProtocolInteractionOutcome : IEquatable<ProtocolInteractionOutcome>
    {
        public ProtocolInteractionOutcome(
            long sequence,
            string requestId,
            string targetId,
            string commandName,
            int commandVersion,
            InteractionOrigin origin,
            InteractionStatus status,
            IEnumerable<InteractionStageProgress> stages,
            InteractionRejectionCode? rejectionCode,
            string? faultCode,
            StateObservation before,
            StateObservation after)
        {
            if (sequence < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequence),
                    sequence,
                    "Sequence must be positive.");
            }

            ProtocolContract.RequireIdentifier(requestId, nameof(requestId));
            ProtocolContract.RequireIdentifier(targetId, nameof(targetId));
            ProtocolContract.RequireIdentifier(commandName, nameof(commandName));
            if (commandVersion < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandVersion),
                    commandVersion,
                    "Command version must be positive.");
            }

            ProtocolContract.RequireDefinedEnum(origin, nameof(origin));
            ProtocolContract.RequireDefinedEnum(status, nameof(status));
            if (rejectionCode != null)
            {
                ProtocolContract.RequireDefinedEnum(rejectionCode.Value, nameof(rejectionCode));
            }

            ProtocolContract.RequireOptionalIdentifier(faultCode, nameof(faultCode));
            if (before == null)
            {
                throw new ArgumentNullException(nameof(before));
            }

            if (after == null)
            {
                throw new ArgumentNullException(nameof(after));
            }

            var progress = new StageProgress(stages);
            RequireMatchingProbeSets(before, after);
            ValidateShape(status, progress, rejectionCode, faultCode, before, after);

            Sequence = sequence;
            RequestId = requestId;
            TargetId = targetId;
            CommandName = commandName;
            CommandVersion = commandVersion;
            Origin = origin;
            Status = status;
            Stages = progress.Stages;
            RejectionCode = rejectionCode;
            FaultCode = faultCode;
            Before = before;
            After = after;
        }

        public long Sequence { get; }

        public string RequestId { get; }

        public string TargetId { get; }

        public string CommandName { get; }

        public int CommandVersion { get; }

        public InteractionOrigin Origin { get; }

        public InteractionStatus Status { get; }

        public EquatableList<InteractionStageProgress> Stages { get; }

        public InteractionRejectionCode? RejectionCode { get; }

        public string? FaultCode { get; }

        public StateObservation Before { get; }

        public StateObservation After { get; }

        // Projects a dispatcher-produced result onto the fields the wire may
        // carry. Faults keep only the stable application code; rejections keep
        // only the enum code (design §12.2, §19).
        public static ProtocolInteractionOutcome FromResult(InteractionResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return new ProtocolInteractionOutcome(
                result.Sequence,
                result.RequestId,
                result.TargetId,
                result.CommandName,
                result.CommandVersion,
                result.Origin,
                result.Status,
                result.Stages.Stages,
                result.Rejection?.Code,
                result.Fault?.ApplicationCode,
                result.Before,
                result.After);
        }

        public bool Equals(ProtocolInteractionOutcome? other)
        {
            return other != null
                && Sequence == other.Sequence
                && string.Equals(RequestId, other.RequestId, StringComparison.Ordinal)
                && string.Equals(TargetId, other.TargetId, StringComparison.Ordinal)
                && string.Equals(CommandName, other.CommandName, StringComparison.Ordinal)
                && CommandVersion == other.CommandVersion
                && Origin == other.Origin
                && Status == other.Status
                && Stages.Equals(other.Stages)
                && RejectionCode == other.RejectionCode
                && string.Equals(FaultCode, other.FaultCode, StringComparison.Ordinal)
                && Before.Equals(other.Before)
                && After.Equals(other.After);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as ProtocolInteractionOutcome);
        }

        public override int GetHashCode()
        {
            var hash = ProtocolContract.CombineHashCodes(
                Sequence.GetHashCode(),
                StringComparer.Ordinal.GetHashCode(RequestId));
            hash = ProtocolContract.CombineHashCodes(hash, (int)Status);
            return ProtocolContract.CombineHashCodes(hash, Stages.GetHashCode());
        }

        // Mirrors InteractionResult's cross-field rules (design §12) for the wire
        // subset so a structurally impossible outcome is rejected at construction
        // on either side of the boundary.
        private static void ValidateShape(
            InteractionStatus status,
            StageProgress progress,
            InteractionRejectionCode? rejectionCode,
            string? faultCode,
            StateObservation before,
            StateObservation after)
        {
            var stages = progress.Stages;
            var last = stages.Count == 0 ? null : stages[stages.Count - 1];
            switch (status)
            {
                case InteractionStatus.Succeeded:
                    RequireNoCodes(rejectionCode, faultCode, status);
                    if (last != null && last.Status != InteractionStageStatus.Completed)
                    {
                        throw new ArgumentException(
                            "Succeeded outcomes must only contain completed stages.",
                            nameof(stages));
                    }

                    break;
                case InteractionStatus.Rejected:
                    if (rejectionCode == null)
                    {
                        throw new ArgumentException(
                            "Rejected outcomes require a rejection code.",
                            nameof(rejectionCode));
                    }

                    if (faultCode != null)
                    {
                        throw new ArgumentException(
                            "Rejected outcomes must not carry a fault code.",
                            nameof(faultCode));
                    }

                    if (stages.Count != 0)
                    {
                        throw new ArgumentException(
                            "Rejected outcomes must not contain stages.",
                            nameof(stages));
                    }

                    RequireEmptyState(before, after, status);
                    break;
                case InteractionStatus.Faulted:
                    if (rejectionCode != null)
                    {
                        throw new ArgumentException(
                            "Faulted outcomes must not carry a rejection code.",
                            nameof(rejectionCode));
                    }

                    if (last == null || last.Status != InteractionStageStatus.Faulted)
                    {
                        throw new ArgumentException(
                            "Faulted outcomes must end with a faulted stage.",
                            nameof(stages));
                    }

                    break;
                default:
                    RequireNoCodes(rejectionCode, faultCode, status);
                    if (last == null)
                    {
                        RequireEmptyState(before, after, status);
                    }
                    else if (last.Status != InteractionStageStatus.Cancelled)
                    {
                        throw new ArgumentException(
                            "Cancelled outcomes with stages must end with a cancelled stage.",
                            nameof(stages));
                    }

                    break;
            }
        }

        private static void RequireNoCodes(
            InteractionRejectionCode? rejectionCode,
            string? faultCode,
            InteractionStatus status)
        {
            if (rejectionCode != null || faultCode != null)
            {
                throw new ArgumentException(
                    status + " outcomes must not carry rejection or fault codes.",
                    nameof(status));
            }
        }

        private static void RequireMatchingProbeSets(
            StateObservation before,
            StateObservation after)
        {
            var beforeProbes = before.Probes;
            var afterProbes = after.Probes;
            if (beforeProbes.Count != afterProbes.Count)
            {
                throw new ArgumentException(
                    "Before and after state maps must cover the same probes.",
                    nameof(after));
            }

            for (var index = 0; index < beforeProbes.Count; index++)
            {
                if (!string.Equals(
                    beforeProbes[index].ProbeId,
                    afterProbes[index].ProbeId,
                    StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Before and after state maps must cover the same probes.",
                        nameof(after));
                }
            }
        }

        private static void RequireEmptyState(
            StateObservation before,
            StateObservation after,
            InteractionStatus status)
        {
            if (before.Probes.Count != 0 || after.Probes.Count != 0)
            {
                throw new ArgumentException(
                    status + " outcomes without stages must carry empty state maps.",
                    nameof(after));
            }
        }
    }
}
