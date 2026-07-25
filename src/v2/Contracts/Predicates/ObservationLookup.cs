using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The identity of the observation materialization an evaluation runs against
    /// (observation-state.md §2 as far as item 2 can express it — the `ContentId`
    /// leg of the snapshot tuple arrives with the codec module).
    /// </summary>
    public sealed class ObservationBasis : IEquatable<ObservationBasis>
    {
        public ObservationBasis(
            RuntimeIncarnationId incarnation,
            SourceRevision revision,
            ViewContractRef view,
            SecurityDomainId domain,
            string scope)
        {
            if (incarnation.IsDefault)
            {
                throw new ArgumentException(
                    "ObservationBasis requires a non-default incarnation.", nameof(incarnation));
            }

            if (view.IsDefault)
            {
                throw new ArgumentException(
                    "ObservationBasis requires a non-default view contract.", nameof(view));
            }

            if (domain.IsDefault)
            {
                throw new ArgumentException(
                    "ObservationBasis requires a non-default security domain.", nameof(domain));
            }

            Incarnation = incarnation;
            Revision = revision;
            View = view;
            Domain = domain;
            Scope = ContractGrammar.ValidateIdentifier(scope, nameof(scope));
        }

        public RuntimeIncarnationId Incarnation { get; }

        /// <summary>The pinned SourceRevision — also the basis's view watermark (observation-state.md §4).</summary>
        public SourceRevision Revision { get; }

        public ViewContractRef View { get; }

        public SecurityDomainId Domain { get; }

        public string Scope { get; }

        public bool Equals(ObservationBasis? other) =>
            other != null &&
            Incarnation.Equals(other.Incarnation) &&
            Revision.Equals(other.Revision) &&
            View.Equals(other.View) &&
            Domain.Equals(other.Domain) &&
            string.Equals(Scope, other.Scope, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as ObservationBasis);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(Incarnation.GetHashCode(), Revision.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, View.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, Domain.GetHashCode());
            return ContractGrammar.CombineHashes(hash, StringComparer.Ordinal.GetHashCode(Scope));
        }

        public override string ToString() => $"{Incarnation}@{Revision}/{View}/{Domain}/{Scope}";
    }

    /// <summary>The kind of answer a field lookup gives.</summary>
    public enum FieldLookupKind
    {
        /// <summary>The field exists and carries a value (possibly the explicit null value).</summary>
        Present,

        /// <summary>The field does not exist at this path.</summary>
        Absent,

        /// <summary>The field exists but its value is withheld by redaction policy.</summary>
        Redacted,

        /// <summary>The path is outside the basis's scope or exposure policy.</summary>
        OutOfScope,

        /// <summary>The region is not materialized; the reason says why.</summary>
        Incomplete,
    }

    /// <summary>
    /// One field lookup answer. Redaction and scope are lookup answers, never
    /// values, which is what makes the no-boolean-oracle rule enforceable
    /// (verification.md §2.3).
    /// </summary>
    public readonly struct FieldLookup : IEquatable<FieldLookup>
    {
        private readonly FieldValue value;
        private readonly CompletenessReason reason;

        private FieldLookup(FieldLookupKind kind, FieldValue value, CompletenessReason reason)
        {
            Kind = kind;
            this.value = value;
            this.reason = reason;
        }

        public static FieldLookup Present(FieldValue value)
        {
            if (value.IsDefault)
            {
                throw new ArgumentException("A present lookup requires a value.", nameof(value));
            }

            return new FieldLookup(FieldLookupKind.Present, value, default);
        }

        public static FieldLookup Absent => new FieldLookup(FieldLookupKind.Absent, default, default);

        public static FieldLookup Redacted => new FieldLookup(FieldLookupKind.Redacted, default, default);

        public static FieldLookup OutOfScope => new FieldLookup(FieldLookupKind.OutOfScope, default, default);

        public static FieldLookup Incomplete(CompletenessReason reason) =>
            new FieldLookup(FieldLookupKind.Incomplete, default, reason);

        public FieldLookupKind Kind { get; }

        public FieldValue Value =>
            Kind == FieldLookupKind.Present
                ? value
                : throw new InvalidOperationException("Only a present lookup carries a value.");

        public CompletenessReason IncompleteReason =>
            Kind == FieldLookupKind.Incomplete
                ? reason
                : throw new InvalidOperationException("Only an incomplete lookup carries a reason.");

        /// <summary>The Unevaluable reason this non-present answer maps to (guarantees.md §3.5).</summary>
        public UnevaluableReason ToUnevaluable()
        {
            switch (Kind)
            {
                case FieldLookupKind.Redacted:
                    return UnevaluableReason.Redacted;
                case FieldLookupKind.OutOfScope:
                    return UnevaluableReason.OutOfScope;
                case FieldLookupKind.Incomplete:
                    return CompletenessReasons.ToUnevaluable(reason);
                default:
                    throw new InvalidOperationException(
                        "Present and absent lookups are evaluable; they map to no Unevaluable reason.");
            }
        }

        public bool Equals(FieldLookup other) =>
            Kind == other.Kind && value.Equals(other.value) && reason == other.reason;

        public override bool Equals(object? obj) => obj is FieldLookup other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes((int)Kind, value.GetHashCode());
            return ContractGrammar.CombineHashes(hash, (int)reason);
        }

        public override string ToString() =>
            Kind == FieldLookupKind.Present ? $"Present({value})" :
            Kind == FieldLookupKind.Incomplete ? $"Incomplete({reason})" : Kind.ToString();

        public static bool operator ==(FieldLookup left, FieldLookup right) => left.Equals(right);

        public static bool operator !=(FieldLookup left, FieldLookup right) => !left.Equals(right);
    }

    /// <summary>A keyed-collection count answer, mirroring <see cref="FieldLookup"/>.</summary>
    public readonly struct CollectionCountLookup : IEquatable<CollectionCountLookup>
    {
        private readonly int count;
        private readonly CompletenessReason reason;

        private CollectionCountLookup(FieldLookupKind kind, int count, CompletenessReason reason)
        {
            Kind = kind;
            this.count = count;
            this.reason = reason;
        }

        public static CollectionCountLookup Present(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            return new CollectionCountLookup(FieldLookupKind.Present, count, default);
        }

        public static CollectionCountLookup Absent =>
            new CollectionCountLookup(FieldLookupKind.Absent, 0, default);

        public static CollectionCountLookup Redacted =>
            new CollectionCountLookup(FieldLookupKind.Redacted, 0, default);

        public static CollectionCountLookup OutOfScope =>
            new CollectionCountLookup(FieldLookupKind.OutOfScope, 0, default);

        public static CollectionCountLookup Incomplete(CompletenessReason reason) =>
            new CollectionCountLookup(FieldLookupKind.Incomplete, 0, reason);

        public FieldLookupKind Kind { get; }

        public int Count =>
            Kind == FieldLookupKind.Present
                ? count
                : throw new InvalidOperationException("Only a present lookup carries a count.");

        public CompletenessReason IncompleteReason =>
            Kind == FieldLookupKind.Incomplete
                ? reason
                : throw new InvalidOperationException("Only an incomplete lookup carries a reason.");

        /// <summary>The Unevaluable reason this non-present answer maps to (guarantees.md §3.5).</summary>
        public UnevaluableReason ToUnevaluable()
        {
            switch (Kind)
            {
                case FieldLookupKind.Redacted:
                    return UnevaluableReason.Redacted;
                case FieldLookupKind.OutOfScope:
                    return UnevaluableReason.OutOfScope;
                case FieldLookupKind.Incomplete:
                    return CompletenessReasons.ToUnevaluable(reason);
                default:
                    throw new InvalidOperationException(
                        "Present and absent lookups are evaluable; they map to no Unevaluable reason.");
            }
        }

        public bool Equals(CollectionCountLookup other) =>
            Kind == other.Kind && count == other.count && reason == other.reason;

        public override bool Equals(object? obj) => obj is CollectionCountLookup other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes((int)Kind, count);
            return ContractGrammar.CombineHashes(hash, (int)reason);
        }

        public static bool operator ==(CollectionCountLookup left, CollectionCountLookup right) => left.Equals(right);

        public static bool operator !=(CollectionCountLookup left, CollectionCountLookup right) => !left.Equals(right);
    }

    /// <summary>
    /// The evaluation seam between the predicate evaluator and whatever produces
    /// materializations: item 2's kernel supplies a pinned raw read; item 3's view
    /// projections implement the same interface. Implementations MUST be snapshot-local
    /// and pure — same basis, same answers.
    /// </summary>
    public interface IObservationLookup
    {
        ObservationBasis Basis { get; }

        FieldLookup Lookup(FieldPath path);

        /// <summary>Bounded keyed-collection count at <paramref name="path"/> (verification.md §2.2).</summary>
        CollectionCountLookup CountCollection(FieldPath path);
    }
}
