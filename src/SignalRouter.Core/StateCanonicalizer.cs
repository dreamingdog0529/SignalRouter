using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

namespace SignalRouter
{
    // Turns a probe snapshot into a deterministic canonical byte sequence and its SHA-256
    // hash (design §14; ADR 0001). Determinism is the property the recorder and strict
    // replayer (design §16) rely on: the same logical state must hash identically across
    // builds, machines, and processes.
    //
    // Canonical form is a constrained JSON subset: object, array, string, bool, integer,
    // null. Object keys are emitted in ascending ordinal order with no insignificant
    // whitespace. Non-integer numbers and any other JSON construct are rejected rather than
    // silently coerced, which sidesteps floating-point canonicalization ambiguity.
    internal static class StateCanonicalizer
    {
        private const string HexDigits = "0123456789abcdef";

        // The canonical UTF-8 byte sequence for the snapshot. Throws ArgumentException if the
        // payload is not well-formed JSON or steps outside the canonical value subset.
        public static byte[] Canonicalize(StateProbeSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(snapshot.Utf8Json);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    "A probe snapshot must be well-formed JSON.",
                    nameof(snapshot),
                    exception);
            }

            using (document)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    WriteCanonical(writer, document.RootElement);
                }

                return buffer.WrittenSpan.ToArray();
            }
        }

        // Lowercase-hex SHA-256 of the canonical bytes. The result contains no whitespace or
        // control characters, so it satisfies InteractionContract.RequireIdentifier when it
        // becomes a StateProbeObservation hash.
        public static string ComputeHash(StateProbeSnapshot snapshot)
        {
            var canonical = Canonicalize(snapshot);
            using (var sha256 = SHA256.Create())
            {
                return ToLowerHex(sha256.ComputeHash(canonical));
            }
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    WriteObject(writer, element);
                    return;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteCanonical(writer, item);
                    }

                    writer.WriteEndArray();
                    return;
                case JsonValueKind.String:
                    writer.WriteStringValue(element.GetString());
                    return;
                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    return;
                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    return;
                case JsonValueKind.Null:
                    writer.WriteNullValue();
                    return;
                case JsonValueKind.Number:
                    WriteInteger(writer, element);
                    return;
                default:
                    throw new ArgumentException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "A probe snapshot must not contain JSON of kind '{0}'.",
                            element.ValueKind));
            }
        }

        private static void WriteObject(Utf8JsonWriter writer, JsonElement element)
        {
            // Sort keys by ordinal comparison and reject duplicates so the canonical form is
            // unambiguous regardless of the probe's emission order.
            var properties = new List<JsonProperty>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ArgumentException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "A probe snapshot object must not contain the duplicate key '{0}'.",
                            property.Name));
                }

                properties.Add(property);
            }

            properties.Sort(
                (left, right) => string.CompareOrdinal(left.Name, right.Name));

            writer.WriteStartObject();
            foreach (var property in properties)
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }

            writer.WriteEndObject();
        }

        private static void WriteInteger(Utf8JsonWriter writer, JsonElement element)
        {
            if (element.TryGetInt64(out var signed))
            {
                writer.WriteNumberValue(signed);
                return;
            }

            // Positive integers above long.MaxValue but within ulong range remain exact.
            if (element.TryGetUInt64(out var unsigned))
            {
                writer.WriteNumberValue(unsigned);
                return;
            }

            throw new ArgumentException(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "A probe snapshot number must be an integer within 64-bit range; '{0}' is not.",
                    element.GetRawText()));
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            for (var index = 0; index < bytes.Length; index++)
            {
                var value = bytes[index];
                chars[index * 2] = HexDigits[value >> 4];
                chars[(index * 2) + 1] = HexDigits[value & 0xF];
            }

            return new string(chars);
        }
    }
}
