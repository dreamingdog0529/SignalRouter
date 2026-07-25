using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// Cancellation evidence embedded in E4 whenever cancellation contributed to a
    /// terminal (guarantees.md §5.7). The constructor enforces the phase/flag
    /// consistency the phases define: before the effect nothing was permitted or
    /// started; during or after it, both.
    /// </summary>
    public sealed class CancellationEvidence : IEquatable<CancellationEvidence>
    {
        public CancellationEvidence(
            LogicalOrder requestedOrder,
            LogicalOrder observedOrder,
            CancellationPhase phase,
            string disposition,
            bool effectPermitted,
            bool effectStarted)
        {
            if (observedOrder < requestedOrder)
            {
                throw new ArgumentException(
                    "Cancellation cannot be observed at an earlier order than it was requested.",
                    nameof(observedOrder));
            }

            if (phase == CancellationPhase.BeforeEffect && (effectPermitted || effectStarted))
            {
                throw new ArgumentException(
                    "A BeforeEffect cancellation implies no effect was permitted or started.",
                    nameof(phase));
            }

            if (phase != CancellationPhase.BeforeEffect && (!effectPermitted || !effectStarted))
            {
                throw new ArgumentException(
                    "A DuringEffect or AfterEffect cancellation implies the effect was permitted and started.",
                    nameof(phase));
            }

            RequestedOrder = requestedOrder;
            ObservedOrder = observedOrder;
            Phase = phase;
            Disposition = ContractGrammar.ValidateCode(disposition, nameof(disposition));
            EffectPermitted = effectPermitted;
            EffectStarted = effectStarted;
        }

        /// <summary>The logical order at which the cancel was requested.</summary>
        public LogicalOrder RequestedOrder { get; }

        /// <summary>The logical order at which the cancel was observed.</summary>
        public LogicalOrder ObservedOrder { get; }

        public CancellationPhase Phase { get; }

        /// <summary>The stable disposition code of how the cancellation was handled.</summary>
        public string Disposition { get; }

        public bool EffectPermitted { get; }

        public bool EffectStarted { get; }

        public bool Equals(CancellationEvidence? other) =>
            other != null &&
            RequestedOrder.Equals(other.RequestedOrder) &&
            ObservedOrder.Equals(other.ObservedOrder) &&
            Phase == other.Phase &&
            string.Equals(Disposition, other.Disposition, StringComparison.Ordinal) &&
            EffectPermitted == other.EffectPermitted &&
            EffectStarted == other.EffectStarted;

        public override bool Equals(object? obj) => Equals(obj as CancellationEvidence);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(RequestedOrder.GetHashCode(), ObservedOrder.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, (int)Phase);
            hash = ContractGrammar.CombineHashes(hash, StringComparer.Ordinal.GetHashCode(Disposition));
            hash = ContractGrammar.CombineHashes(hash, EffectPermitted ? 1 : 0);
            return ContractGrammar.CombineHashes(hash, EffectStarted ? 1 : 0);
        }

        public override string ToString() => $"{Phase}({Disposition})";
    }
}
