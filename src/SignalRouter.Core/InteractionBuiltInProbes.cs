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

        // Explains a semantic-ui hash change as scalar field changes on targets present in
        // BOTH snapshots (matched by ordinal id). Target additions/removals and nested
        // availableInteractions changes still change the hash but are not enumerated here;
        // the 3-field StatePropertyChange cannot express presence or nested structure, so they
        // are deferred (ADR 0002). Emitting only scalar field deltas keeps every change a
        // valid StatePropertyChange (differing before/after InteractionValue).
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
                var afterTargets = IndexTargetsById(afterDocument.RootElement);
                var changes = new List<StatePropertyChange>();
                foreach (var beforeTarget in EnumerateTargets(beforeDocument.RootElement))
                {
                    var id = beforeTarget.GetProperty("id").GetString()!;
                    if (afterTargets.TryGetValue(id, out var afterTarget))
                    {
                        AddScalarFieldChanges(changes, id, beforeTarget, afterTarget);
                    }
                }

                return changes;
            }
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
            // Skip equal values: this both honors StatePropertyChange's before != after
            // invariant and absorbs the one ambiguity where a JSON-null value and an explicit
            // Null-kind value both map to InteractionValue.Null. The hash stays authoritative.
            if (before.Equals(after))
            {
                return;
            }

            var path = string.Concat("targets[", id, "].", field);
            changes.Add(new StatePropertyChange(path, before, after));
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
