using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SignalRouter.V2.Contracts
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
        // ASCII unit/record separators keep the canonical form unambiguous without
        // escaping: neither may appear in identifiers (ContractGrammar bans control
        // characters), and field values are length-free because fields are
        // separator-delimited.
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
            builder.Append("capability").Append(FieldSeparator)
                .Append(contract.Id.Value).Append(FieldSeparator)
                .Append(contract.Version.Major.ToString(CultureInfo.InvariantCulture)).Append('.')
                .Append(contract.Version.Minor.ToString(CultureInfo.InvariantCulture))
                .Append(RecordSeparator);
            builder.Append("target").Append(FieldSeparator);
            if (target.HasAuthorKey)
            {
                builder.Append("key").Append(FieldSeparator).Append(target.AuthorKey!.Value.Value);
            }
            else
            {
                builder.Append("node").Append(FieldSeparator)
                    .Append(target.Node.Incarnation.Value).Append('/')
                    .Append(target.Node.Value.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append(RecordSeparator);
            builder.Append("arguments").Append(FieldSeparator).Append(argumentDigest.Value);

            var fingerprint = new SemanticFingerprint(Sha256Hex(builder.ToString()));
            return new CanonicalInvocation(fingerprint, argumentDigest);
        }

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

        private static string BuildArgumentsSection(
            InvocationPayload payload, ArgumentSchema schema, byte[] redactionKey)
        {
            var names = new List<string>();
            foreach (var field in payload.Fields)
            {
                names.Add(field.Name);
            }

            names.Sort(StringComparer.Ordinal);

            var builder = new StringBuilder();
            foreach (var name in names)
            {
                payload.TryGetValue(name, out var value);
                schema.TryGetField(name, out var declared);
                builder.Append(name).Append(FieldSeparator);
                if (declared.Sensitivity == Sensitivity.Sensitive &&
                    value.Kind != FieldValueKind.Null)
                {
                    builder.Append("sensitive").Append(FieldSeparator)
                        .Append(HmacHex(redactionKey, value.ToString()));
                }
                else
                {
                    builder.Append(FieldKindTag(value.Kind)).Append(FieldSeparator)
                        .Append(value.ToString());
                }

                builder.Append(RecordSeparator);
            }

            return builder.ToString();
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
