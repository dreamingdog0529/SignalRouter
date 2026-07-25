using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>The causal category of an admission (semantic-model.md §6).</summary>
    public enum CausalityKind
    {
        /// <summary>Not caused by other controlled work.</summary>
        Root,

        /// <summary>Spawned by a parent interaction's committed continuation (guarantees.md §5.8).</summary>
        Continuation,

        /// <summary>Triggered by something outside the controlled work.</summary>
        ExternalTrigger,
    }

    /// <summary>
    /// "Caused by what?" — one of the four orthogonal identity-envelope fields
    /// (semantic-model.md §6). A continuation MUST carry its
    /// <see cref="ContinuationLink"/>; the constructor-factories make an inconsistent
    /// combination unrepresentable.
    /// </summary>
    public sealed class Causality : IEquatable<Causality>
    {
        private static readonly Causality RootInstance =
            new Causality(CausalityKind.Root, null, null);

        private readonly ContinuationLink? continuation;

        private Causality(CausalityKind kind, ContinuationLink? continuation, string? externalTriggerHint)
        {
            Kind = kind;
            this.continuation = continuation;
            ExternalTriggerHint = externalTriggerHint;
        }

        public CausalityKind Kind { get; }

        /// <summary>Non-null exactly when <see cref="Kind"/> is <see cref="CausalityKind.Continuation"/>.</summary>
        public ContinuationLink? Continuation => continuation;

        /// <summary>Optional hint naming the external trigger; only for <see cref="CausalityKind.ExternalTrigger"/>.</summary>
        public string? ExternalTriggerHint { get; }

        public static Causality Root() => RootInstance;

        public static Causality OfContinuation(ContinuationLink link)
        {
            if (link.IsDefault)
            {
                throw new ArgumentException(
                    "Continuation causality requires a non-default link.", nameof(link));
            }

            return new Causality(CausalityKind.Continuation, link, null);
        }

        public static Causality OfExternalTrigger(string? hint)
        {
            if (hint != null)
            {
                ContractGrammar.ValidateIdentifier(hint, nameof(hint));
            }

            return new Causality(CausalityKind.ExternalTrigger, null, hint);
        }

        public bool Equals(Causality? other) =>
            other != null &&
            Kind == other.Kind &&
            Nullable.Equals(continuation, other.continuation) &&
            string.Equals(ExternalTriggerHint, other.ExternalTriggerHint, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as Causality);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(
                (int)Kind, continuation?.GetHashCode() ?? 0);
            return ContractGrammar.CombineHashes(
                hash,
                ExternalTriggerHint == null
                    ? 0
                    : StringComparer.Ordinal.GetHashCode(ExternalTriggerHint));
        }

        public override string ToString() =>
            Kind == CausalityKind.Continuation ? $"Continuation({continuation})" : Kind.ToString();
    }
}
