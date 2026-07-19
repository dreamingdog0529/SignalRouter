using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRouter
{
    public sealed class InteractionValidation : IEquatable<InteractionValidation>
    {
        private static readonly InteractionValidation ValidInstance =
            new InteractionValidation(null);

        private InteractionValidation(RejectionInfo? rejection)
        {
            Rejection = rejection;
        }

        public static InteractionValidation Valid
        {
            get { return ValidInstance; }
        }

        public bool IsValid
        {
            get { return Rejection == null; }
        }

        public RejectionInfo? Rejection { get; }

        public static InteractionValidation Reject(
            InteractionRejectionCode code,
            string message)
        {
            return new InteractionValidation(new RejectionInfo(code, message));
        }

        public bool Equals(InteractionValidation? other)
        {
            return other != null && Equals(Rejection, other.Rejection);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as InteractionValidation);
        }

        public override int GetHashCode()
        {
            return Rejection == null ? 0 : Rejection.GetHashCode();
        }
    }

    public sealed class InteractionContext
    {
        internal InteractionContext(
            long sequence,
            string requestId,
            InteractionDispatchOptions options)
        {
            if (sequence < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequence),
                    sequence,
                    "Sequence must be positive.");
            }

            InteractionContract.RequireIdentifier(requestId, nameof(requestId));
            Sequence = sequence;
            RequestId = requestId;
            Options = options;
        }

        public long Sequence { get; }

        public string RequestId { get; }

        public InteractionDispatchOptions Options { get; }
    }

    public interface IInteractionPipeline<TCommand>
        where TCommand : struct, IInteractionCommand
    {
        InteractionValidation Validate(in TCommand command);

        ValueTask ExecuteAsync(
            TCommand command,
            InteractionContext context,
            CancellationToken cancellationToken);
    }

    public interface IInteractionTarget
    {
        string Id { get; }

        InteractionDescriptor Describe();

        bool TryGetPipeline<TCommand>(
            out IInteractionPipeline<TCommand>? pipeline)
            where TCommand : struct, IInteractionCommand;
    }

    public sealed class AvailableInteraction : IEquatable<AvailableInteraction>
    {
        public AvailableInteraction(
            string wireName,
            int version,
            InteractionArgumentSchema arguments)
        {
            WireName = wireName;
            Version = version;
            Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        }

        public string WireName { get; }

        public int Version { get; }

        public InteractionArgumentSchema Arguments { get; }

        public bool Equals(AvailableInteraction? other)
        {
            return other != null
                && string.Equals(WireName, other.WireName, StringComparison.Ordinal)
                && Version == other.Version
                && Arguments.Equals(other.Arguments);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as AvailableInteraction);
        }

        public override int GetHashCode()
        {
            return InteractionContract.CombineHashCodes(
                WireName == null ? 0 : StringComparer.Ordinal.GetHashCode(WireName),
                Version,
                Arguments.GetHashCode());
        }
    }

    public sealed class InteractionDescriptor : IEquatable<InteractionDescriptor>
    {
        private readonly ReadOnlyCollection<AvailableInteraction> availableInteractions;

        public InteractionDescriptor(
            string id,
            string? parentId,
            string role,
            string label,
            InteractionValue? value,
            bool visible,
            bool enabled,
            IEnumerable<AvailableInteraction> availableInteractions)
        {
            if (availableInteractions == null)
            {
                throw new ArgumentNullException(nameof(availableInteractions));
            }

            var copy = new List<AvailableInteraction>();
            foreach (var interaction in availableInteractions)
            {
                if (interaction == null)
                {
                    throw new ArgumentException(
                        "Available interactions must not contain null.",
                        nameof(availableInteractions));
                }

                copy.Add(interaction);
            }

            Id = id;
            ParentId = parentId;
            Role = role;
            Label = label;
            Value = value;
            Visible = visible;
            Enabled = enabled;
            this.availableInteractions =
                new ReadOnlyCollection<AvailableInteraction>(copy.ToArray());
        }

        public string Id { get; }

        public string? ParentId { get; }

        public string Role { get; }

        public string Label { get; }

        public InteractionValue? Value { get; }

        public bool Visible { get; }

        public bool Enabled { get; }

        public IReadOnlyList<AvailableInteraction> AvailableInteractions
        {
            get { return availableInteractions; }
        }

        public bool Equals(InteractionDescriptor? other)
        {
            return other != null
                && string.Equals(Id, other.Id, StringComparison.Ordinal)
                && string.Equals(ParentId, other.ParentId, StringComparison.Ordinal)
                && string.Equals(Role, other.Role, StringComparison.Ordinal)
                && string.Equals(Label, other.Label, StringComparison.Ordinal)
                && Equals(Value, other.Value)
                && Visible == other.Visible
                && Enabled == other.Enabled
                && InteractionContract.SequenceEqual(
                    AvailableInteractions,
                    other.AvailableInteractions);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as InteractionDescriptor);
        }

        public override int GetHashCode()
        {
            var hash = InteractionContract.CombineHashCodes(
                Id == null ? 0 : StringComparer.Ordinal.GetHashCode(Id),
                ParentId == null ? 0 : StringComparer.Ordinal.GetHashCode(ParentId),
                Role == null ? 0 : StringComparer.Ordinal.GetHashCode(Role));
            hash = InteractionContract.CombineHashCodes(
                hash,
                Label == null ? 0 : StringComparer.Ordinal.GetHashCode(Label),
                Value == null ? 0 : Value.GetHashCode());
            hash = InteractionContract.CombineHashCodes(
                hash,
                Visible.GetHashCode(),
                Enabled.GetHashCode());
            return InteractionContract.CombineHashCodes(
                hash,
                InteractionContract.GetSequenceHashCode(AvailableInteractions));
        }

        internal InteractionDescriptor WithAvailableInteractions(
            IEnumerable<AvailableInteraction> interactions)
        {
            return new InteractionDescriptor(
                Id,
                ParentId,
                Role,
                Label,
                Value,
                Visible,
                Enabled,
                interactions);
        }
    }

    public enum InteractionRegistryView
    {
        All = 0,
        Agent = 1,
    }

    public sealed class InteractionRegistrySnapshot :
        IEquatable<InteractionRegistrySnapshot>
    {
        private readonly ReadOnlyCollection<InteractionDescriptor> targets;

        internal InteractionRegistrySnapshot(
            string sessionEpoch,
            long revision,
            IEnumerable<InteractionDescriptor> targets)
        {
            SessionEpoch = sessionEpoch;
            Revision = revision;
            this.targets = new ReadOnlyCollection<InteractionDescriptor>(
                new List<InteractionDescriptor>(targets).ToArray());
        }

        public string SessionEpoch { get; }

        public long Revision { get; }

        public IReadOnlyList<InteractionDescriptor> Targets
        {
            get { return targets; }
        }

        public bool Equals(InteractionRegistrySnapshot? other)
        {
            return other != null
                && string.Equals(SessionEpoch, other.SessionEpoch, StringComparison.Ordinal)
                && Revision == other.Revision
                && InteractionContract.SequenceEqual(Targets, other.Targets);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as InteractionRegistrySnapshot);
        }

        public override int GetHashCode()
        {
            return InteractionContract.CombineHashCodes(
                StringComparer.Ordinal.GetHashCode(SessionEpoch),
                Revision.GetHashCode(),
                InteractionContract.GetSequenceHashCode(Targets));
        }
    }

    public interface IInteractionTargetRegistration : IDisposable
    {
        string TargetId { get; }
    }

    public sealed class InteractionRegistry
    {
        private readonly InteractionCommandCatalog catalog;
        private readonly Dictionary<string, TargetEntry> targets =
            new Dictionary<string, TargetEntry>(StringComparer.Ordinal);
        private long revision;

        public InteractionRegistry(
            InteractionCommandCatalog catalog,
            string sessionEpoch)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            InteractionContract.RequireIdentifier(sessionEpoch, nameof(sessionEpoch));
            SessionEpoch = sessionEpoch;
        }

        public string SessionEpoch { get; }

        public long Revision
        {
            get { return revision; }
        }

        public static InteractionRegistry CreateNewSession(
            InteractionCommandCatalog catalog)
        {
            return new InteractionRegistry(catalog, Guid.NewGuid().ToString("N"));
        }

        public IInteractionTargetRegistration Register(
            IInteractionTarget target,
            bool agentVisible)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var targetId = target.Id;
            InteractionContract.RequireTargetId(targetId, nameof(target));
            if (targets.ContainsKey(targetId))
            {
                throw new InvalidOperationException(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Target ID '{0}' is already registered.",
                        targetId));
            }

            ValidateDescriptor(targetId, target, target.Describe());
            var token = new object();
            targets.Add(targetId, new TargetEntry(target, agentVisible, token));
            IncrementRevision();
            return new TargetRegistration(this, targetId, token);
        }

        public bool TryResolve(string targetId, out IInteractionTarget? target)
        {
            if (targetId == null)
            {
                throw new ArgumentNullException(nameof(targetId));
            }

            TargetEntry? entry;
            if (targets.TryGetValue(targetId, out entry))
            {
                target = entry.Target;
                return true;
            }

            target = null;
            return false;
        }

        public void NotifyDescriptorChanged(string targetId)
        {
            if (targetId == null)
            {
                throw new ArgumentNullException(nameof(targetId));
            }

            TargetEntry? entry;
            if (!targets.TryGetValue(targetId, out entry))
            {
                throw new KeyNotFoundException(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Target ID '{0}' is not registered.",
                        targetId));
            }

            ValidateDescriptor(targetId, entry.Target, entry.Target.Describe());
            IncrementRevision();
        }

        public InteractionRegistrySnapshot GetSnapshot(InteractionRegistryView view)
        {
            InteractionContract.RequireDefinedEnum(view, nameof(view));
            var ids = new List<string>(targets.Keys);
            ids.Sort(StringComparer.Ordinal);
            var descriptors = new List<InteractionDescriptor>();

            foreach (var id in ids)
            {
                var entry = targets[id];
                var descriptor = entry.Target.Describe();
                ValidateDescriptor(id, entry.Target, descriptor);
                if (view == InteractionRegistryView.Agent && !entry.AgentVisible)
                {
                    continue;
                }

                if (view == InteractionRegistryView.Agent)
                {
                    descriptor = FilterAgentInteractions(descriptor);
                }

                descriptors.Add(descriptor);
            }

            return new InteractionRegistrySnapshot(SessionEpoch, Revision, descriptors);
        }

        private InteractionDescriptor FilterAgentInteractions(
            InteractionDescriptor descriptor)
        {
            var interactions = new List<AvailableInteraction>();
            foreach (var interaction in descriptor.AvailableInteractions)
            {
                InteractionCommandCatalogEntry? catalogEntry;
                if (!catalog.TryGet(
                    interaction.WireName,
                    interaction.Version,
                    out catalogEntry))
                {
                    throw new InvalidOperationException(
                        "Descriptor changed after validation.");
                }

                if (catalogEntry!.AgentVisible)
                {
                    interactions.Add(interaction);
                }
            }

            return descriptor.WithAvailableInteractions(interactions);
        }

        private void ValidateDescriptor(
            string registeredId,
            IInteractionTarget target,
            InteractionDescriptor? descriptor)
        {
            if (descriptor == null)
            {
                throw new InvalidOperationException(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Target '{0}' returned a null descriptor.",
                        target.Id));
            }

            InteractionContract.RequireTargetId(target.Id, nameof(target));
            InteractionContract.RequireTargetId(descriptor.Id, nameof(descriptor));
            if (!string.Equals(registeredId, target.Id, StringComparison.Ordinal)
                || !string.Equals(registeredId, descriptor.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Registered target ID '{0}' does not match target ID '{1}' or descriptor ID '{2}'.",
                        registeredId,
                        target.Id,
                        descriptor.Id));
            }

            InteractionContract.RequireOptionalIdentifier(
                descriptor.ParentId,
                nameof(descriptor));
            if (string.Equals(descriptor.Id, descriptor.ParentId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A target cannot be its own parent.");
            }

            InteractionContract.RequireIdentifier(descriptor.Role, nameof(descriptor));
            if (descriptor.Label == null)
            {
                throw new InvalidOperationException("A descriptor label must not be null.");
            }

            if (descriptor.AvailableInteractions.Count == 0)
            {
                throw new InvalidOperationException(
                    "A registered interaction target must expose at least one operation.");
            }

            var identities = new HashSet<CommandIdentity>();
            foreach (var interaction in descriptor.AvailableInteractions)
            {
                InteractionContract.RequireIdentifier(
                    interaction.WireName,
                    nameof(descriptor));
                if (interaction.Version < 1)
                {
                    throw new InvalidOperationException(
                        "Descriptor command versions must be positive.");
                }

                var identity = new CommandIdentity(
                    interaction.WireName,
                    interaction.Version);
                if (!identities.Add(identity))
                {
                    throw new InvalidOperationException(
                        "Descriptor command identities must be unique.");
                }

                InteractionCommandCatalogEntry? catalogEntry;
                if (!catalog.TryGet(
                    interaction.WireName,
                    interaction.Version,
                    out catalogEntry))
                {
                    throw new InvalidOperationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "Descriptor command '{0}@{1}' is not registered.",
                            interaction.WireName,
                            interaction.Version));
                }

                if (!interaction.Arguments.IsCompatibleWith(catalogEntry!.Arguments))
                {
                    throw new InvalidOperationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "Descriptor schema for '{0}@{1}' does not match the catalog.",
                            interaction.WireName,
                            interaction.Version));
                }

                if (!catalogEntry.SupportsTarget(target))
                {
                    throw new InvalidOperationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "Target '{0}' has no pipeline for '{1}@{2}'.",
                            target.Id,
                            interaction.WireName,
                            interaction.Version));
                }
            }
        }

        private void Unregister(string targetId, object token)
        {
            TargetEntry? entry;
            if (!targets.TryGetValue(targetId, out entry)
                || !ReferenceEquals(entry.Token, token))
            {
                return;
            }

            targets.Remove(targetId);
            IncrementRevision();
        }

        private void IncrementRevision()
        {
            revision = checked(revision + 1);
        }

        private sealed class TargetEntry
        {
            public TargetEntry(
                IInteractionTarget target,
                bool agentVisible,
                object token)
            {
                Target = target;
                AgentVisible = agentVisible;
                Token = token;
            }

            public IInteractionTarget Target { get; }

            public bool AgentVisible { get; }

            public object Token { get; }
        }

        private sealed class TargetRegistration : IInteractionTargetRegistration
        {
            private InteractionRegistry? registry;
            private readonly object token;

            public TargetRegistration(
                InteractionRegistry registry,
                string targetId,
                object token)
            {
                this.registry = registry;
                TargetId = targetId;
                this.token = token;
            }

            public string TargetId { get; }

            public void Dispose()
            {
                var owner = registry;
                if (owner == null)
                {
                    return;
                }

                registry = null;
                owner.Unregister(TargetId, token);
            }
        }
    }
}
