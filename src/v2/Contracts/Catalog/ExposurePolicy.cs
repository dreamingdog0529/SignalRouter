using System;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// Which security domains may see a node, capability, or state surface.
    /// Nothing is visible by default (security-resources.md §4): an empty policy
    /// exposes to no one.
    /// </summary>
    public sealed class ExposurePolicy
    {
        public ExposurePolicy(ValueList<SecurityDomainId> visibleTo)
        {
            VisibleTo = visibleTo ?? throw new ArgumentNullException(nameof(visibleTo));
        }

        /// <summary>The default-deny policy: visible to no domain.</summary>
        public static ExposurePolicy Hidden { get; } = new ExposurePolicy(ValueList<SecurityDomainId>.Empty);

        public ValueList<SecurityDomainId> VisibleTo { get; }

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
