using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// Why a replay comparison could not be evaluated (guarantees.md §3.3, §3.5).
    /// Open vocabulary; canonical codes reserved below. Additionally every
    /// <see cref="UnevaluableReason"/> code is a valid Incomparable code: the §3.3
    /// mapping of a live <c>Unevaluable(reason)</c> preserves the reason verbatim —
    /// see <see cref="FromUnevaluable"/>, the single spec-mandated cross-axis mapping.
    /// </summary>
    public readonly struct IncomparableReason : IEquatable<IncomparableReason>
    {
        private readonly string? value;

        public IncomparableReason(string value)
        {
            this.value = ContractGrammar.ValidateCode(value, nameof(value));
        }

        /// <summary>The replayer does not support the pinned profile version.</summary>
        public static IncomparableReason UnsupportedProfileVersion => new IncomparableReason("UnsupportedProfileVersion");

        /// <summary>The view is incomplete in a region the profile requires.</summary>
        public static IncomparableReason Incompleteness => new IncomparableReason("Incompleteness");

        /// <summary>The artifact carries an unknown extension the profile marks mandatory.</summary>
        public static IncomparableReason UnknownMandatoryExtension => new IncomparableReason("UnknownMandatoryExtension");

        /// <summary>No projection exists onto a common comparison profile.</summary>
        public static IncomparableReason MissingMigration => new IncomparableReason("MissingMigration");

        /// <summary>The position lies at or beyond a contamination interval (guarantees.md §5.5).</summary>
        public static IncomparableReason Contamination => new IncomparableReason("Contamination");

        /// <summary>A DuringEffect cancellation, or a Cancelled terminal with phase AfterEffect (guarantees.md §5.7).</summary>
        public static IncomparableReason CancellationTiming => new IncomparableReason("CancellationTiming");

        /// <summary>A temporal predicate without interval evidence (guarantees.md §5.6).</summary>
        public static IncomparableReason TemporalPredicate => new IncomparableReason("TemporalPredicate");

        /// <summary>A recorded E6 resolution of Faulted (guarantees.md §5.6).</summary>
        public static IncomparableReason PredicateFault => new IncomparableReason("PredicateFault");

        /// <summary>
        /// The single spec-mandated cross-axis mapping (guarantees.md §3.3): a live
        /// <c>Unevaluable(reason)</c> answers <c>Incomparable(reason)</c> at replay,
        /// preserving the reason string verbatim.
        /// </summary>
        public static IncomparableReason FromUnevaluable(UnevaluableReason reason)
        {
            if (reason.IsDefault)
            {
                throw new ArgumentException(
                    "Cannot map a default UnevaluableReason.", nameof(reason));
            }

            return new IncomparableReason(reason.Value);
        }

        public bool IsDefault => value == null;

        public string Value => value ?? throw new InvalidOperationException(
            "A default IncomparableReason carries no value.");

        /// <summary>
        /// True when this is one of the guarantees.md §3.5 reserved codes — including,
        /// per the inclusion rule, every canonical <see cref="UnevaluableReason"/> code.
        /// </summary>
        public bool IsCanonical
        {
            get
            {
                if (Equals(UnsupportedProfileVersion) || Equals(Incompleteness) ||
                    Equals(UnknownMandatoryExtension) || Equals(MissingMigration) ||
                    Equals(Contamination) || Equals(CancellationTiming) ||
                    Equals(TemporalPredicate) || Equals(PredicateFault))
                {
                    return true;
                }

                return value != null && new UnevaluableReason(value).IsCanonical;
            }
        }

        public bool Equals(IncomparableReason other) => string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is IncomparableReason other && Equals(other);

        public override int GetHashCode() => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "(default)";

        public static bool operator ==(IncomparableReason left, IncomparableReason right) => left.Equals(right);

        public static bool operator !=(IncomparableReason left, IncomparableReason right) => !left.Equals(right);
    }
}
