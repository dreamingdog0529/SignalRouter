using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// A <c>major.minor</c> contract version. Ordering compares major first, then
    /// minor; equality is exact.
    /// </summary>
    public readonly struct ContractVersion : IEquatable<ContractVersion>, IComparable<ContractVersion>
    {
        public ContractVersion(int major, int minor)
        {
            if (major < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(major), "Major version must not be negative.");
            }

            if (minor < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minor), "Minor version must not be negative.");
            }

            Major = major;
            Minor = minor;
        }

        public int Major { get; }

        public int Minor { get; }

        public int CompareTo(ContractVersion other)
        {
            var byMajor = Major.CompareTo(other.Major);
            return byMajor != 0 ? byMajor : Minor.CompareTo(other.Minor);
        }

        public bool Equals(ContractVersion other) => Major == other.Major && Minor == other.Minor;

        public override bool Equals(object? obj) => obj is ContractVersion other && Equals(other);

        public override int GetHashCode() => ContractGrammar.CombineHashes(Major, Minor);

        public override string ToString() => $"{Major}.{Minor}";

        public static bool operator ==(ContractVersion left, ContractVersion right) => left.Equals(right);

        public static bool operator !=(ContractVersion left, ContractVersion right) => !left.Equals(right);

        public static bool operator <(ContractVersion left, ContractVersion right) => left.CompareTo(right) < 0;

        public static bool operator >(ContractVersion left, ContractVersion right) => left.CompareTo(right) > 0;

        public static bool operator <=(ContractVersion left, ContractVersion right) => left.CompareTo(right) <= 0;

        public static bool operator >=(ContractVersion left, ContractVersion right) => left.CompareTo(right) >= 0;
    }
}
