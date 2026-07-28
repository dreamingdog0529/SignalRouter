using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SignalRouter.Contracts
{
    /// <summary>The kernel-derived identity of a canonicalized invocation.</summary>
    public sealed class CanonicalInvocation
    {
        public CanonicalInvocation(SemanticFingerprint fingerprint, ArgumentDigest arguments)
        {
            if (fingerprint.IsDefault)
            {
                throw new ArgumentException("Fingerprint must be non-default.", nameof(fingerprint));
            }

            if (arguments.IsDefault)
            {
                throw new ArgumentException("Argument digest must be non-default.", nameof(arguments));
            }

            Fingerprint = fingerprint;
            Arguments = arguments;
        }

        public SemanticFingerprint Fingerprint { get; }

        public ArgumentDigest Arguments { get; }
    }

    /// <summary>
    /// Derives the authoritative semantic fingerprint and redacted-argument digest
    /// from a canonicalized payload (semantic-model.md §2.2, kernel-execution.md §3,
    /// ADR 0010). The fingerprint covers the capability contract, the resolved
    /// target identity (`AuthorKey` when the node has one, otherwise the `NodeRef`),
    /// and the redacted-argument digest — a caller-supplied fingerprint is verified
    /// against this derivation, never trusted.
    ///
    /// Sensitive argument values never contribute plaintext or a bare hash (a
    /// low-entropy secret must not be confirmable by hashing a guess,
    /// security-resources.md §4): they contribute an HMAC keyed with runtime-secret
    /// material the kernel supplies.
    /// </summary>
    public static class InvocationCanonicalizer
    {
        // Separators structure the canonical form; every variable text segment is
        // additionally LENGTH-FRAMED ("<length>:<text>") because field VALUES are
        // arbitrary strings that may themselves contain the separators — framing is
        // what makes distinct payloads collision-free by construction.
        private const char FieldSeparator = '\u001f';
        private const char RecordSeparator = '\u001e';

        public static CanonicalInvocation Canonicalize(
            CapabilityContractRef contract,
            ResolvedTarget target,
            InvocationPayload payload,
            ArgumentSchema schema,
            byte[] redactionKey)
        {
            if (contract.IsDefault)
            {
                throw new ArgumentException("Contract must be non-default.", nameof(contract));
            }

            if (target.IsDefault)
            {
                throw new ArgumentException("Target must be non-default.", nameof(target));
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (schema == null)
            {
                throw new ArgumentNullException(nameof(schema));
            }

            if (redactionKey == null || redactionKey.Length == 0)
            {
                throw new ArgumentException("A non-empty redaction key is required.", nameof(redactionKey));
            }

            ValidatePayload(payload, schema);

            var argumentsSection = BuildArgumentsSection(payload, schema, redactionKey);
            var argumentDigest = new ArgumentDigest(Sha256Hex(argumentsSection));

            var builder = new StringBuilder();
            builder.Append("capability").Append(FieldSeparator);
            AppendFramed(builder, contract.Id.Value);
            builder.Append(FieldSeparator)
                .Append(contract.Version.Major.ToString(CultureInfo.InvariantCulture)).Append('.')
                .Append(contract.Version.Minor.ToString(CultureInfo.InvariantCulture))
                .Append(RecordSeparator);
            builder.Append("target").Append(FieldSeparator);
            if (target.HasAuthorKey)
            {
                builder.Append("key").Append(FieldSeparator);
                AppendFramed(builder, target.AuthorKey!.Value.Value);
            }
            else
            {
                builder.Append("node").Append(FieldSeparator);
                AppendFramed(builder, target.Node.Incarnation.Value);
                builder.Append('/')
                    .Append(target.Node.Value.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append(RecordSeparator);
            builder.Append("arguments").Append(FieldSeparator).Append(argumentDigest.Value);

            var fingerprint = new SemanticFingerprint(Sha256Hex(builder.ToString()));
            return new CanonicalInvocation(fingerprint, argumentDigest);
        }

        /// <summary>
        /// Projects the live payload into its portable recorded form (ADR 0015):
        /// canonical ordinal name order, non-sensitive values typed, sensitive
        /// values as a contract-scoped <see cref="SecretReference"/>
        /// (<c>contractId@major.minor/argumentName</c>) plus the same keyed digest
        /// the argument section embeds. <see cref="DigestOf"/> over the result
        /// equals the digest <see cref="Canonicalize"/> derives from the payload.
        /// </summary>
        public static RecordedArguments Project(
            CapabilityContractRef contract,
            InvocationPayload payload,
            ArgumentSchema schema,
            byte[] redactionKey)
        {
            if (contract.IsDefault)
            {
                throw new ArgumentException("Contract must be non-default.", nameof(contract));
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (schema == null)
            {
                throw new ArgumentNullException(nameof(schema));
            }

            if (redactionKey == null || redactionKey.Length == 0)
            {
                throw new ArgumentException("A non-empty redaction key is required.", nameof(redactionKey));
            }

            ValidatePayload(payload, schema);

            var names = SortedNames(payload);
            var fields = new RecordedArgument[names.Count];
            for (var index = 0; index < names.Count; index++)
            {
                var name = names[index];
                payload.TryGetValue(name, out var value);
                schema.TryGetField(name, out var declared);
                if (declared.Sensitivity == Sensitivity.Sensitive &&
                    value.Kind != FieldValueKind.Null)
                {
                    // Both variable components are length-framed: contract ids and
                    // argument names may legally contain '@' and '/', so bare
                    // concatenation would not be injective.
                    var contractId = contract.Id.Value;
                    var reference = new SecretReference(
                        contractId.Length.ToString(CultureInfo.InvariantCulture) + ":" + contractId
                        + "@" + contract.Version.Major.ToString(CultureInfo.InvariantCulture)
                        + "." + contract.Version.Minor.ToString(CultureInfo.InvariantCulture)
                        + "/" + name.Length.ToString(CultureInfo.InvariantCulture) + ":" + name);
                    fields[index] = RecordedArgument.OfSecret(
                        name,
                        reference,
                        new ArgumentDigest(HmacHex(redactionKey, CanonicalRendering(value))));
                }
                else
                {
                    fields[index] = RecordedArgument.OfValue(name, value);
                }
            }

            return new RecordedArguments(ValueArray<RecordedArgument>.From(fields));
        }

        /// <summary>
        /// Recomputes the redacted argument digest from the recorded form — the
        /// replay-side identity check: a resolved or re-admitted argument set that
        /// does not re-digest to E2's recorded digest is not the recorded
        /// invocation.
        /// </summary>
        public static ArgumentDigest DigestOf(RecordedArguments recorded)
        {
            if (recorded == null)
            {
                throw new ArgumentNullException(nameof(recorded));
            }

            var builder = new StringBuilder();
            for (var index = 0; index < recorded.Fields.Count; index++)
            {
                var field = recorded.Fields[index];
                if (field.IsSecret)
                {
                    AppendSensitiveContribution(builder, field.Name, field.SecretValueDigest.Value);
                }
                else
                {
                    AppendValueContribution(builder, field.Name, field.Value);
                }
            }

            return new ArgumentDigest(Sha256Hex(builder.ToString()));
        }

        /// <summary>
        /// The keyed digest of one sensitive value — what a recorded
        /// <see cref="RecordedArgument.SecretValueDigest"/> holds. Replay
        /// re-digests a resolved secret against the recorded digest with the
        /// shared redaction material and stops before the affected entry on a
        /// mismatch — never a silent substitution (ADR 0015).
        /// </summary>
        public static ArgumentDigest SensitiveValueDigest(byte[] redactionKey, FieldValue value)
        {
            if (redactionKey == null)
            {
                throw new ArgumentNullException(nameof(redactionKey));
            }

            if (value.IsDefault)
            {
                throw new ArgumentException(
                    "A sensitive digest requires a non-default value.", nameof(value));
            }

            return new ArgumentDigest(HmacHex(redactionKey, CanonicalRendering(value)));
        }

        // The single source of one field's canonical contribution — the projection
        // and the live digest path must never drift apart.
        private static void AppendSensitiveContribution(
            StringBuilder builder, string name, string keyedDigestHex)
        {
            AppendFramed(builder, name);
            builder.Append(FieldSeparator)
                .Append("sensitive").Append(FieldSeparator)
                .Append(keyedDigestHex)
                .Append(RecordSeparator);
        }

        private static void AppendValueContribution(StringBuilder builder, string name, FieldValue value)
        {
            AppendFramed(builder, name);
            builder.Append(FieldSeparator)
                .Append(FieldKindTag(value.Kind)).Append(FieldSeparator);
            AppendFramed(builder, CanonicalRendering(value));
            builder.Append(RecordSeparator);
        }

        // Floats contribute their IEEE-754 bit pattern, aligning argument identity
        // with the observation codec (ADR 0012): 0.0 and -0.0 are distinct
        // payloads, and digest inequality implies nothing. FieldValue.Equals stays
        // numeric; identity and DTO equality are deliberately different relations.
        private static string CanonicalRendering(FieldValue value) =>
            value.Kind == FieldValueKind.Float
                ? BitConverter.DoubleToInt64Bits(value.AsFloat).ToString("x16", CultureInfo.InvariantCulture)
                : value.ToString();

        private static void ValidatePayload(InvocationPayload payload, ArgumentSchema schema)
        {
            foreach (var field in payload.Fields)
            {
                if (!schema.TryGetField(field.Name, out var declared))
                {
                    throw new ArgumentException(
                        $"Payload field '{field.Name}' is not declared by the argument schema.",
                        nameof(payload));
                }

                if (field.Value.Kind != FieldValueKind.Null && !Matches(declared.Type, field.Value.Kind))
                {
                    throw new ArgumentException(
                        $"Payload field '{field.Name}' has kind {field.Value.Kind}; schema declares {declared.Type}.",
                        nameof(payload));
                }
            }

            foreach (var declared in schema.Fields)
            {
                if (declared.Required && !payload.TryGetValue(declared.Name, out _))
                {
                    throw new ArgumentException(
                        $"Required argument '{declared.Name}' is missing.", nameof(payload));
                }
            }
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

        private static List<string> SortedNames(InvocationPayload payload)
        {
            var names = new List<string>();
            foreach (var field in payload.Fields)
            {
                names.Add(field.Name);
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static string BuildArgumentsSection(
            InvocationPayload payload, ArgumentSchema schema, byte[] redactionKey)
        {
            var names = SortedNames(payload);

            var builder = new StringBuilder();
            foreach (var name in names)
            {
                payload.TryGetValue(name, out var value);
                schema.TryGetField(name, out var declared);
                if (declared.Sensitivity == Sensitivity.Sensitive &&
                    value.Kind != FieldValueKind.Null)
                {
                    AppendSensitiveContribution(
                        builder, name, HmacHex(redactionKey, CanonicalRendering(value)));
                }
                else
                {
                    AppendValueContribution(builder, name, value);
                }
            }

            return builder.ToString();
        }

        private static void AppendFramed(StringBuilder builder, string text)
        {
            builder.Append(text.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(text);
        }

        private static string FieldKindTag(FieldValueKind kind)
        {
            switch (kind)
            {
                case FieldValueKind.String:
                    return "s";
                case FieldValueKind.Integer:
                    return "i";
                case FieldValueKind.Boolean:
                    return "b";
                case FieldValueKind.Float:
                    return "f";
                default:
                    return "n";
            }
        }

        private static string Sha256Hex(string material)
        {
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(material)));
            }
        }

        private static string HmacHex(byte[] key, string material)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(material)));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            const string hex = "0123456789abcdef";
            var characters = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                characters[i * 2] = hex[bytes[i] >> 4];
                characters[(i * 2) + 1] = hex[bytes[i] & 0x0F];
            }

            return new string(characters);
        }
    }
}
