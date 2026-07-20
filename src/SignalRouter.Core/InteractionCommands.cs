using System;
using System.Threading;
using System.Threading.Tasks;
using VitalRouter;

namespace SignalRouter
{
    public interface IInteractionCommand : ICommand
    {
        string TargetId { get; }
    }

    public readonly struct ClickCommand : IInteractionCommand, IEquatable<ClickCommand>
    {
        public ClickCommand(string targetId)
        {
            InteractionContract.RequireTargetId(targetId, nameof(targetId));
            TargetId = targetId;
        }

        public string TargetId { get; }

        public bool Equals(ClickCommand other)
        {
            return string.Equals(TargetId, other.TargetId, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is ClickCommand other && Equals(other);
        }

        public override int GetHashCode()
        {
            return TargetId == null ? 0 : StringComparer.Ordinal.GetHashCode(TargetId);
        }

        public static bool operator ==(ClickCommand left, ClickCommand right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ClickCommand left, ClickCommand right)
        {
            return !left.Equals(right);
        }
    }

    public readonly struct SetValueCommand : IInteractionCommand, IEquatable<SetValueCommand>
    {
        public SetValueCommand(string targetId, string value)
        {
            InteractionContract.RequireTargetId(targetId, nameof(targetId));
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            TargetId = targetId;
            Value = value;
        }

        public string TargetId { get; }

        public string Value { get; }

        public bool Equals(SetValueCommand other)
        {
            return string.Equals(TargetId, other.TargetId, StringComparison.Ordinal)
                && string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is SetValueCommand other && Equals(other);
        }

        public override int GetHashCode()
        {
            return InteractionContract.CombineHashCodes(
                TargetId == null ? 0 : StringComparer.Ordinal.GetHashCode(TargetId),
                Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value));
        }

        public static bool operator ==(SetValueCommand left, SetValueCommand right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SetValueCommand left, SetValueCommand right)
        {
            return !left.Equals(right);
        }
    }

    public enum InteractionOrigin
    {
        Human = 0,
        Agent = 1,
        Replay = 2,
        Test = 3,
    }

    public readonly struct InteractionDispatchOptions : IEquatable<InteractionDispatchOptions>
    {
        public InteractionDispatchOptions(
            InteractionOrigin origin,
            string? correlationId = null,
            string? idempotencyKey = null)
        {
            InteractionContract.RequireDefinedEnum(origin, nameof(origin));
            InteractionContract.RequireOptionalIdentifier(correlationId, nameof(correlationId));
            InteractionContract.RequireOptionalIdentifier(idempotencyKey, nameof(idempotencyKey));

            Origin = origin;
            CorrelationId = correlationId;
            IdempotencyKey = idempotencyKey;
        }

        public InteractionOrigin Origin { get; }

        public string? CorrelationId { get; }

        public string? IdempotencyKey { get; }

        public bool Equals(InteractionDispatchOptions other)
        {
            return Origin == other.Origin
                && string.Equals(CorrelationId, other.CorrelationId, StringComparison.Ordinal)
                && string.Equals(IdempotencyKey, other.IdempotencyKey, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is InteractionDispatchOptions other && Equals(other);
        }

        public override int GetHashCode()
        {
            return InteractionContract.CombineHashCodes(
                (int)Origin,
                CorrelationId == null ? 0 : StringComparer.Ordinal.GetHashCode(CorrelationId),
                IdempotencyKey == null ? 0 : StringComparer.Ordinal.GetHashCode(IdempotencyKey));
        }

        public static bool operator ==(
            InteractionDispatchOptions left,
            InteractionDispatchOptions right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(
            InteractionDispatchOptions left,
            InteractionDispatchOptions right)
        {
            return !left.Equals(right);
        }
    }

    public interface IInteractionDispatcher
    {
        ValueTask<InteractionResult> DispatchAsync<TCommand>(
            TCommand command,
            InteractionDispatchOptions options,
            CancellationToken cancellationToken = default)
            where TCommand : struct, IInteractionCommand;
    }
}
