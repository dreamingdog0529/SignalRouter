using System;
using System.Collections.Generic;
using SignalRouter.V2.AdapterSdk;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>
    /// A fail-fast kernel fault (security-resources.md §5): control-lane overflow,
    /// a non-monotonic clock reading, concurrent pumping — conditions the kernel
    /// must never paper over.
    /// </summary>
    public sealed class KernelFaultException : Exception
    {
        public KernelFaultException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Binds a principal kind to the security domain its observations evaluate in.</summary>
    public readonly struct PrincipalDomainBinding : IEquatable<PrincipalDomainBinding>
    {
        public PrincipalDomainBinding(string principalKind, SecurityDomainId domain)
        {
            PrincipalKind = ContractGrammar.ValidateCode(principalKind, nameof(principalKind));
            if (domain.IsDefault)
            {
                throw new ArgumentException("Binding requires a non-default domain.", nameof(domain));
            }

            Domain = domain;
        }

        public string PrincipalKind { get; }

        public SecurityDomainId Domain { get; }

        public bool Equals(PrincipalDomainBinding other) =>
            string.Equals(PrincipalKind, other.PrincipalKind, StringComparison.Ordinal) &&
            Domain.Equals(other.Domain);

        public override bool Equals(object? obj) => obj is PrincipalDomainBinding other && Equals(other);

        public override int GetHashCode() =>
            ContractGrammar.CombineHashes(
                StringComparer.Ordinal.GetHashCode(PrincipalKind), Domain.GetHashCode());

        public static bool operator ==(PrincipalDomainBinding left, PrincipalDomainBinding right) => left.Equals(right);

        public static bool operator !=(PrincipalDomainBinding left, PrincipalDomainBinding right) => !left.Equals(right);
    }

    /// <summary>
    /// The kernel's configuration: the `default@1` resource profile
    /// (security-resources.md §5.1), the injected monotonic clock (ADR 0010), the
    /// per-incarnation redaction key for canonicalization, and the
    /// principal-kind → security-domain bindings (default deny: an unbound principal
    /// resolves to no domain and sees nothing).
    /// </summary>
    public sealed class KernelOptions
    {
        private readonly Dictionary<string, SecurityDomainId> domainsByKind;

        public KernelOptions(
            IMonotonicClock monotonicClock,
            byte[] redactionKey,
            ValueList<PrincipalDomainBinding> principalDomains,
            SecurityDomainId recordDomain,
            int mailboxControlCapacity = 4096,
            int mailboxMaxOutstandingPostFenceOperations = 64,
            int mailboxMutationCapacity = 256,
            int sourcePublicationCapacity = 512,
            int sourcePublicationAggregateBytes = 16 * 1024 * 1024,
            int sourcePublicationPendingPerSource = 16,
            int recoveryIndexPendingCapacity = 4096,
            int recoveryIndexTerminalCapacity = 4096,
            long recoveryIndexTerminalRetentionLogicalTime = 300,
            int traceRingCapacity = 8192,
            int traceRingByteCapacity = 4 * 1024 * 1024,
            int maxArmedWaits = 256,
            int maxContinuationsPerParent = 32,
            int maxRegisteredPredicateContracts = 1024)
        {
            MonotonicClock = monotonicClock ?? throw new ArgumentNullException(nameof(monotonicClock));
            if (redactionKey == null || redactionKey.Length == 0)
            {
                throw new ArgumentException("A non-empty redaction key is required.", nameof(redactionKey));
            }

            if (principalDomains == null)
            {
                throw new ArgumentNullException(nameof(principalDomains));
            }

            if (recordDomain.IsDefault)
            {
                throw new ArgumentException("A record domain is required.", nameof(recordDomain));
            }

            if (mailboxControlCapacity < 1 || mailboxMaxOutstandingPostFenceOperations < 1 ||
                mailboxMutationCapacity < 1 || sourcePublicationCapacity < 1 ||
                sourcePublicationAggregateBytes < 1 || sourcePublicationPendingPerSource < 1 ||
                recoveryIndexPendingCapacity < 1 || recoveryIndexTerminalCapacity < 1 ||
                recoveryIndexTerminalRetentionLogicalTime < 1 || traceRingCapacity < 1 ||
                traceRingByteCapacity < 1 || maxArmedWaits < 1 || maxContinuationsPerParent < 1 ||
                maxRegisteredPredicateContracts < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(mailboxControlCapacity), "Every bound must be positive.");
            }

            var copy = new byte[redactionKey.Length];
            Array.Copy(redactionKey, copy, redactionKey.Length);
            RedactionKey = copy;

            domainsByKind = new Dictionary<string, SecurityDomainId>(StringComparer.Ordinal);
            foreach (var binding in principalDomains)
            {
                if (domainsByKind.ContainsKey(binding.PrincipalKind))
                {
                    throw new ArgumentException(
                        "Principal kinds must be bound at most once.", nameof(principalDomains));
                }

                domainsByKind.Add(binding.PrincipalKind, binding.Domain);
            }

            RecordDomain = recordDomain;
            MailboxControlCapacity = mailboxControlCapacity;
            MailboxMaxOutstandingPostFenceOperations = mailboxMaxOutstandingPostFenceOperations;
            MailboxMutationCapacity = mailboxMutationCapacity;
            SourcePublicationCapacity = sourcePublicationCapacity;
            SourcePublicationAggregateBytes = sourcePublicationAggregateBytes;
            SourcePublicationPendingPerSource = sourcePublicationPendingPerSource;
            RecoveryIndexPendingCapacity = recoveryIndexPendingCapacity;
            RecoveryIndexTerminalCapacity = recoveryIndexTerminalCapacity;
            RecoveryIndexTerminalRetentionLogicalTime = recoveryIndexTerminalRetentionLogicalTime;
            TraceRingCapacity = traceRingCapacity;
            TraceRingByteCapacity = traceRingByteCapacity;
            MaxArmedWaits = maxArmedWaits;
            MaxContinuationsPerParent = maxContinuationsPerParent;
            MaxRegisteredPredicateContracts = maxRegisteredPredicateContracts;
        }

        public IMonotonicClock MonotonicClock { get; }

        internal byte[] RedactionKey { get; }

        public SecurityDomainId RecordDomain { get; }

        public int MailboxControlCapacity { get; }

        public int MailboxMaxOutstandingPostFenceOperations { get; }

        public int MailboxMutationCapacity { get; }

        public int SourcePublicationCapacity { get; }

        public int SourcePublicationAggregateBytes { get; }

        public int SourcePublicationPendingPerSource { get; }

        public int RecoveryIndexPendingCapacity { get; }

        public int RecoveryIndexTerminalCapacity { get; }

        public long RecoveryIndexTerminalRetentionLogicalTime { get; }

        public int TraceRingCapacity { get; }

        public int TraceRingByteCapacity { get; }

        public int MaxArmedWaits { get; }

        public int MaxContinuationsPerParent { get; }

        public int MaxRegisteredPredicateContracts { get; }

        /// <summary>Default deny: false when the principal's kind has no bound domain.</summary>
        public bool TryResolveDomain(Principal principal, out SecurityDomainId domain)
        {
            if (principal == null)
            {
                throw new ArgumentNullException(nameof(principal));
            }

            return domainsByKind.TryGetValue(principal.Kind, out domain);
        }
    }
}
