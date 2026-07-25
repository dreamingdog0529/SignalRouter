using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// The four orthogonal identity fields carried in the admission envelope and
    /// recorded in E2 (semantic-model.md §6). It exists for authorization at
    /// admission, auditing, and evidence — semantics MUST NOT branch on it after
    /// admission (the equivalence axiom).
    /// </summary>
    public sealed class IdentityEnvelope : IEquatable<IdentityEnvelope>
    {
        public IdentityEnvelope(Principal principal, IngressPath ingress, Provenance provenance, Causality causality)
        {
            Principal = principal ?? throw new ArgumentNullException(nameof(principal));
            if (ingress.IsDefault)
            {
                throw new ArgumentException(
                    "IdentityEnvelope requires a non-default ingress path.", nameof(ingress));
            }

            Ingress = ingress;
            Provenance = provenance;
            Causality = causality ?? throw new ArgumentNullException(nameof(causality));
        }

        public Principal Principal { get; }

        public IngressPath Ingress { get; }

        public Provenance Provenance { get; }

        public Causality Causality { get; }

        public bool Equals(IdentityEnvelope? other) =>
            other != null &&
            Principal.Equals(other.Principal) &&
            Ingress.Equals(other.Ingress) &&
            Provenance == other.Provenance &&
            Causality.Equals(other.Causality);

        public override bool Equals(object? obj) => Equals(obj as IdentityEnvelope);

        public override int GetHashCode()
        {
            var hash = ContractGrammar.CombineHashes(Principal.GetHashCode(), Ingress.GetHashCode());
            hash = ContractGrammar.CombineHashes(hash, (int)Provenance);
            return ContractGrammar.CombineHashes(hash, Causality.GetHashCode());
        }
    }
}
