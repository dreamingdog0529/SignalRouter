using System;
using System.Globalization;

namespace SignalRouter.Protocol
{
    // The envelope-wide protocol version (design §18.3). Majors gate
    // compatibility: peers with different majors fail the handshake. Minors are
    // cumulative — implementing minor N implies minors 0..N — which is what makes
    // selecting the lower of two minors safe during negotiation (ADR 0007).
    public readonly struct ProtocolVersion : IEquatable<ProtocolVersion>
    {
        public const int CurrentMajor = 1;
        public const int CurrentMinor = 0;

        public ProtocolVersion(int major, int minor)
        {
            if (major < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(major),
                    major,
                    "Version components must be non-negative.");
            }

            if (minor < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minor),
                    minor,
                    "Version components must be non-negative.");
            }

            Major = major;
            Minor = minor;
        }

        public int Major { get; }

        public int Minor { get; }

        public static ProtocolVersion Current
        {
            get { return new ProtocolVersion(CurrentMajor, CurrentMinor); }
        }

        public bool IsMajorCompatibleWith(ProtocolVersion other)
        {
            return Major == other.Major;
        }

        // Strict "MAJOR.MINOR": invariant decimal digits only, no signs, no
        // whitespace, no leading zeros. Anything else is rejected so a malformed
        // envelope version can never alias a valid one.
        public static bool TryParse(string? text, out ProtocolVersion version)
        {
            version = default;
            if (text == null)
            {
                return false;
            }

            var separator = text.IndexOf('.');
            if (separator <= 0
                || separator == text.Length - 1
                || text.IndexOf('.', separator + 1) >= 0)
            {
                return false;
            }

            if (!TryParseComponent(text, 0, separator, out var major)
                || !TryParseComponent(text, separator + 1, text.Length, out var minor))
            {
                return false;
            }

            version = new ProtocolVersion(major, minor);
            return true;
        }

        public override string ToString()
        {
            return Major.ToString(CultureInfo.InvariantCulture)
                + "." + Minor.ToString(CultureInfo.InvariantCulture);
        }

        public bool Equals(ProtocolVersion other)
        {
            return Major == other.Major && Minor == other.Minor;
        }

        public override bool Equals(object? obj)
        {
            return obj is ProtocolVersion other && Equals(other);
        }

        public override int GetHashCode()
        {
            return ProtocolContract.CombineHashCodes(Major, Minor);
        }

        public static bool operator ==(ProtocolVersion left, ProtocolVersion right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ProtocolVersion left, ProtocolVersion right)
        {
            return !left.Equals(right);
        }

        private static bool TryParseComponent(string text, int start, int end, out int value)
        {
            value = 0;
            var length = end - start;
            if (length == 0 || length > 9)
            {
                return false;
            }

            if (length > 1 && text[start] == '0')
            {
                return false;
            }

            var result = 0;
            for (var index = start; index < end; index++)
            {
                var digit = text[index] - '0';
                if (digit < 0 || digit > 9)
                {
                    return false;
                }

                result = (result * 10) + digit;
            }

            value = result;
            return true;
        }
    }
}
