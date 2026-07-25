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

        /// <summary>Pump-thread adoption: document swap + revision advance are one step.</summary>
        internal bool TryAdopt(StateSourceKey key, SourceDocument document, NodeStore store)
        {
            if (!registrations.TryGetValue(key, out var registration) ||
                registration.SourceClass != StateSourceClass.RevisionBound)
            {
                return false;
            }

            documents[key] = document;
            store.AdvanceRevision();
            return true;
        }

        internal bool TryGetDocument(StateSourceKey key, out SourceDocument document)
        {
            return documents.TryGetValue(key, out document!);
        }

        internal IEnumerable<StateSourceRegistration> Registrations => registrations.Values;
    }
}
