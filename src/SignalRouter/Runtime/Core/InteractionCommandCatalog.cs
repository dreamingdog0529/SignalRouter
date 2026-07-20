using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRouter
{
    public enum InteractionArgumentType
    {
        String = 0,
        Boolean = 1,
        Number = 2,
    }

    public sealed record InteractionArgumentDefinition
    {
        public InteractionArgumentDefinition(
            string name,
            InteractionArgumentType type,
            bool required,
            bool sensitive)
        {
            Name = name;
            Type = type;
            Required = required;
            Sensitive = sensitive;
        }

        public string Name { get; }

        public InteractionArgumentType Type { get; }

        public bool Required { get; }

        public bool Sensitive { get; }
    }

    public sealed record InteractionArgumentSchema
    {
        private static readonly InteractionArgumentSchema EmptyInstance =
            new InteractionArgumentSchema(Array.Empty<InteractionArgumentDefinition>());

        public InteractionArgumentSchema(IEnumerable<InteractionArgumentDefinition> arguments)
        {
            Arguments = EquatableList<InteractionArgumentDefinition>.Create(
                arguments,
                nameof(arguments),
                "Schema arguments must not contain null.");
        }

        public static InteractionArgumentSchema Empty
        {
            get { return EmptyInstance; }
        }

        public EquatableList<InteractionArgumentDefinition> Arguments { get; }

        public bool IsCompatibleWith(InteractionArgumentSchema catalogSchema)
        {
            if (catalogSchema == null)
            {
                throw new ArgumentNullException(nameof(catalogSchema));
            }

            if (Arguments.Count != catalogSchema.Arguments.Count)
            {
                return false;
            }

            for (var index = 0; index < Arguments.Count; index++)
            {
                var actual = Arguments[index];
                var expected = catalogSchema.Arguments[index];
                if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal)
                    || actual.Type != expected.Type
                    || actual.Required != expected.Required
                    || (expected.Sensitive && !actual.Sensitive))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public interface IInteractionCommandSchema<TCommand>
        where TCommand : struct, IInteractionCommand
    {
        InteractionArgumentSchema Arguments { get; }

        TCommand Decode(string targetId, JsonElement arguments);

        void WriteArguments(Utf8JsonWriter writer, in TCommand command);
    }

    public sealed class InteractionCommandException : ArgumentException
    {
        public InteractionCommandException(
            InteractionRejectionCode rejectionCode,
            string message)
            : base(message)
        {
            InteractionContract.RequireDefinedEnum(rejectionCode, nameof(rejectionCode));
            RejectionCode = rejectionCode;
        }

        public InteractionCommandException(
            InteractionRejectionCode rejectionCode,
            string message,
            Exception innerException)
            : base(message, innerException)
        {
            InteractionContract.RequireDefinedEnum(rejectionCode, nameof(rejectionCode));
            RejectionCode = rejectionCode;
        }

        public InteractionRejectionCode RejectionCode { get; }
    }

    public sealed class ClickCommandSchema : IInteractionCommandSchema<ClickCommand>
    {
        private ClickCommandSchema()
        {
        }

        public static ClickCommandSchema Instance { get; } = new ClickCommandSchema();

        public InteractionArgumentSchema Arguments
        {
            get { return InteractionArgumentSchema.Empty; }
        }

        public ClickCommand Decode(string targetId, JsonElement arguments)
        {
            InteractionJson.RequireObject(arguments);
            foreach (var property in arguments.EnumerateObject())
            {
                throw InteractionJson.Invalid(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Unknown click argument '{0}'.",
                        property.Name));
            }

            try
            {
                return new ClickCommand(targetId);
            }
            catch (ArgumentException exception)
            {
                throw new InteractionCommandException(
                    InteractionRejectionCode.InvalidArguments,
                    exception.Message,
                    exception);
            }
        }

        public void WriteArguments(Utf8JsonWriter writer, in ClickCommand command)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    public sealed class SetValueCommandSchema : IInteractionCommandSchema<SetValueCommand>
    {
        private static readonly InteractionArgumentSchema ArgumentSchema =
            new InteractionArgumentSchema(
                new[]
                {
                    new InteractionArgumentDefinition(
                        "value",
                        InteractionArgumentType.String,
                        true,
                        false),
                });

        private SetValueCommandSchema()
        {
        }

        public static SetValueCommandSchema Instance { get; } =
            new SetValueCommandSchema();

        public InteractionArgumentSchema Arguments
        {
            get { return ArgumentSchema; }
        }

        public SetValueCommand Decode(string targetId, JsonElement arguments)
        {
            InteractionJson.RequireObject(arguments);
            string? value = null;
            var found = false;
            foreach (var property in arguments.EnumerateObject())
            {
                if (!string.Equals(property.Name, "value", StringComparison.Ordinal))
                {
                    throw InteractionJson.Invalid(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "Unknown set_value argument '{0}'.",
                            property.Name));
                }

                if (found)
                {
                    throw InteractionJson.Invalid("Duplicate set_value argument 'value'.");
                }

                found = true;
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw InteractionJson.Invalid(
                        "The set_value argument 'value' must be a string.");
                }

                value = property.Value.GetString();
            }

            if (!found)
            {
                throw InteractionJson.Invalid(
                    "The required set_value argument 'value' is missing.");
            }

            try
            {
                return new SetValueCommand(targetId, value!);
            }
            catch (ArgumentException exception)
            {
                throw new InteractionCommandException(
                    InteractionRejectionCode.InvalidArguments,
                    exception.Message,
                    exception);
            }
        }

        public void WriteArguments(Utf8JsonWriter writer, in SetValueCommand command)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            writer.WriteStartObject();
            writer.WriteString("value", command.Value);
            writer.WriteEndObject();
        }
    }

    public sealed class InteractionCommandCatalogBuilder
    {
        private readonly List<IRegistrationDraft> registrations =
            new List<IRegistrationDraft>();

        public InteractionCommandCatalogBuilder Register<TCommand>(
            string wireName,
            int version,
            IInteractionCommandSchema<TCommand> schema,
            bool agentVisible)
            where TCommand : struct, IInteractionCommand
        {
            registrations.Add(
                new RegistrationDraft<TCommand>(
                    wireName,
                    version,
                    schema,
                    agentVisible));
            return this;
        }

        public InteractionCommandCatalog Build()
        {
            var identities = new HashSet<CommandIdentity>();
            var types = new HashSet<Type>();
            var entries = new List<InteractionCommandCatalogEntry>();

            foreach (var registration in registrations)
            {
                ValidateRegistration(registration);
                var identity = new CommandIdentity(
                    registration.WireName,
                    registration.Version);
                if (!identities.Add(identity))
                {
                    throw new InvalidOperationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "Command identity '{0}@{1}' is already registered.",
                            registration.WireName,
                            registration.Version));
                }

                if (!types.Add(registration.CommandType))
                {
                    throw new InvalidOperationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "Command type '{0}' is already registered.",
                            registration.CommandType.FullName));
                }

                entries.Add(registration.CreateEntry());
            }

            return new InteractionCommandCatalog(entries);
        }

        private static void ValidateRegistration(IRegistrationDraft registration)
        {
            InteractionContract.RequireIdentifier(
                registration.WireName,
                "wireName");
            if (registration.Version < 1)
            {
                throw new ArgumentOutOfRangeException(
                    "version",
                    registration.Version,
                    "Command version must be positive.");
            }

            if (registration.Schema == null)
            {
                throw new ArgumentException("Command schema must not be null.", "schema");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var argument in registration.Schema.Arguments)
            {
                InteractionContract.RequireIdentifier(argument.Name, "schema");
                InteractionContract.RequireDefinedEnum(argument.Type, "schema");
                if (!names.Add(argument.Name))
                {
                    throw new ArgumentException(
                        "Command schema argument names must be unique.",
                        "schema");
                }
            }
        }

        private interface IRegistrationDraft
        {
            string WireName { get; }

            int Version { get; }

            Type CommandType { get; }

            InteractionArgumentSchema? Schema { get; }

            InteractionCommandCatalogEntry CreateEntry();
        }

        private sealed class RegistrationDraft<TCommand> : IRegistrationDraft
            where TCommand : struct, IInteractionCommand
        {
            private readonly IInteractionCommandSchema<TCommand>? schema;
            private readonly bool agentVisible;

            public RegistrationDraft(
                string wireName,
                int version,
                IInteractionCommandSchema<TCommand>? schema,
                bool agentVisible)
            {
                WireName = wireName;
                Version = version;
                this.schema = schema;
                this.agentVisible = agentVisible;
            }

            public string WireName { get; }

            public int Version { get; }

            public Type CommandType
            {
                get { return typeof(TCommand); }
            }

            public InteractionArgumentSchema? Schema
            {
                get { return schema?.Arguments; }
            }

            public InteractionCommandCatalogEntry CreateEntry()
            {
                return new InteractionCommandCatalogEntry<TCommand>(
                    WireName,
                    Version,
                    schema!,
                    agentVisible);
            }
        }
    }

    public sealed class InteractionCommandCatalog
    {
        private readonly ReadOnlyCollection<InteractionCommandCatalogEntry> entries;
        private readonly Dictionary<CommandIdentity, InteractionCommandCatalogEntry> byIdentity;
        private readonly Dictionary<Type, InteractionCommandCatalogEntry> byType;

        internal InteractionCommandCatalog(
            IEnumerable<InteractionCommandCatalogEntry> entries)
        {
            var copy = new List<InteractionCommandCatalogEntry>(entries);
            copy.Sort(
                (left, right) =>
                {
                    var nameComparison =
                        StringComparer.Ordinal.Compare(left.WireName, right.WireName);
                    return nameComparison != 0
                        ? nameComparison
                        : left.Version.CompareTo(right.Version);
                });

            byIdentity = new Dictionary<CommandIdentity, InteractionCommandCatalogEntry>();
            byType = new Dictionary<Type, InteractionCommandCatalogEntry>();
            foreach (var entry in copy)
            {
                byIdentity.Add(
                    new CommandIdentity(entry.WireName, entry.Version),
                    entry);
                byType.Add(entry.CommandType, entry);
            }

            this.entries =
                new ReadOnlyCollection<InteractionCommandCatalogEntry>(copy);
        }

        public IReadOnlyList<InteractionCommandCatalogEntry> Entries
        {
            get { return entries; }
        }

        public static InteractionCommandCatalog CreateMvp()
        {
            return new InteractionCommandCatalogBuilder()
                .Register("click", 1, ClickCommandSchema.Instance, true)
                .Register("set_value", 1, SetValueCommandSchema.Instance, true)
                .Build();
        }

        public bool TryGet(
            string wireName,
            int version,
            out InteractionCommandCatalogEntry? entry)
        {
            if (wireName == null)
            {
                throw new ArgumentNullException(nameof(wireName));
            }

            return byIdentity.TryGetValue(
                new CommandIdentity(wireName, version),
                out entry);
        }

        public InteractionCommandCatalogEntry Get<TCommand>()
            where TCommand : struct, IInteractionCommand
        {
            InteractionCommandCatalogEntry? entry;
            if (!byType.TryGetValue(typeof(TCommand), out entry))
            {
                throw new InteractionCommandException(
                    InteractionRejectionCode.CommandNotRegistered,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Command type '{0}' is not registered.",
                        typeof(TCommand).FullName));
            }

            return entry;
        }

        public DecodedInteractionCommand Decode(
            string wireName,
            int version,
            string targetId,
            JsonElement arguments)
        {
            InteractionCommandCatalogEntry? entry;
            if (!TryGet(wireName, version, out entry))
            {
                throw new InteractionCommandException(
                    InteractionRejectionCode.CommandNotRegistered,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Command '{0}@{1}' is not registered.",
                        wireName,
                        version));
            }

            return entry!.Decode(targetId, arguments);
        }
    }

    public abstract class InteractionCommandCatalogEntry
    {
        protected InteractionCommandCatalogEntry(
            string wireName,
            int version,
            Type commandType,
            InteractionArgumentSchema arguments,
            bool agentVisible)
        {
            WireName = wireName;
            Version = version;
            CommandType = commandType;
            Arguments = arguments;
            AgentVisible = agentVisible;
        }

        public string WireName { get; }

        public int Version { get; }

        public Type CommandType { get; }

        public InteractionArgumentSchema Arguments { get; }

        public bool AgentVisible { get; }

        public abstract DecodedInteractionCommand Decode(
            string targetId,
            JsonElement arguments);

        public abstract void WriteArguments(
            Utf8JsonWriter writer,
            IInteractionCommand command);

        internal abstract ValueTask<InteractionResult> DispatchAsync(
            IInteractionDispatcher dispatcher,
            IInteractionCommand command,
            InteractionDispatchOptions options,
            CancellationToken cancellationToken);

        internal abstract bool SupportsTarget(IInteractionTarget target);
    }

    public sealed class DecodedInteractionCommand
    {
        private readonly InteractionCommandCatalogEntry entry;

        internal DecodedInteractionCommand(
            InteractionCommandCatalogEntry entry,
            IInteractionCommand command)
        {
            this.entry = entry;
            Command = command;
        }

        public string WireName
        {
            get { return entry.WireName; }
        }

        public int Version
        {
            get { return entry.Version; }
        }

        public IInteractionCommand Command { get; }

        public TCommand GetCommand<TCommand>()
            where TCommand : struct, IInteractionCommand
        {
            if (Command is TCommand command)
            {
                return command;
            }

            throw new InvalidOperationException(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Decoded command is '{0}', not '{1}'.",
                    Command.GetType().FullName,
                    typeof(TCommand).FullName));
        }

        public ValueTask<InteractionResult> DispatchAsync(
            IInteractionDispatcher dispatcher,
            InteractionDispatchOptions options,
            CancellationToken cancellationToken = default)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            return entry.DispatchAsync(
                dispatcher,
                Command,
                options,
                cancellationToken);
        }

        public void WriteArguments(Utf8JsonWriter writer)
        {
            entry.WriteArguments(writer, Command);
        }
    }

    internal sealed class InteractionCommandCatalogEntry<TCommand> :
        InteractionCommandCatalogEntry
        where TCommand : struct, IInteractionCommand
    {
        private readonly IInteractionCommandSchema<TCommand> schema;

        public InteractionCommandCatalogEntry(
            string wireName,
            int version,
            IInteractionCommandSchema<TCommand> schema,
            bool agentVisible)
            : base(wireName, version, typeof(TCommand), schema.Arguments, agentVisible)
        {
            this.schema = schema;
        }

        public override DecodedInteractionCommand Decode(
            string targetId,
            JsonElement arguments)
        {
            try
            {
                var command = schema.Decode(targetId, arguments);
                InteractionContract.RequireTargetId(command.TargetId, nameof(targetId));
                if (!string.Equals(command.TargetId, targetId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The decoded command target does not match the requested target.",
                        nameof(targetId));
                }

                return new DecodedInteractionCommand(this, command);
            }
            catch (InteractionCommandException)
            {
                throw;
            }
            catch (ArgumentException exception)
            {
                throw new InteractionCommandException(
                    InteractionRejectionCode.InvalidArguments,
                    "Command arguments are invalid.",
                    exception);
            }
            catch (JsonException exception)
            {
                throw new InteractionCommandException(
                    InteractionRejectionCode.InvalidArguments,
                    "Command arguments are invalid JSON.",
                    exception);
            }
        }

        public override void WriteArguments(
            Utf8JsonWriter writer,
            IInteractionCommand command)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            if (!(command is TCommand typedCommand))
            {
                throw new ArgumentException(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Expected command type '{0}'.",
                        typeof(TCommand).FullName),
                    nameof(command));
            }

            schema.WriteArguments(writer, in typedCommand);
        }

        internal override ValueTask<InteractionResult> DispatchAsync(
            IInteractionDispatcher dispatcher,
            IInteractionCommand command,
            InteractionDispatchOptions options,
            CancellationToken cancellationToken)
        {
            return dispatcher.DispatchAsync(
                (TCommand)command,
                options,
                cancellationToken);
        }

        internal override bool SupportsTarget(IInteractionTarget target)
        {
            IInteractionPipeline<TCommand>? pipeline;
            return target.TryGetPipeline(out pipeline) && pipeline != null;
        }
    }

    internal readonly struct CommandIdentity : IEquatable<CommandIdentity>
    {
        public CommandIdentity(string wireName, int version)
        {
            WireName = wireName;
            Version = version;
        }

        public string WireName { get; }

        public int Version { get; }

        public bool Equals(CommandIdentity other)
        {
            return Version == other.Version
                && string.Equals(WireName, other.WireName, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is CommandIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            return InteractionContract.CombineHashCodes(
                StringComparer.Ordinal.GetHashCode(WireName),
                Version);
        }
    }

    internal static class InteractionJson
    {
        public static void RequireObject(JsonElement arguments)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("Command arguments must be a JSON object.");
            }
        }

        public static InteractionCommandException Invalid(string message)
        {
            return new InteractionCommandException(
                InteractionRejectionCode.InvalidArguments,
                message);
        }
    }
}
