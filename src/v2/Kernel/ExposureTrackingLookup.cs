using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>
    /// Detects, during a recorded assertion, whether the predicate referenced
    /// material outside the record view's exposure (verification.md §3.3): an
    /// OutOfScope <em>field lookup</em> means the assertion cannot produce
    /// evidence and is refused with a distinct error. Redacted lookups and
    /// secret-operand outcomes stay recordable — the record view carries its
    /// redaction marks, so replay reproduces the same Unevaluable answer.
    /// </summary>
    internal sealed class ExposureTrackingLookup : IObservationLookup
    {
        private readonly IObservationLookup inner;

        internal ExposureTrackingLookup(IObservationLookup inner)
        {
            this.inner = inner;
        }

        internal bool OutOfScopeReferenced { get; private set; }

        public ObservationBasis Basis => inner.Basis;

        internal void ResetExposure() => OutOfScopeReferenced = false;

        public FieldLookup Lookup(FieldPath path)
        {
            var answer = inner.Lookup(path);
            if (answer.Kind == FieldLookupKind.OutOfScope)
            {
                OutOfScopeReferenced = true;
            }

            return answer;
        }

        public CollectionCountLookup CountCollection(FieldPath path)
        {
            var answer = inner.CountCollection(path);
            if (answer.Kind == FieldLookupKind.OutOfScope)
            {
                OutOfScopeReferenced = true;
            }

            return answer;
        }
    }
}
