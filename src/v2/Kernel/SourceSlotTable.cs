using System;
using System.Collections.Generic;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>
    /// The state-source slots (observation-state.md §7): one immutable document per
    /// registered source. Adoption of a revision-bound publication swaps the
    /// document and advances the shared `SourceRevision` in one pump-thread step —
    /// observers can never see a torn document. Sampled sources hold no slot; they
    /// are read at materialization time through their registered reader.
    /// </summary>
    internal sealed class SourceSlotTable
    {
        private readonly Dictionary<StateSourceKey, StateSourceRegistration> registrations =
            new Dictionary<StateSourceKey, StateSourceRegistration>();

        private readonly Dictionary<StateSourceKey, SourceDocument> documents =
            new Dictionary<StateSourceKey, SourceDocument>();

        internal void Register(StateSourceRegistration registration)
        {
            if (registration == null)
            {
                throw new ArgumentNullException(nameof(registration));
            }

            if (registrations.ContainsKey(registration.Key))
            {
                throw new ArgumentException(
                    "Duplicate state-source key; uniqueness is enforced at registration (semantic-model.md 8).",
                    nameof(registration));
            }

            registrations.Add(registration.Key, registration);
        }

        internal bool TryGetRegistration(StateSourceKey key, out StateSourceRegistration registration)
        {
            return registrations.TryGetValue(key, out registration!);
        }

        /// <summary>
        /// Pump-thread adoption: the document is validated against its contract
        /// (declared fields, types, byte ceiling — security-resources.md §5),
        /// redacted at production (sensitive values never enter the slot,
        /// semantic-model.md §7), and only then swapped with the revision advance
        /// in one step. Invalid publications never partially swap.
        /// </summary>
        internal bool TryAdopt(StateSourceKey key, SourceDocument document, int approximateBytes, NodeStore store)
        {
            if (!registrations.TryGetValue(key, out var registration) ||
                registration.SourceClass != StateSourceClass.RevisionBound)
            {
                return false;
            }

            var descriptor = registration.Descriptor;
            if (approximateBytes > descriptor.MaxDocumentBytes)
            {
                return false;
            }

            var retained = new List<NamedField>();
            foreach (var field in document.Fields)
            {
                SourceFieldSchema? declared = null;
                foreach (var schema in descriptor.Fields)
                {
                    if (string.Equals(schema.Name, field.Name, StringComparison.Ordinal))
                    {
                        declared = schema;
                        break;
                    }
                }

                if (declared == null)
                {
                    return false; // undeclared field
                }

                if (field.Value.Kind != FieldValueKind.Null &&
                    !Matches(declared.Value.Type, field.Value.Kind))
                {
                    return false; // runtime type contradicts the schema
                }

                if (declared.Value.Sensitivity != Sensitivity.Sensitive)
                {
                    retained.Add(field);
                }
            }

            documents[key] = new SourceDocument(ValueList<NamedField>.From(retained));
            store.AdvanceRevision();
            return true;
        }

        private static bool Matches(FieldType declared, FieldValueKind actual)
        {
            switch (declared)
            {
                case FieldType.String:
                    return actual == FieldValueKind.String;
                case FieldType.Integer:
                    return actual == FieldValueKind.Integer;
                case FieldType.Boolean:
                    return actual == FieldValueKind.Boolean;
                case FieldType.Float:
                    return actual == FieldValueKind.Float;
                default:
                    return false;
            }
        }

        internal bool TryGetDocument(StateSourceKey key, out SourceDocument document)
        {
            return documents.TryGetValue(key, out document!);
        }

        internal IEnumerable<StateSourceRegistration> Registrations => registrations.Values;

        /// <summary>
        /// Whether any sampled source is exposed to the family. When none is, a
        /// materialization under that family is a pure function of the revision —
        /// the precondition for sharing one evaluation read across armed waits
        /// (observation-state.md §7: sampled sources read at materialization time).
        /// </summary>
        internal bool HasSampledVisibleTo(ViewFamily family)
        {
            foreach (var registration in registrations.Values)
            {
                if (registration.SourceClass != StateSourceClass.Sampled)
                {
                    continue;
                }

                var visible = family == ViewFamily.Record
                    ? registration.Descriptor.RecordVisible
                    : registration.Descriptor.AgentVisible;
                if (visible)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
