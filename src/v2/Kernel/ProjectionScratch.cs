using System;
using System.Collections.Generic;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>
    /// The projector's reusable working buffers (performance-track plan P3e,
    /// ADR 0013): runtime-owned exclusive storage, reset per use, never a shared
    /// pool rental. Pump-thread only, one materialization at a time — nested use
    /// is a kernel fault, never silent corruption.
    ///
    /// Bounded high-water by construction: every list's length is capped by the
    /// materialization ceilings and the per-pump byte budget, so retained
    /// capacity cannot exceed the configured resource profile's own bounds.
    /// Contents are post-redaction values only (sensitive material never enters
    /// a materialized aggregate), and <see cref="List{T}.Clear"/> zeroes the
    /// retained references at the start of every use.
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

        private bool inUse;

        internal void Begin()
        {
            if (inUse)
            {
                throw new KernelFaultException(
                    "Nested materialization: the projection scratch has a single user.");
            }

            inUse = true;
            Completeness.Clear();
            Candidates.Clear();
            Included.Clear();
            IncludedKeys.Clear();
            ChildCounts.Clear();
            Nodes.Clear();
            Attributes.Clear();
            Capabilities.Clear();
            SourceCandidates.Clear();
            Sources.Clear();
            Fields.Clear();
            RedactedNames.Clear();
        }

        internal void End() => inUse = false;
    }
}
