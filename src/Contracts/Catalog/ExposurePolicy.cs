using System;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// Which security domains may see a node, capability, or state surface.
    /// Nothing is visible by default (security-resources.md §4): an empty policy
    /// exposes to no one.
    /// </summary>
    public sealed class ExposurePolicy
    {
        public ExposurePolicy(ValueArray<SecurityDomainId> visibleTo)
        {
            VisibleTo = visibleTo;
        }

        /// <summary>The default-deny policy: visible to no domain.</summary>
        public static ExposurePolicy Hidden { get; } = new ExposurePolicy(ValueArray<SecurityDomainId>.Empty);

        public ValueArray<SecurityDomainId> VisibleTo { get; }

        public bool IsVisibleTo(SecurityDomainId domain)
        {
            if (domain.IsDefault)
            {
                throw new ArgumentException("Domain must be non-default.", nameof(domain));
            }

            foreach (var visible in VisibleTo)
            {
                if (visible.Equals(domain))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
