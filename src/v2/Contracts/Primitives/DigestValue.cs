using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// An immutable digest payload with structural equality. The text encoding of a
    /// digest (hex, base64, binary framing) is deliberately not fixed here: encodings
    /// are codec-boundary decisions (ADR 0007); this type owns only the bytes and
    /// their equality. <see cref="ToString"/> renders lowercase hex for diagnostics
    /// without making that rendering contractual.
    /// </summary>
    public readonly struct DigestValue : IEquatable<DigestValue>
    {
        private readonly byte[]? value;

        private DigestValue(byte[] value)
        {
            this.value = value;
        }

        /// <summary>True for the uninitialized <c>default</c> value, which carries no bytes.</summary>
        public bool IsDefault => value == null;

        public int Length => value?.Length ?? 0;

        /// <summary>Copies <paramref name="bytes"/>; the digest must be non-empty.</summary>
        public static DigestValue From(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length == 0)
            {
                throw new ArgumentException("Digest must not be empty.", nameof(bytes));
            }

            var copy = new byte[bytes.Length];
            Array.Copy(bytes, copy, bytes.Length);
            return new DigestValue(copy);
        }

        /// <summary>Returns a defensive copy of the digest bytes.</summary>
        public byte[] ToArray()
        {
            if (value == null)
            {
                throw new InvalidOperationException("A default DigestValue carries no bytes.");
            }

            var copy = new byte[value.Length];
            Array.Copy(value, copy, value.Length);
            return copy;
        }

        public bool Equals(DigestValue other)
        {
            if (value == null || other.value == null)
            {
                return value == other.value;
            }

            if (value.Length != other.value.Length)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != other.value[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is DigestValue other && Equals(other);
        }

        public override int GetHashCode()
        {
            if (value == null)
            {
                return 0;
            }

            var hash = 17;
            foreach (var b in value)
            {
                hash = ContractGrammar.CombineHashes(hash, b);
            }

            return hash;
        }

        public override string ToString()
        {
            if (value == null)
            {
                return "(default)";
            }

            var characters = new char[value.Length * 2];
            const string hex = "0123456789abcdef";
            for (var i = 0; i < value.Length; i++)
            {
                characters[i * 2] = hex[value[i] >> 4];
                characters[(i * 2) + 1] = hex[value[i] & 0x0F];
            }

            return new string(characters);
        }

        public static bool operator ==(DigestValue left, DigestValue right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(DigestValue left, DigestValue right)
        {
            return !left.Equals(right);
        }
    }
}
