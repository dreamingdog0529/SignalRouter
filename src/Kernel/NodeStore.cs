using System;
using System.Collections.Generic;
using SignalRouter.Contracts;

namespace SignalRouter.Kernel
{
    /// <summary>One live node's state. Pump-thread only.</summary>
    internal sealed class NodeRecord
    {
        internal NodeRecord(NodeRef reference, NodeRegistration registration)
        {
            Reference = reference;
            Registration = registration;
            Attributes = new Dictionary<string, NodeAttribute>(StringComparer.Ordinal);
            foreach (var attribute in registration.Attributes)
            {
                Attributes.Add(attribute.Name, attribute);
            }

            Availability = new Dictionary<CapabilityContractRef, bool>();
            foreach (var declaration in registration.Capabilities)
            {
                Availability.Add(declaration.Contract, declaration.InitiallyAvailable);
            }
        }

        internal NodeRef Reference { get; }

        internal NodeRegistration Registration { get; }

        internal Dictionary<string, NodeAttribute> Attributes { get; }

        internal Dictionary<CapabilityContractRef, bool> Availability { get; }

        internal bool Unregistered { get; set; }
    }

    /// <summary>
    /// The kernel's live node state core (semantic-model.md §1–§3): registration
    /// with fail-fast `AuthorKey` uniqueness, never-reused `NodeRef`s,
    /// AuthorKey⇄NodeRef resolution, capability availability, and the shared
    /// `SourceRevision` clock — advanced by every observable mutation and never by a
    /// refusal or no-op. Pump-thread only; cross-thread callers go through the
    /// mailbox.
    /// </summary>
    internal sealed class NodeStore
    {
        private readonly Dictionary<ulong, NodeRecord> byValue = new Dictionary<ulong, NodeRecord>();
        private readonly Dictionary<AuthorKey, NodeRecord> byAuthorKey = new Dictionary<AuthorKey, NodeRecord>();
        private ulong nextNodeValue = 1;
        private ulong revision;

        internal NodeStore(RuntimeIncarnationId incarnation)
        {
            if (incarnation.IsDefault)
            {
                throw new ArgumentException("A non-default incarnation is required.", nameof(incarnation));
            }

            Incarnation = incarnation;
        }

        internal RuntimeIncarnationId Incarnation { get; }

        internal SourceRevision Revision => new SourceRevision(revision);

        internal void AdvanceRevision()
        {
            revision++;
        }

        internal NodeRef Register(NodeRegistration registration)
        {
            if (registration == null)
            {
                throw new ArgumentNullException(nameof(registration));
            }

            if (registration.AuthorKey.HasValue &&
                byAuthorKey.ContainsKey(registration.AuthorKey.Value))
            {
                throw new ArgumentException(
                    "Duplicate AuthorKey; uniqueness is enforced at registration (semantic-model.md 3.2).",
                    nameof(registration));
            }

            if (registration.Parent.HasValue &&
                !byAuthorKey.ContainsKey(registration.Parent.Value))
            {
                throw new ArgumentException(
                    "Dangling parent reference; a parent must be registered first (semantic-model.md 1).",
                    nameof(registration));
            }

            var reference = new NodeRef(Incarnation, nextNodeValue++);
            var record = new NodeRecord(reference, registration);
            byValue.Add(reference.Value, record);
            if (registration.AuthorKey.HasValue)
            {
                byAuthorKey.Add(registration.AuthorKey.Value, record);
            }

            AdvanceRevision();
            return reference;
        }

        internal bool TryUnregister(NodeRef node)
        {
            if (!TryResolveLive(node, out var record))
            {
                return false;
            }

            record.Unregistered = true;
            byValue.Remove(node.Value);
            if (record.Registration.AuthorKey.HasValue)
            {
                byAuthorKey.Remove(record.Registration.AuthorKey.Value);
            }

            AdvanceRevision();
            return true;
        }

        internal bool TryUpdateAttributes(NodeRef node, ValueArray<NodeAttribute> updates)
        {
            if (updates == null)
            {
                throw new ArgumentNullException(nameof(updates));
            }

            if (!TryResolveLive(node, out var record))
            {
                return false;
            }

            // The sensitivity ratchet: an update may raise sensitivity relative to
            // the registered attribute, never lower it (semantic-model.md 7).
            foreach (var update in updates)
            {
                if (record.Attributes.TryGetValue(update.Name, out var existing) &&
                    existing.Sensitivity == Sensitivity.Sensitive &&
                    update.Sensitivity == Sensitivity.Standard)
                {
                    return false;
                }
            }

            foreach (var update in updates)
            {
                record.Attributes[update.Name] = update;
            }

            AdvanceRevision();
            return true;
        }

        internal bool TrySetAvailability(NodeRef node, CapabilityContractRef capability, bool available)
        {
            if (!TryResolveLive(node, out var record))
            {
                return false;
            }

            if (!record.Availability.TryGetValue(capability, out var current))
            {
                return false;
            }

            if (current == available)
            {
                // A no-op never advances the revision.
                return true;
            }

            record.Availability[capability] = available;
            AdvanceRevision();
            return true;
        }

        internal bool TryResolveLive(NodeRef node, out NodeRecord record)
        {
            if (!node.IsDefault &&
                node.Incarnation.Equals(Incarnation) &&
                byValue.TryGetValue(node.Value, out record!))
            {
                return true;
            }

            record = null!;
            return false;
        }

        internal bool TryResolveByKey(AuthorKey key, out NodeRecord record)
        {
            return byAuthorKey.TryGetValue(key, out record!);
        }

        internal IEnumerable<NodeRecord> LiveRecords => byValue.Values;
    }
}
