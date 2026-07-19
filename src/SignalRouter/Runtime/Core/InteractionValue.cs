using System;

namespace SignalRouter
{
    public enum InteractionValueKind
    {
        Null = 0,
        String = 1,
        Boolean = 2,
        Number = 3,
    }

    public sealed class InteractionValue : IEquatable<InteractionValue>
    {
        private static readonly InteractionValue NullInstance =
            new InteractionValue(InteractionValueKind.Null, null, false, 0m);

        private readonly string? stringValue;
        private readonly bool booleanValue;
        private readonly decimal numberValue;

        private InteractionValue(
            InteractionValueKind kind,
            string? stringValue,
            bool booleanValue,
            decimal numberValue)
        {
            Kind = kind;
            this.stringValue = stringValue;
            this.booleanValue = booleanValue;
            this.numberValue = numberValue;
        }

        public InteractionValueKind Kind { get; }

        public static InteractionValue Null
        {
            get { return NullInstance; }
        }

        public static InteractionValue FromString(string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            return new InteractionValue(InteractionValueKind.String, value, false, 0m);
        }

        public static InteractionValue FromBoolean(bool value)
        {
            return new InteractionValue(InteractionValueKind.Boolean, null, value, 0m);
        }

        public static InteractionValue FromNumber(decimal value)
        {
            return new InteractionValue(InteractionValueKind.Number, null, false, value);
        }

        public string GetString()
        {
            RequireKind(InteractionValueKind.String);
            return stringValue!;
        }

        public bool GetBoolean()
        {
            RequireKind(InteractionValueKind.Boolean);
            return booleanValue;
        }

        public decimal GetNumber()
        {
            RequireKind(InteractionValueKind.Number);
            return numberValue;
        }

        public bool Equals(InteractionValue? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other == null || Kind != other.Kind)
            {
                return false;
            }

            switch (Kind)
            {
                case InteractionValueKind.Null:
                    return true;
                case InteractionValueKind.String:
                    return string.Equals(stringValue, other.stringValue, StringComparison.Ordinal);
                case InteractionValueKind.Boolean:
                    return booleanValue == other.booleanValue;
                case InteractionValueKind.Number:
                    return numberValue == other.numberValue;
                default:
                    throw new InvalidOperationException("The interaction value kind is invalid.");
            }
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as InteractionValue);
        }

        public override int GetHashCode()
        {
            switch (Kind)
            {
                case InteractionValueKind.Null:
                    return (int)InteractionValueKind.Null;
                case InteractionValueKind.String:
                    return InteractionContract.CombineHashCodes(
                        (int)Kind,
                        StringComparer.Ordinal.GetHashCode(stringValue));
                case InteractionValueKind.Boolean:
                    return InteractionContract.CombineHashCodes(
                        (int)Kind,
                        booleanValue.GetHashCode());
                case InteractionValueKind.Number:
                    return InteractionContract.CombineHashCodes(
                        (int)Kind,
                        numberValue.GetHashCode());
                default:
                    throw new InvalidOperationException("The interaction value kind is invalid.");
            }
        }

        public static bool operator ==(InteractionValue? left, InteractionValue? right)
        {
            if (ReferenceEquals(left, null))
            {
                return ReferenceEquals(right, null);
            }

            return left.Equals(right);
        }

        public static bool operator !=(InteractionValue? left, InteractionValue? right)
        {
            return !(left == right);
        }

        private void RequireKind(InteractionValueKind expected)
        {
            if (Kind != expected)
            {
                throw new InvalidOperationException(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Value kind is {0}; expected {1}.",
                        Kind,
                        expected));
            }
        }
    }
}
