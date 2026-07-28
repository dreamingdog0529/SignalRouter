using System;
using System.Collections.Generic;

namespace SignalRouter.Contracts
{
    /// <summary>An immutable state-source document: uniquely named typed fields (observation-state.md §7).</summary>
    public sealed class SourceDocument
    {
        private readonly Dictionary<string, FieldValue> byName;

        public SourceDocument(ValueArray<NamedField> fields)
        {
            byName = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (byName.ContainsKey(field.Name))
                {
                    throw new ArgumentException("Document field names must be unique.", nameof(fields));
                }

                byName.Add(field.Name, field.Value);
            }

            Fields = fields;
        }

        public ValueArray<NamedField> Fields { get; }

        public bool TryGetValue(string name, out FieldValue value)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            return byName.TryGetValue(name, out value);
        }
    }

    /// <summary>
    /// The read contract of a sampled state source (observation-state.md §7.1): the
    /// document is read at materialization time and MAY consult external state.
    /// Freshness is judged against the reading's age; a document older than the
    /// declared bound surfaces as `Stale` completeness. Pump thread only.
    /// </summary>
    public interface ISampledSourceReader
    {
        /// <summary>
        /// The current document and the logical time it was produced at, or null
        /// when the source has no document (`SourceUnavailable`).
        /// </summary>
        SampledDocument? Read();
    }

    /// <summary>One sampled reading: the document plus its production time in host logical units.</summary>
    public sealed class SampledDocument
    {
        public SampledDocument(SourceDocument document, long producedAtLogicalTime)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            ProducedAtLogicalTime = producedAtLogicalTime;
        }

        public SourceDocument Document { get; }

        public long ProducedAtLogicalTime { get; }
    }
}
