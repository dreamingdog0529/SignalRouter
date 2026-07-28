using System;
using System.Collections.Generic;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel
{
    /// <summary>
    /// The projector's reusable working buffers (performance-track plan P3e,
    /// ADR 0013): runtime-owned exclusive storage, reset per use, never a shared
    /// pool rental. Pump-thread only, one materialization at a time — nested use
    /// is a kernel fault, never silent corruption.
    ///
    /// Most buffers' lengths are capped by the materialization ceilings, but
    /// candidate selection scales with the visible node count (unbounded until
    /// `Kernel.MaxLiveNodes` lands), so <see cref="End"/> releases every
    /// reference the buffers hold — a record must never stay rooted past its
    /// materialization (an unregistered node would otherwise survive in
    /// scratch) — and trims any backing storage that grew past the retained
    /// clamp (the ADR 0013 bounded-high-water rule). Contents are
    /// post-redaction values only.
    /// </summary>
    internal sealed class ProjectionScratch
    {
        internal readonly List<CompletenessEntry> Completeness = new List<CompletenessEntry>();
        internal readonly List<NodeRecord> Candidates = new List<NodeRecord>();
        internal readonly List<NodeRecord> Included = new List<NodeRecord>();
        internal readonly HashSet<string> IncludedKeys = new HashSet<string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> ChildCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly List<MaterializedNode> Nodes = new List<MaterializedNode>();
        internal readonly List<MaterializedAttribute> Attributes = new List<MaterializedAttribute>();
        internal readonly List<MaterializedCapability> Capabilities = new List<MaterializedCapability>();
        internal readonly List<StateSourceRegistration> SourceCandidates =
            new List<StateSourceRegistration>();
        internal readonly List<MaterializedSource> Sources = new List<MaterializedSource>();
        internal readonly List<NamedField> Fields = new List<NamedField>();
        internal readonly List<string> RedactedNames = new List<string>();

        /// <summary>
        /// Retained-capacity clamp: matches the default materialization/terminal
        /// scale of the resource profile. Storage that grew past it during one
        /// projection (a large visible-node candidate sweep) trims back on exit.
        /// </summary>
        private const int RetainedCapacityLimit = 4096;

        private bool inUse;

        internal void Begin()
        {
            if (inUse)
            {
                throw new KernelFaultException(
                    "Nested materialization: the projection scratch has a single user.");
            }

            inUse = true;
        }

        internal void End()
        {
            ReleaseAndClamp(Completeness);
            ReleaseAndClamp(Candidates);
            ReleaseAndClamp(Included);
            ReleaseAndClamp(Nodes);
            ReleaseAndClamp(Attributes);
            ReleaseAndClamp(Capabilities);
            ReleaseAndClamp(SourceCandidates);
            ReleaseAndClamp(Sources);
            ReleaseAndClamp(Fields);
            ReleaseAndClamp(RedactedNames);

            var keysOversized = IncludedKeys.Count > RetainedCapacityLimit;
            IncludedKeys.Clear();
            if (keysOversized)
            {
                IncludedKeys.TrimExcess();
            }

            var countsOversized = ChildCounts.Count > RetainedCapacityLimit;
            ChildCounts.Clear();
            if (countsOversized)
            {
                ChildCounts.TrimExcess();
            }

            inUse = false;
        }

        private static void ReleaseAndClamp<T>(List<T> buffer)
        {
            buffer.Clear();
            if (buffer.Capacity > RetainedCapacityLimit)
            {
                buffer.Capacity = RetainedCapacityLimit;
            }
        }
    }
}
