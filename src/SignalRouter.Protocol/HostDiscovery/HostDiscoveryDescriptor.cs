using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SignalRouter.Protocol.HostDiscovery
{
    // The host discovery descriptor (ADR 0008): the small owner-only JSON file a
    // host publishes so a runtime configured for the same port can learn the live
    // endpoint and present the host's authentication token. The runtime does NOT
    // trust the file's endpoint — a corrupted or foreign descriptor could otherwise
    // send the token to the wrong place — so parsing is strict: schema version,
    // token shape, GUID form, positive pid, a UTC timestamp, a loopback endpoint on
    // exactly the selector port with no userinfo/query/fragment and a root path, a
    // size cap, and rejection of duplicate JSON members. Anything off yields "no
    // descriptor", never a connection.
    public sealed class HostDiscoveryDescriptor
    {
        public const int SchemaVersion = 1;

        public const int MaxDescriptorBytes = 4096;

        private const string SchemaVersionProperty = "schemaVersion";
        private const string InstanceIdProperty = "instanceId";
        private const string EndpointProperty = "endpoint";
        private const string TokenProperty = "token";
        private const string ProcessIdProperty = "pid";
        private const string StartedAtProperty = "startedAt";

        private HostDiscoveryDescriptor(
            Guid instanceId,
            Uri endpoint,
            string token,
            int processId,
            DateTimeOffset startedAt)
        {
            InstanceId = instanceId;
            Endpoint = endpoint;
            Token = token;
            ProcessId = processId;
            StartedAt = startedAt;
        }

        public Guid InstanceId { get; }

        public Uri Endpoint { get; }

        // Exactly 64 lower-case hex characters (the 256-bit token).
        public string Token { get; }

        public int ProcessId { get; }

        // The host process's OS start time, in UTC. With the pid this is a stale
        // heuristic, not proof of host identity (ADR 0008).
        public DateTimeOffset StartedAt { get; }

        // Serializes a descriptor to its canonical JSON form. Used by the host
        // writer; kept next to the parser so the two stay in lock-step.
        public static string Serialize(
            Guid instanceId,
            Uri endpoint,
            string token,
            int processId,
            DateTimeOffset startedAt)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber(SchemaVersionProperty, SchemaVersion);
                writer.WriteString(InstanceIdProperty, instanceId.ToString("D", CultureInfo.InvariantCulture));
                writer.WriteString(EndpointProperty, endpoint.AbsoluteUri);
                writer.WriteString(TokenProperty, token);
                writer.WriteNumber(ProcessIdProperty, processId);
                writer.WriteString(
                    StartedAtProperty,
                    startedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray());
        }

        // Strictly parses and validates a descriptor. selectorPort is the port the
        // runtime is configured for; the endpoint's port must equal it. Returns
        // false (with a null descriptor) on any deviation.
        public static bool TryParse(string json, int selectorPort, out HostDiscoveryDescriptor? descriptor)
        {
            descriptor = null;
            if (json == null || json.Length == 0)
            {
                return false;
            }

            var bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes > MaxDescriptorBytes)
            {
                return false;
            }

            int? schemaVersion = null;
            string? instanceIdText = null;
            string? endpointText = null;
            string? token = null;
            int? processId = null;
            string? startedAtText = null;

            try
            {
                var reader = new Utf8JsonReader(
                    Encoding.UTF8.GetBytes(json),
                    new JsonReaderOptions { MaxDepth = 8 });
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }

                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString()!;
                    if (!seen.Add(name))
                    {
                        // A duplicate member is ambiguous; reject the whole file.
                        return false;
                    }

                    if (!reader.Read())
                    {
                        return false;
                    }

                    switch (name)
                    {
                        case SchemaVersionProperty:
                            if (reader.TokenType != JsonTokenType.Number
                                || !reader.TryGetInt32(out var version))
                            {
                                return false;
                            }

                            schemaVersion = version;
                            break;
                        case InstanceIdProperty:
                            if (reader.TokenType != JsonTokenType.String)
                            {
                                return false;
                            }

                            instanceIdText = reader.GetString();
                            break;
                        case EndpointProperty:
                            if (reader.TokenType != JsonTokenType.String)
                            {
                                return false;
                            }

                            endpointText = reader.GetString();
                            break;
                        case TokenProperty:
                            if (reader.TokenType != JsonTokenType.String)
                            {
                                return false;
                            }

                            token = reader.GetString();
                            break;
                        case ProcessIdProperty:
                            if (reader.TokenType != JsonTokenType.Number
                                || !reader.TryGetInt32(out var pid))
                            {
                                return false;
                            }

                            processId = pid;
                            break;
                        case StartedAtProperty:
                            if (reader.TokenType != JsonTokenType.String)
                            {
                                return false;
                            }

                            startedAtText = reader.GetString();
                            break;
                        default:
                            // Unknown members are skipped for forward compatibility
                            // (schemaVersion gates real additions), but a nested value
                            // must be skipped wholesale.
                            reader.Skip();
                            break;
                    }
                }

                if (reader.TokenType != JsonTokenType.EndObject)
                {
                    return false;
                }
            }
            catch (JsonException)
            {
                return false;
            }

            if (schemaVersion != SchemaVersion
                || instanceIdText == null
                || endpointText == null
                || token == null
                || processId == null
                || startedAtText == null)
            {
                return false;
            }

            if (!Guid.TryParseExact(instanceIdText, "D", out var instanceId))
            {
                return false;
            }

            if (HostHelloAuthenticationPolicy.TryDecodeToken(token) == null)
            {
                return false;
            }

            if (processId.Value <= 0)
            {
                return false;
            }

            if (!TryParseUtc(startedAtText, out var startedAt))
            {
                return false;
            }

            if (!TryParseLoopbackEndpoint(endpointText, selectorPort, out var endpoint))
            {
                return false;
            }

            descriptor = new HostDiscoveryDescriptor(
                instanceId,
                endpoint!,
                token,
                processId.Value,
                startedAt);
            return true;
        }

        private static bool TryParseUtc(string value, out DateTimeOffset startedAt)
        {
            startedAt = default;
            if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
            {
                return false;
            }

            // Require an explicit UTC instant (offset zero), not a local time.
            if (parsed.Offset != TimeSpan.Zero)
            {
                return false;
            }

            startedAt = parsed;
            return true;
        }

        private static bool TryParseLoopbackEndpoint(string value, int selectorPort, out Uri? endpoint)
        {
            endpoint = null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!string.Equals(uri.Scheme, "ws", StringComparison.Ordinal))
            {
                return false;
            }

            if (uri.Port != selectorPort)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
            {
                return false;
            }

            var host = uri.Host.Trim('[', ']');
            if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
            {
                return false;
            }

            endpoint = uri;
            return true;
        }
    }
}
