using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// One clause's evaluation inside an E8 cut: a stable clause ID with the expected
    /// and actual evaluations (guarantees.md §5.10). Per-clause structured
    /// explanations are diagnostic material and never part of strict comparison.
    /// </summary>
    public readonly struct ClauseEvaluation : IEquatable<ClauseEvaluation>
    {
        public ClauseEvaluation(string clauseId, string expected, string actual)
        {
            ClauseId = ContractGrammar.ValidateIdentifier(clauseId, nameof(clauseId));
            Expected = ContractGrammar.ValidateIdentifier(expected, nameof(expected));
            Actual = ContractGrammar.ValidateIdentifier(actual, nameof(actual));
        }

        public string ClauseId { get; }

        public string Expected { get; }

        public string Actual { get; }

        public bool Equals(ClauseEvaluation other) =>
            string.Equals(ClauseId, other.ClauseId, StringComparison.Ordinal) &&
            string.Equals(Expected, other.Expected, StringComparison.Ordinal) &&
            string.Equals(Actual, other.Actual, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ClauseEvaluation other && Equals(other);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(ClauseId),
                StringComparer.Ordinal.GetHashCode(Expected));
            return ContractGrammar.CombineHashes(hash, StringComparer.Ordinal.GetHashCode(Actual));
        }

        public override string ToString() => $"{ClauseId}: expected {Expected}, actual {Actual}";

        public static bool operator ==(ClauseEvaluation left, ClauseEvaluation right) => left.Equals(right);

        public static bool operator !=(ClauseEvaluation left, ClauseEvaluation right) => !left.Equals(right);
    }

    /// <summary>
    /// E8 — one standalone assertion evaluated while the recording is active
    /// (guarantees.md §5.10). An atomic single cut: it opens no commitment, the close
    /// fence neither waits for nor cancels it, and it imposes no closure obligation
    /// (rule R5).
    /// </summary>
    public sealed class AssertionEvaluated : EvidenceCut
    {
        public AssertionEvaluated(
            EvidenceSequence sequence,
            RuntimeIncarnationId incarnation,
            SourceRevision watermark,
            ViewContractRef view,
            int stateSourceTableVersion,
            string scope,
            SecurityDomainId domain,
            ContentId snapshot,
            bool completeForScope,
            PredicateContractRef predicate,
            ArgumentDigest operands,
            ValueArray<ClauseEvaluation> clauses,
            PredicateEvaluationOutcome outcome,
            ValueArray<string> witnessPaths)
            : base(sequence)
        {
            if (incarnation.IsDefault)
            {
                throw new ArgumentException("E8 requires a non-default incarnation id.", nameof(incarnation));
            }

            if (view.IsDefault)
            {
                throw new ArgumentException("E8 requires a non-default view contract.", nameof(view));
            }

            if (stateSourceTableVersion < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stateSourceTableVersion), "State-source table version must not be negative.");
            }

            if (domain.IsDefault)
            {
                throw new ArgumentException("E8 requires a non-default security domain.", nameof(domain));
            }

            if (snapshot.IsDefault)
            {
                throw new ArgumentException("E8 requires a non-default snapshot ContentId.", nameof(snapshot));
            }

            if (predicate.IsDefault)
            {
                throw new ArgumentException("E8 requires a non-default predicate reference.", nameof(predicate));
            }

            if (operands.IsDefault)
            {
                throw new ArgumentException("E8 requires a non-default operand digest.", nameof(operands));
            }

            Incarnation = incarnation;
            Watermark = watermark;
            View = view;
            StateSourceTableVersion = stateSourceTableVersion;
            Scope = ContractGrammar.ValidateIdentifier(scope, nameof(scope));
            Domain = domain;
            Snapshot = snapshot;
            CompleteForScope = completeForScope;
            Predicate = predicate;
            Operands = operands;
            Clauses = clauses;
            Outcome = outcome;
            WitnessPaths = witnessPaths;
        }

        public override EvidenceCutKind Kind => EvidenceCutKind.AssertionEvaluated;

        public RuntimeIncarnationId Incarnation { get; }

        /// <summary>The SourceRevision/ViewWatermark of the evaluated materialization (observation-state.md §4).</summary>
        public SourceRevision Watermark { get; }

        public ViewContractRef View { get; }

        public int StateSourceTableVersion { get; }

        public string Scope { get; }

        public SecurityDomainId Domain { get; }

        public ContentId Snapshot { get; }

        /// <summary>The evaluated snapshot's completeness for the assertion's scope.</summary>
        public bool CompleteForScope { get; }

        public PredicateContractRef Predicate { get; }

        /// <summary>Redacted operands; secret operands are secret references.</summary>
        public ArgumentDigest Operands { get; }

        public ValueArray<ClauseEvaluation> Clauses { get; }

        public PredicateEvaluationOutcome Outcome { get; }

        /// <summary>Bounded, redacted witness paths.</summary>
        public ValueArray<string> WitnessPaths { get; }
    }
}
