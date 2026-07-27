using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Codec.CanonicalState
{
    /// <summary>
    /// Canonical representation v1 (ADR 0012): the production
    /// <see cref="ICanonicalStateCodec"/>. The payload embeds the projection
    /// identity — view contract, security domain, scope — and neither temporal leg;
    /// `Decode` takes <see cref="RuntimeIncarnationId"/> and
    /// <see cref="SourceRevision"/> from the authoritative snapshot/cut tuple.
    /// `ContentId = ("sha256", 1, SHA-256(payload))`, the digest being the raw 32
    /// octets over the entire payload, header included. Decode enforces canonical
    /// form by re-encoding: a payload that parses but does not re-encode to the
    /// identical bytes is malformed, so `Encode(Decode(b)) == b` by construction.
    /// Stateless and thread-safe.
    /// </summary>
    public sealed class CanonicalStateCodec : ICanonicalStateCodec
    {
        public const string AlgorithmId = "sha256";

        public const int RepresentationVersion = 1;

        public CanonicalStateResult Encode(ObservationMaterialization materialization)
        {
            if (materialization == null)
            {
                throw new ArgumentNullException(nameof(materialization));
            }

            var payload = EncodePayload(materialization);
            using (var sha = SHA256.Create())
            {
                return new CanonicalStateResult(
                    new ContentId(AlgorithmId, RepresentationVersion, DigestValue.From(sha.ComputeHash(payload))),
                    payload);
            }
        }

        public ObservationMaterialization Decode(
            byte[] canonicalPayload, RuntimeIncarnationId incarnation, SourceRevision revision)
        {
            if (canonicalPayload == null)
            {
                throw new ArgumentNullException(nameof(canonicalPayload));
            }

            if (incarnation.IsDefault)
            {
                throw new ArgumentException(
                    "Decode requires the authoritative incarnation from the referencing tuple.",
                    nameof(incarnation));
            }

            var reader = new PayloadReader(canonicalPayload);
            var materialization = DecodePayload(reader, incarnation, revision);
            reader.ExpectEnd();

            // Canonical-form enforcement (ADR 0012): one re-encode comparison
            // subsumes every alternative-encoding attack — non-minimal framing that
            // parsed, out-of-order lists, and anything else the structural parse
            // could not see. The re-encode stages in a pooled buffer and compares
            // as a span: enforcement allocates nothing.
            var writer = PayloadWriter.Rent();
            try
            {
                WritePayload(ref writer, materialization);
                if (!writer.WrittenSpan.SequenceEqual(canonicalPayload))
                {
                    throw new CanonicalStateFormatException(
                        "NonCanonical", -1, "The payload is not in canonical form.");
                }
            }
            finally
            {
                writer.Dispose();
            }

            return materialization;
        }

        /// <summary>Verify-before-use (semantic-model.md §5): recompute and compare; algorithm/version mismatches answer false.</summary>
        public static bool Verify(ContentId id, byte[] canonicalPayload)
        {
            if (canonicalPayload == null)
            {
                throw new ArgumentNullException(nameof(canonicalPayload));
            }

            if (id.IsDefault ||
                !string.Equals(id.DigestAlgorithmId, AlgorithmId, StringComparison.Ordinal) ||
                id.CanonicalRepresentationVersion != RepresentationVersion)
            {
                return false;
            }

            using (var sha = SHA256.Create())
            {
                return DigestValue.From(sha.ComputeHash(canonicalPayload)).Equals(id.Digest);
            }
        }

        // ── Encoding ─────────────────────────────────────────────────────────

        private static byte[] EncodePayload(ObservationMaterialization materialization)
        {
            var writer = PayloadWriter.Rent();
            try
            {
                WritePayload(ref writer, materialization);
                return writer.ToArray();
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void WritePayload(ref PayloadWriter writer, ObservationMaterialization materialization)
        {
            writer.WriteMagic();
            writer.WriteVaruint(RepresentationVersion);

            // Projection identity — no temporal legs (ADR 0012).
            WriteContract(ref writer, materialization.Basis.View.Id.Value, materialization.Basis.View.Version);
            writer.WriteString(materialization.Basis.Domain.Value);
            writer.WriteString(materialization.Basis.Scope);

            writer.WriteVaruint(materialization.Nodes.Count);
            foreach (var node in materialization.Nodes)
            {
                writer.WriteString(node.Key.Value);
                writer.WriteString(node.Role.Value);
                if (node.Parent.HasValue)
                {
                    writer.WriteRaw(0x01);
                    writer.WriteString(node.Parent.Value.Value);
                }
                else
                {
                    writer.WriteRaw(0x00);
                }

                writer.WriteVaruint(node.Attributes.Count);
                foreach (var attribute in node.Attributes)
                {
                    writer.WriteString(attribute.Name);
                    if (attribute.Redacted)
                    {
                        writer.WriteRaw(0x01);
                    }
                    else
                    {
                        writer.WriteRaw(0x00);
                        WriteValue(ref writer, attribute.Value);
                    }
                }

                writer.WriteVaruint(node.Capabilities.Count);
                foreach (var capability in node.Capabilities)
                {
                    WriteContract(ref writer, capability.Contract.Id.Value, capability.Contract.Version);
                    writer.WriteBool(capability.Available);
                }

                writer.WriteVaruint(node.VisibleChildCount);
            }

            writer.WriteVaruint(materialization.Sources.Count);
            foreach (var source in materialization.Sources)
            {
                writer.WriteString(source.Key.Value);
                WriteContract(ref writer, source.Contract.Id.Value, source.Contract.Version);
                if (source.Omission.HasValue)
                {
                    writer.WriteRaw(0x01);
                    writer.WriteString(CompletenessReasonCodes.ToCode(source.Omission.Value));
                }
                else
                {
                    writer.WriteRaw(0x00);
                }

                writer.WriteVaruint(source.Fields.Count);
                foreach (var field in source.Fields)
                {
                    writer.WriteString(field.Name);
                    WriteValue(ref writer, field.Value);
                }

                writer.WriteVaruint(source.RedactedFieldNames.Count);
                foreach (var name in source.RedactedFieldNames)
                {
                    writer.WriteString(name);
                }
            }

            writer.WriteBool(materialization.Completeness.RootTruncated);
            writer.WriteVaruint(materialization.Completeness.Entries.Count);
            foreach (var entry in materialization.Completeness.Entries)
            {
                writer.WriteString(entry.Region.Value);
                writer.WriteString(CompletenessReasonCodes.ToCode(entry.Reason));
            }
        }

        private static void WriteContract(ref PayloadWriter writer, string id, ContractVersion version)
        {
            writer.WriteString(id);
            writer.WriteVaruint(version.Major);
            writer.WriteVaruint(version.Minor);
        }

        private static void WriteValue(ref PayloadWriter writer, FieldValue value)
        {
            switch (value.Kind)
            {
                case FieldValueKind.String:
                    writer.WriteRaw(0x01);
                    writer.WriteString(value.AsString);
                    break;
                case FieldValueKind.Integer:
                    writer.WriteRaw(0x02);
                    writer.WriteInt64(value.AsInteger);
                    break;
                case FieldValueKind.Boolean:
                    writer.WriteRaw(0x03);
                    writer.WriteBool(value.AsBoolean);
                    break;
                case FieldValueKind.Float:
                    writer.WriteRaw(0x04);
                    writer.WriteFloatBits(value.AsFloat);
                    break;
                default:
                    writer.WriteRaw(0x05);
                    break;
            }
        }

        // ── Decoding ─────────────────────────────────────────────────────────

        // Minimum encoded sizes per list item — the allocation-bomb pre-check.
        private const int MinNodeBytes = 8;
        private const int MinAttributeBytes = 3;
        private const int MinCapabilityBytes = 5;
        private const int MinSourceBytes = 9;
        private const int MinFieldBytes = 3;
        private const int MinNameBytes = 2;
        private const int MinCompletenessEntryBytes = 4;

        private static ObservationMaterialization DecodePayload(
            PayloadReader reader, RuntimeIncarnationId incarnation, SourceRevision revision)
        {
            var magicStart = reader.Position;
            if (reader.ReadByte() != 0x53 || reader.ReadByte() != 0x52 ||
                reader.ReadByte() != 0x43 || reader.ReadByte() != 0x53)
            {
                throw new CanonicalStateFormatException(
                    "BadMagic", magicStart, "The payload does not start with the SRCS magic.");
            }

            var versionStart = reader.Position;
            var version = reader.ReadVaruint();
            if (version != RepresentationVersion)
            {
                throw new CanonicalStateFormatException(
                    "UnsupportedVersion", versionStart,
                    "Unsupported canonical representation version " + version + ".");
            }

            try
            {
                var view = ReadViewContract(reader);
                var domain = new SecurityDomainId(reader.ReadString());
                var scope = reader.ReadString();

                var nodeCount = reader.ReadCount(MinNodeBytes);
                var nodes = new List<MaterializedNode>(nodeCount);
                for (var i = 0; i < nodeCount; i++)
                {
                    nodes.Add(ReadNode(reader));
                }

                var sourceCount = reader.ReadCount(MinSourceBytes);
                var sources = new List<MaterializedSource>(sourceCount);
                for (var i = 0; i < sourceCount; i++)
                {
                    sources.Add(ReadSource(reader));
                }

                var rootTruncated = reader.ReadBool();
                var entryCount = reader.ReadCount(MinCompletenessEntryBytes);
                var entries = new List<CompletenessEntry>(entryCount);
                for (var i = 0; i < entryCount; i++)
                {
                    var region = new FieldPath(reader.ReadString());
                    var reasonStart = reader.Position;
                    var reasonCode = reader.ReadString();
                    if (!CompletenessReasonCodes.TryFromCode(reasonCode, out var reason))
                    {
                        throw new CanonicalStateFormatException(
                            "UnknownReasonCode", reasonStart,
                            "Unknown completeness reason code '" + reasonCode + "'.");
                    }

                    entries.Add(new CompletenessEntry(region, reason));
                }

                // With this bound the budget equals the entry count, so From never
                // folds and reconstruction is exact (ADR 0012).
                var completeness = CompletenessMap.From(
                    entries,
                    Math.Max(1, checked(entries.Count + (rootTruncated ? 1 : 0))),
                    rootTruncated);

                var basis = new ObservationBasis(
                    incarnation, revision, view, domain, scope);
                return new ObservationMaterialization(
                    basis,
                    ValueArray<MaterializedNode>.From(nodes),
                    ValueArray<MaterializedSource>.From(sources),
                    completeness);
            }
            catch (ArgumentException exception)
            {
                // Contracts constructors are the validation path: what they refuse
                // (duplicates, grammar violations, contradictory states) is a
                // structural fault of the payload.
                throw new CanonicalStateFormatException(
                    "InvalidStructure", reader.Position, exception.Message);
            }
        }

        private static ViewContractRef ReadViewContract(PayloadReader reader)
        {
            var id = reader.ReadString();
            var major = reader.ReadVaruint();
            var minor = reader.ReadVaruint();
            return new ViewContractRef(new ViewContractId(id), new ContractVersion(major, minor));
        }

        private static MaterializedNode ReadNode(PayloadReader reader)
        {
            var key = new AuthorKey(reader.ReadString());
            var role = new NodeRole(reader.ReadString());
            AuthorKey? parent = null;
            if (reader.ReadOption())
            {
                parent = new AuthorKey(reader.ReadString());
            }

            var attributeCount = reader.ReadCount(MinAttributeBytes);
            var attributes = new List<MaterializedAttribute>(attributeCount);
            for (var i = 0; i < attributeCount; i++)
            {
                var name = reader.ReadString();
                var redacted = reader.ReadOption();
                attributes.Add(redacted
                    ? new MaterializedAttribute(name, default, redacted: true)
                    : new MaterializedAttribute(name, ReadValue(reader), redacted: false));
            }

            var capabilityCount = reader.ReadCount(MinCapabilityBytes);
            var capabilities = new List<MaterializedCapability>(capabilityCount);
            for (var i = 0; i < capabilityCount; i++)
            {
                var id = reader.ReadString();
                var major = reader.ReadVaruint();
                var minor = reader.ReadVaruint();
                var available = reader.ReadBool();
                capabilities.Add(new MaterializedCapability(
                    new CapabilityContractRef(new CapabilityContractId(id), new ContractVersion(major, minor)),
                    available));
            }

            var childCount = reader.ReadVaruint();
            return new MaterializedNode(
                key, role, parent,
                ValueArray<MaterializedAttribute>.From(attributes),
                ValueArray<MaterializedCapability>.From(capabilities),
                childCount);
        }

        private static MaterializedSource ReadSource(PayloadReader reader)
        {
            var key = new StateSourceKey(reader.ReadString());
            var id = reader.ReadString();
            var major = reader.ReadVaruint();
            var minor = reader.ReadVaruint();
            CompletenessReason? omission = null;
            if (reader.ReadOption())
            {
                var reasonStart = reader.Position;
                var code = reader.ReadString();
                if (!CompletenessReasonCodes.TryFromCode(code, out var reason))
                {
                    throw new CanonicalStateFormatException(
                        "UnknownReasonCode", reasonStart,
                        "Unknown completeness reason code '" + code + "'.");
                }

                omission = reason;
            }

            var fieldCount = reader.ReadCount(MinFieldBytes);
            var fields = new List<NamedField>(fieldCount);
            for (var i = 0; i < fieldCount; i++)
            {
                var name = reader.ReadString();
                fields.Add(new NamedField(name, ReadValue(reader)));
            }

            var redactedCount = reader.ReadCount(MinNameBytes);
            var redactedNames = new List<string>(redactedCount);
            for (var i = 0; i < redactedCount; i++)
            {
                redactedNames.Add(reader.ReadString());
            }

            return new MaterializedSource(
                key,
                new StateSourceContractRef(new StateSourceContractId(id), new ContractVersion(major, minor)),
                ValueArray<NamedField>.From(fields),
                ValueArray<string>.From(redactedNames),
                omission);
        }

        private static FieldValue ReadValue(PayloadReader reader)
        {
            var tagStart = reader.Position;
            var tag = reader.ReadByte();
            switch (tag)
            {
                case 0x01:
                    return FieldValue.Of(reader.ReadString());
                case 0x02:
                    return FieldValue.Of(reader.ReadInt64());
                case 0x03:
                    return FieldValue.Of(reader.ReadBool());
                case 0x04:
                    return FieldValue.Of(reader.ReadFloatBits());
                case 0x05:
                    return FieldValue.Null;
                default:
                    throw new CanonicalStateFormatException(
                        "UnknownValueTag", tagStart, "Unknown value tag 0x" + tag.ToString("X2") + ".");
            }
        }
    }
}
