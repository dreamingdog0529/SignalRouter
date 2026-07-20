using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SignalRouter
{
    // The semantic-ui built-in probe (design §14): a canonical snapshot of the semantic UI
    // tree — session epoch, revision, and every registered descriptor's observable state.
    // It also explains a hash change as property-level changes over its own snapshot schema
    // (IStatePropertyDiffProvider, design §14, ADR 0002).
    public sealed class SemanticUiStateProbe : IInteractionStateProbe, IStatePropertyDiffProvider
    {
        public const string ProbeId = "semantic-ui";

        private readonly InteractionRegistry registry;
        private readonly InteractionRegistryView view;

        public SemanticUiStateProbe(
            InteractionRegistry registry,
            InteractionRegistryView view = InteractionRegistryView.All)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            InteractionContract.RequireDefinedEnum(view, nameof(view));
            this.view = view;
        }

        public string Id
        {
            get { return ProbeId; }
        }

        public int Version
        {
            get { return 1; }
        }

        public StateProbeSnapshot Capture()
        {
            var snapshot = registry.GetSnapshot(view);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("sessionEpoch", snapshot.SessionEpoch);
                writer.WriteNumber("revision", snapshot.Revision);
                writer.WritePropertyName("targets");
                writer.WriteStartArray();
                foreach (var descriptor in snapshot.Targets)
                {
                    WriteDescriptor(writer, descriptor);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return StateProbeSnapshot.FromUtf8Bytes(buffer.WrittenMemory);
        }

        // Explains a semantic-ui hash change as property changes on its own snapshot schema.
        // A target present in BOTH snapshots (matched by ordinal id) yields Modified changes for
        // each differing scalar field; a target present on only one side yields Added (before
        // absent) or Removed (after absent) changes for every scalar field, so presence is
        // expressed per field via the nullable-side StatePropertyChange model (ADR 0003).
        //
        // Nested availableInteractions/argument-schema changes are also enumerated (ADR 0004):
        // interactions are matched by (wireName, version) and arguments by name under the nested
        // path targets[<id>].availableInteractions[<wireName>@<version>].arguments[<name>].<field>.
        // An added/removed interaction or argument is expressed per field like an added/removed
        // target; argument position is surfaced as a synthetic ordinal, but only when argument
        // membership is unchanged, so add/remove-induced index shifts do not generate noise.
        public IReadOnlyList<StatePropertyChange> DiffProperties(
            StateProbeSnapshot before,
            StateProbeSnapshot after)
        {
            if (before == null)
            {
                throw new ArgumentNullException(nameof(before));
            }

            if (after == null)
            {
                throw new ArgumentNullException(nameof(after));
            }

            using (var beforeDocument = JsonDocument.Parse(before.Utf8Json))
            using (var afterDocument = JsonDocument.Parse(after.Utf8Json))
            {
                var beforeTargets = IndexTargetsById(beforeDocument.RootElement);
                var afterTargets = IndexTargetsById(afterDocument.RootElement);
                var changes = new List<StatePropertyChange>();
                foreach (var beforeTarget in EnumerateTargets(beforeDocument.RootElement))
                {
                    var id = beforeTarget.GetProperty("id").GetString()!;
                    if (afterTargets.TryGetValue(id, out var afterTarget))
                    {
                        AddScalarFieldChanges(changes, id, beforeTarget, afterTarget);
                        AddInteractionChanges(changes, id, beforeTarget, afterTarget);
                    }
                    else
                    {
                        AddPresenceChanges(changes, id, beforeTarget, StatePropertyChangeKind.Removed);
                        AddPresenceInteractions(changes, id, beforeTarget, StatePropertyChangeKind.Removed);
                    }
                }

                foreach (var afterTarget in EnumerateTargets(afterDocument.RootElement))
                {
                    var id = afterTarget.GetProperty("id").GetString()!;
                    if (!beforeTargets.ContainsKey(id))
                    {
                        AddPresenceChanges(changes, id, afterTarget, StatePropertyChangeKind.Added);
                        AddPresenceInteractions(changes, id, afterTarget, StatePropertyChangeKind.Added);
                    }
                }

                return changes;
            }
        }

        // Emits an Added (target present only in after) or Removed (present only in before) change
        // for every scalar field of a single-sided target, reading the present side and leaving the
        // absent side null. A field whose present value is InteractionValue.Null is still emitted:
        // the field went from absent to present(null), which the nullable-side model distinguishes.
        private static void AddPresenceChanges(
            List<StatePropertyChange> changes,
            string id,
            JsonElement target,
            StatePropertyChangeKind kind)
        {
            AddPresence(changes, id, "role", kind, ReadString(target, "role"));
            AddPresence(changes, id, "label", kind, ReadString(target, "label"));
            AddPresence(changes, id, "parentId", kind, ReadNullableString(target, "parentId"));
            AddPresence(
                changes,
                id,
                "visible",
                kind,
                InteractionValue.FromBoolean(target.GetProperty("visible").GetBoolean()));
            AddPresence(
                changes,
                id,
                "enabled",
                kind,
                InteractionValue.FromBoolean(target.GetProperty("enabled").GetBoolean()));
            AddPresence(
                changes,
                id,
                "value",
                kind,
                ReadDescriptorValue(target.GetProperty("value")));
        }

        private static void AddPresence(
            List<StatePropertyChange> changes,
            string id,
            string field,
            StatePropertyChangeKind kind,
            InteractionValue present)
        {
            AddSingleSided(changes, string.Concat("targets[", id, "].", field), kind, present);
        }

        // Emits a single-sided change at an explicit path: the present value on After for an
        // Added change (Before absent) or on Before for a Removed change (After absent). Shared
        // by target-level and nested (interaction/argument) presence enumeration.
        private static void AddSingleSided(
            List<StatePropertyChange> changes,
            string path,
            StatePropertyChangeKind kind,
            InteractionValue present)
        {
            changes.Add(kind == StatePropertyChangeKind.Added
                ? new StatePropertyChange(path, null, present)
                : new StatePropertyChange(path, present, null));
        }

        private static void AddScalarFieldChanges(
            List<StatePropertyChange> changes,
            string id,
            JsonElement before,
            JsonElement after)
        {
            AddIfChanged(changes, id, "role", ReadString(before, "role"), ReadString(after, "role"));
            AddIfChanged(changes, id, "label", ReadString(before, "label"), ReadString(after, "label"));
            AddIfChanged(
                changes,
                id,
                "parentId",
                ReadNullableString(before, "parentId"),
                ReadNullableString(after, "parentId"));
            AddIfChanged(
                changes,
                id,
                "visible",
                InteractionValue.FromBoolean(before.GetProperty("visible").GetBoolean()),
                InteractionValue.FromBoolean(after.GetProperty("visible").GetBoolean()));
            AddIfChanged(
                changes,
                id,
                "enabled",
                InteractionValue.FromBoolean(before.GetProperty("enabled").GetBoolean()),
                InteractionValue.FromBoolean(after.GetProperty("enabled").GetBoolean()));
            AddIfChanged(
                changes,
                id,
                "value",
                ReadDescriptorValue(before.GetProperty("value")),
                ReadDescriptorValue(after.GetProperty("value")));
        }

        private static void AddIfChanged(
            List<StatePropertyChange> changes,
            string id,
            string field,
            InteractionValue before,
            InteractionValue after)
        {
            AddIfChanged(changes, string.Concat("targets[", id, "].", field), before, after);
        }

        private static void AddIfChanged(
            List<StatePropertyChange> changes,
            string path,
            InteractionValue before,
            InteractionValue after)
        {
            // Skip equal values: this both honors StatePropertyChange's before != after
            // invariant and absorbs the one ambiguity where a JSON-null value and an explicit
            // Null-kind value both map to InteractionValue.Null. The hash stays authoritative.
            if (before.Equals(after))
            {
                return;
            }

            changes.Add(new StatePropertyChange(path, before, after));
        }

        // Enumerates availableInteractions changes on a matched target (present in both
        // snapshots). Interactions are matched by (wireName, version); a matched interaction
        // recurses into its arguments (its key fields are identical so they never emit), a
        // single-sided interaction is enumerated per field (ADR 0004).
        private static void AddInteractionChanges(
            List<StatePropertyChange> changes,
            string id,
            JsonElement beforeTarget,
            JsonElement afterTarget)
        {
            var beforeInteractions = IndexInteractions(beforeTarget);
            var afterInteractions = IndexInteractions(afterTarget);
            foreach (var beforeInteraction in EnumerateInteractions(beforeTarget))
            {
                var path = InteractionPath(id, beforeInteraction);
                if (afterInteractions.TryGetValue(InteractionKey(beforeInteraction), out var afterInteraction))
                {
                    AddMatchedInteraction(changes, path, beforeInteraction, afterInteraction);
                }
                else
                {
                    AddInteractionPresence(changes, path, beforeInteraction, StatePropertyChangeKind.Removed);
                }
            }

            foreach (var afterInteraction in EnumerateInteractions(afterTarget))
            {
                if (!beforeInteractions.ContainsKey(InteractionKey(afterInteraction)))
                {
                    AddInteractionPresence(
                        changes,
                        InteractionPath(id, afterInteraction),
                        afterInteraction,
                        StatePropertyChangeKind.Added);
                }
            }
        }

        // Emits Added/Removed per-field interaction changes for every interaction of a
        // single-sided target (a target present in only one snapshot).
        private static void AddPresenceInteractions(
            List<StatePropertyChange> changes,
            string id,
            JsonElement target,
            StatePropertyChangeKind kind)
        {
            foreach (var interaction in EnumerateInteractions(target))
            {
                AddInteractionPresence(changes, InteractionPath(id, interaction), interaction, kind);
            }
        }

        private static void AddMatchedInteraction(
            List<StatePropertyChange> changes,
            string interactionPath,
            JsonElement beforeInteraction,
            JsonElement afterInteraction)
        {
            // wireName/version are the match key and identical on both sides, so they never emit.
            var beforeArguments = IndexArguments(beforeInteraction);
            var afterArguments = IndexArguments(afterInteraction);

            // Argument position (ordinal) is only surfaced as a reorder signal when the argument
            // membership is identical on both sides. When arguments are added or removed, the
            // add/remove changes already explain the difference and every following argument's
            // index shifts — emitting those shifts would be noise (ADR 0004, Option C).
            var emitOrdinal = SameArgumentNames(beforeArguments, afterArguments);

            var beforeIndex = 0;
            foreach (var beforeArgument in EnumerateArguments(beforeInteraction))
            {
                var name = beforeArgument.GetProperty("name").GetString()!;
                var argumentPath = ArgumentPath(interactionPath, name);
                if (afterArguments.TryGetValue(name, out var afterArgument))
                {
                    AddMatchedArgument(
                        changes,
                        argumentPath,
                        beforeArgument,
                        beforeIndex,
                        afterArgument.Element,
                        afterArgument.Index,
                        emitOrdinal);
                }
                else
                {
                    AddArgumentPresence(changes, argumentPath, beforeArgument, StatePropertyChangeKind.Removed);
                }

                beforeIndex++;
            }

            foreach (var afterArgument in EnumerateArguments(afterInteraction))
            {
                var name = afterArgument.GetProperty("name").GetString()!;
                if (!beforeArguments.ContainsKey(name))
                {
                    AddArgumentPresence(
                        changes,
                        ArgumentPath(interactionPath, name),
                        afterArgument,
                        StatePropertyChangeKind.Added);
                }
            }
        }

        private static void AddMatchedArgument(
            List<StatePropertyChange> changes,
            string argumentPath,
            JsonElement before,
            int beforeIndex,
            JsonElement after,
            int afterIndex,
            bool emitOrdinal)
        {
            AddIfChanged(
                changes,
                string.Concat(argumentPath, ".type"),
                ReadArgumentType(before),
                ReadArgumentType(after));
            AddIfChanged(
                changes,
                string.Concat(argumentPath, ".required"),
                InteractionValue.FromBoolean(before.GetProperty("required").GetBoolean()),
                InteractionValue.FromBoolean(after.GetProperty("required").GetBoolean()));
            AddIfChanged(
                changes,
                string.Concat(argumentPath, ".sensitive"),
                InteractionValue.FromBoolean(before.GetProperty("sensitive").GetBoolean()),
                InteractionValue.FromBoolean(after.GetProperty("sensitive").GetBoolean()));
            if (emitOrdinal)
            {
                AddIfChanged(
                    changes,
                    string.Concat(argumentPath, ".ordinal"),
                    InteractionValue.FromNumber(beforeIndex),
                    InteractionValue.FromNumber(afterIndex));
            }
        }

        // Emits Added/Removed changes for the key fields (wireName, version) of a single-sided
        // interaction, then per-field presence for each of its arguments. Emitting the key
        // fields keeps an added/removed interaction visible even when it has no arguments.
        private static void AddInteractionPresence(
            List<StatePropertyChange> changes,
            string interactionPath,
            JsonElement interaction,
            StatePropertyChangeKind kind)
        {
            AddSingleSided(
                changes,
                string.Concat(interactionPath, ".wireName"),
                kind,
                InteractionValue.FromString(interaction.GetProperty("wireName").GetString()!));
            AddSingleSided(
                changes,
                string.Concat(interactionPath, ".version"),
                kind,
                InteractionValue.FromNumber(interaction.GetProperty("version").GetInt32()));
            foreach (var argument in EnumerateArguments(interaction))
            {
                var name = argument.GetProperty("name").GetString()!;
                AddArgumentPresence(changes, ArgumentPath(interactionPath, name), argument, kind);
            }
        }

        // Emits Added/Removed changes for the fields of a single-sided argument. Ordinal is not
        // emitted: it only carries meaning as a before/after position delta, which a single-sided
        // argument (or its enclosing interaction) does not have.
        private static void AddArgumentPresence(
            List<StatePropertyChange> changes,
            string argumentPath,
            JsonElement argument,
            StatePropertyChangeKind kind)
        {
            AddSingleSided(changes, string.Concat(argumentPath, ".type"), kind, ReadArgumentType(argument));
            AddSingleSided(
                changes,
                string.Concat(argumentPath, ".required"),
                kind,
                InteractionValue.FromBoolean(argument.GetProperty("required").GetBoolean()));
            AddSingleSided(
                changes,
                string.Concat(argumentPath, ".sensitive"),
                kind,
                InteractionValue.FromBoolean(argument.GetProperty("sensitive").GetBoolean()));
        }

        private static bool SameArgumentNames(
            Dictionary<string, (JsonElement Element, int Index)> before,
            Dictionary<string, (JsonElement Element, int Index)> after)
        {
            if (before.Count != after.Count)
            {
                return false;
            }

            foreach (var name in before.Keys)
            {
                if (!after.ContainsKey(name))
                {
                    return false;
                }
            }

            return true;
        }

        private static string InteractionKey(JsonElement interaction)
        {
            return string.Concat(
                interaction.GetProperty("wireName").GetString(),
                "@",
                interaction.GetProperty("version").GetInt32().ToString(CultureInfo.InvariantCulture));
        }

        private static string InteractionPath(string id, JsonElement interaction)
        {
            return string.Concat("targets[", id, "].availableInteractions[", InteractionKey(interaction), "]");
        }

        private static string ArgumentPath(string interactionPath, string name)
        {
            return string.Concat(interactionPath, ".arguments[", name, "]");
        }

        private static InteractionValue ReadArgumentType(JsonElement argument)
        {
            return InteractionValue.FromNumber(argument.GetProperty("type").GetInt32());
        }

        private static IEnumerable<JsonElement> EnumerateInteractions(JsonElement target)
        {
            return target.GetProperty("availableInteractions").EnumerateArray();
        }

        private static IEnumerable<JsonElement> EnumerateArguments(JsonElement interaction)
        {
            return interaction.GetProperty("arguments").EnumerateArray();
        }

        private static Dictionary<string, JsonElement> IndexInteractions(JsonElement target)
        {
            var byKey = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var interaction in EnumerateInteractions(target))
            {
                byKey[InteractionKey(interaction)] = interaction;
            }

            return byKey;
        }

        private static Dictionary<string, (JsonElement Element, int Index)> IndexArguments(
            JsonElement interaction)
        {
            var byName = new Dictionary<string, (JsonElement Element, int Index)>(StringComparer.Ordinal);
            var index = 0;
            foreach (var argument in EnumerateArguments(interaction))
            {
                byName[argument.GetProperty("name").GetString()!] = (argument, index);
                index++;
            }

            return byName;
        }

        private static InteractionValue ReadString(JsonElement target, string field)
        {
            return InteractionValue.FromString(target.GetProperty(field).GetString()!);
        }

        private static InteractionValue ReadNullableString(JsonElement target, string field)
        {
            var element = target.GetProperty(field);
            return element.ValueKind == JsonValueKind.Null
                ? InteractionValue.Null
                : InteractionValue.FromString(element.GetString()!);
        }

        private static InteractionValue ReadDescriptorValue(JsonElement value)
        {
            // A descriptor with no value serializes as JSON null; a value serializes as
            // {"kind":N,"value":...}. Numbers are encoded as normalized invariant strings
            // (see WriteValue), so they round-trip back through decimal.Parse.
            if (value.ValueKind == JsonValueKind.Null)
            {
                return InteractionValue.Null;
            }

            var kind = (InteractionValueKind)value.GetProperty("kind").GetInt32();
            switch (kind)
            {
                case InteractionValueKind.Null:
                    return InteractionValue.Null;
                case InteractionValueKind.String:
                    return InteractionValue.FromString(value.GetProperty("value").GetString()!);
                case InteractionValueKind.Boolean:
                    return InteractionValue.FromBoolean(value.GetProperty("value").GetBoolean());
                case InteractionValueKind.Number:
                    return InteractionValue.FromNumber(
                        decimal.Parse(
                            value.GetProperty("value").GetString()!,
                            NumberStyles.Number,
                            CultureInfo.InvariantCulture));
                default:
                    throw new InvalidOperationException("The interaction value kind is invalid.");
            }
        }

        private static IEnumerable<JsonElement> EnumerateTargets(JsonElement root)
        {
            return root.GetProperty("targets").EnumerateArray();
        }

        private static Dictionary<string, JsonElement> IndexTargetsById(JsonElement root)
        {
            var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var target in EnumerateTargets(root))
            {
                byId[target.GetProperty("id").GetString()!] = target;
            }

            return byId;
        }

        private static void WriteDescriptor(Utf8JsonWriter writer, InteractionDescriptor descriptor)
        {
            writer.WriteStartObject();
            writer.WriteString("id", descriptor.Id);
            if (descriptor.ParentId == null)
            {
                writer.WriteNull("parentId");
            }
            else
            {
                writer.WriteString("parentId", descriptor.ParentId);
            }

            writer.WriteString("role", descriptor.Role);
            writer.WriteString("label", descriptor.Label);
            writer.WriteBoolean("visible", descriptor.Visible);
            writer.WriteBoolean("enabled", descriptor.Enabled);
            writer.WritePropertyName("value");
            WriteValue(writer, descriptor.Value);
            writer.WritePropertyName("availableInteractions");
            WriteInteractions(writer, descriptor.AvailableInteractions);
            writer.WriteEndObject();
        }

        private static void WriteInteractions(
            Utf8JsonWriter writer,
            IReadOnlyList<AvailableInteraction> interactions)
        {
            // Sort by (wireName, version) so the hash is independent of registration order.
            var ordered = new List<AvailableInteraction>(interactions);
            ordered.Sort((left, right) =>
            {
                var byName = string.CompareOrdinal(left.WireName, right.WireName);
                return byName != 0 ? byName : left.Version.CompareTo(right.Version);
            });

            writer.WriteStartArray();
            foreach (var interaction in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("wireName", interaction.WireName);
                writer.WriteNumber("version", interaction.Version);
                writer.WritePropertyName("arguments");
                WriteArguments(writer, interaction.Arguments);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static void WriteArguments(
            Utf8JsonWriter writer,
            InteractionArgumentSchema schema)
        {
            // Argument requiredness, type, and sensitivity are part of what an agent may send
            // or record; a change in them must change the semantic-ui hash even when the wire
            // name and version are unchanged. Order is preserved: schema argument order is
            // itself observable state (it defines codec output property order, design §6.1).
            writer.WriteStartArray();
            foreach (var argument in schema.Arguments)
            {
                writer.WriteStartObject();
                writer.WriteString("name", argument.Name);
                writer.WriteNumber("type", (int)argument.Type);
                writer.WriteBoolean("required", argument.Required);
                writer.WriteBoolean("sensitive", argument.Sensitive);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static void WriteValue(Utf8JsonWriter writer, InteractionValue? value)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteNumber("kind", (int)value.Kind);
            switch (value.Kind)
            {
                case InteractionValueKind.Null:
                    writer.WriteNull("value");
                    break;
                case InteractionValueKind.String:
                    writer.WriteString("value", value.GetString());
                    break;
                case InteractionValueKind.Boolean:
                    writer.WriteBoolean("value", value.GetBoolean());
                    break;
                case InteractionValueKind.Number:
                    // Encoded as a normalized string so equal values with different decimal
                    // scale (1.0 == 1.00) share one representation and the canonical integer
                    // restriction does not reject fractional descriptor values.
                    writer.WriteString("value", NormalizeNumber(value.GetNumber()));
                    break;
                default:
                    throw new InvalidOperationException("The interaction value kind is invalid.");
            }

            writer.WriteEndObject();
        }

        private static string NormalizeNumber(decimal value)
        {
            // "0.###…" keeps at least one integer digit and drops trailing fractional zeros.
            return value.ToString(
                "0.############################",
                CultureInfo.InvariantCulture);
        }
    }

    // The interaction-runtime built-in probe (design §14): session epoch and registry
    // revision — the runtime identity that strict replay (design §16) compares.
    //
    // Design §14 also lists "queue state" for this probe. Transient queue depth is
    // deliberately excluded from the hashed snapshot: it is timing-dependent and would make
    // an otherwise-reproducible interaction hash differently under replay. Queue metrics, if
    // exposed at all, belong out of band with the runtime bridge, not in a replay-compared
    // state hash (see ADR 0001).
    public sealed class InteractionRuntimeStateProbe : IInteractionStateProbe
    {
        public const string ProbeId = "interaction-runtime";

        private readonly InteractionRegistry registry;

        public InteractionRuntimeStateProbe(InteractionRegistry registry)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public string Id
        {
            get { return ProbeId; }
        }

        public int Version
        {
            get { return 1; }
        }

        public StateProbeSnapshot Capture()
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("sessionEpoch", registry.SessionEpoch);
                writer.WriteNumber("revision", registry.Revision);
                writer.WriteEndObject();
            }

            return StateProbeSnapshot.FromUtf8Bytes(buffer.WrittenMemory);
        }
    }
}
