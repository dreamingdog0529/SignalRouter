using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SignalRouter.Protocol
{
    public enum ProtocolReadStatus
    {
        Success = 0,
        Malformed = 1,
        UnknownMessageType = 2,
        MessageTooLarge = 3,
        UnsupportedVersion = 4,
    }

    // The decode verdict for one received message. Failures never throw and never
    // echo payload content: the error text is a fixed description, and the
    // message ID and type are carried back only after they individually passed
    // identifier validation, so an error reply can reference them safely
    // (design §19, ADR 0007).
    public sealed class ProtocolReadResult
    {
        private ProtocolReadResult(
            ProtocolReadStatus status,
            ProtocolMessage? message,
            string? errorCode,
            string? errorMessage,
            string? messageId,
            string? messageType)
        {
            Status = status;
            Message = message;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            MessageId = messageId;
            MessageType = messageType;
        }

        public ProtocolReadStatus Status { get; }

        public ProtocolMessage? Message { get; }

        public string? ErrorCode { get; }

        public string? ErrorMessage { get; }

        public string? MessageId { get; }

        public string? MessageType { get; }

        internal static ProtocolReadResult Success(ProtocolMessage message)
        {
            return new ProtocolReadResult(
                ProtocolReadStatus.Success,
                message,
                null,
                null,
                message.MessageId,
                message.Type);
        }

        internal static ProtocolReadResult Failure(
            ProtocolReadStatus status,
            string errorCode,
            string errorMessage,
            string? messageId,
            string? messageType)
        {
            return new ProtocolReadResult(
                status,
                null,
                errorCode,
                errorMessage,
                messageId,
                messageType);
        }
    }

    // Decodes one received UTF-8 JSON envelope into a typed message. The shell is
    // lenient where forward compatibility requires it — unknown envelope and
    // payload members are ignored, unknown message types are reported but never
    // executed — and strict everywhere else: duplicate members, missing or
    // wrong-typed required members, and constructor contract violations are all
    // malformed (design §18.3, ADR 0007). Opaque command arguments pass through
    // verbatim for the Core catalog's strict validation at dispatch.
    public static class ProtocolMessageReader
    {
        public static ProtocolReadResult Read(ReadOnlyMemory<byte> utf8Message, int maxMessageBytes)
        {
            if (maxMessageBytes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxMessageBytes),
                    maxMessageBytes,
                    "The size limit must be positive.");
            }

            // The byte-length gate runs before any parsing so oversized garbage
            // costs nothing but a length comparison (design §19).
            if (utf8Message.Length > maxMessageBytes)
            {
                return ProtocolReadResult.Failure(
                    ProtocolReadStatus.MessageTooLarge,
                    ProtocolErrorCodes.PayloadTooLarge,
                    "The message exceeds the receive size limit.",
                    null,
                    null);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(
                    utf8Message,
                    new JsonDocumentOptions { MaxDepth = ProtocolLimits.MaxJsonDepth });
            }
            catch (JsonException)
            {
                return Malformed("The message is not a valid JSON document.", null, null);
            }

            using (document)
            {
                try
                {
                    return ReadEnvelope(document.RootElement);
                }
                catch (InvalidOperationException)
                {
                    // JsonDocument.Parse accepts lone-surrogate \u escapes but
                    // JsonElement.GetString() refuses to materialize them. That
                    // is peer-controlled data, so it must surface as a verdict,
                    // never as an exception out of the receive loop.
                    return Malformed(
                        "The message carries invalid UTF-16 text.",
                        null,
                        null);
                }
            }
        }

        private static ProtocolReadResult ReadEnvelope(JsonElement root)
        {
            if (!TryReadObject(root, out var envelope))
            {
                return Malformed("The envelope must be a JSON object without repeated members.", null, null);
            }

            if (!TryGetIdentifier(envelope, ProtocolSchema.ProtocolProperty, out var protocolText)
                || !ProtocolVersion.TryParse(protocolText, out var protocol))
            {
                return Malformed("The envelope protocol version is missing or invalid.", null, null);
            }

            if (!TryGetIdentifier(envelope, ProtocolSchema.MessageIdProperty, out var messageId))
            {
                return Malformed("The envelope message ID is missing or invalid.", null, null);
            }

            if (!TryGetIdentifier(envelope, ProtocolSchema.TypeProperty, out var type))
            {
                return Malformed("The envelope message type is missing or invalid.", messageId, null);
            }

            // Majors gate the whole schema: a different major may lay out payloads
            // differently, so decoding stops at the envelope — just enough to
            // answer with an explicit protocol_version_incompatible error
            // (design §18.3).
            if (protocol.Major != ProtocolVersion.CurrentMajor)
            {
                return ProtocolReadResult.Failure(
                    ProtocolReadStatus.UnsupportedVersion,
                    ProtocolErrorCodes.ProtocolVersionIncompatible,
                    "The message uses an incompatible major protocol version.",
                    messageId,
                    type);
            }

            if (!TryGetOptionalIdentifier(
                envelope,
                ProtocolSchema.SessionEpochProperty,
                out var sessionEpoch))
            {
                return Malformed("The envelope session epoch is invalid.", messageId, type);
            }

            if (!TryGetOptionalIdentifier(
                envelope,
                ProtocolSchema.RequestIdProperty,
                out var requestId))
            {
                return Malformed("The envelope request ID is invalid.", messageId, type);
            }

            if (!TryGetOptionalIdentifier(
                envelope,
                ProtocolSchema.InReplyToProperty,
                out var inReplyTo))
            {
                return Malformed("The envelope reply reference is invalid.", messageId, type);
            }

            if (!envelope.TryGetValue(ProtocolSchema.PayloadProperty, out var payloadElement)
                || !TryReadObject(payloadElement, out var payload))
            {
                return Malformed(
                    "The envelope payload must be a JSON object without repeated members.",
                    messageId,
                    type);
            }

            ProtocolMessage? message;
            try
            {
                message = DecodePayload(
                    type,
                    protocol,
                    messageId,
                    sessionEpoch,
                    requestId,
                    inReplyTo,
                    payload);
            }
            catch (MalformedPayloadException)
            {
                return Malformed("The payload violates the message schema.", messageId, type);
            }
            catch (ArgumentException)
            {
                // Message constructors revalidate every wire constraint; a
                // violation means the peer sent contract-breaking data, which is
                // indistinguishable from any other malformed input.
                return Malformed("The payload violates the message schema.", messageId, type);
            }

            if (message == null)
            {
                return ProtocolReadResult.Failure(
                    ProtocolReadStatus.UnknownMessageType,
                    ProtocolErrorCodes.UnknownMessageType,
                    "The message type is not part of this protocol version.",
                    messageId,
                    type);
            }

            return ProtocolReadResult.Success(message);
        }

        private static ProtocolMessage? DecodePayload(
            string type,
            ProtocolVersion protocol,
            string messageId,
            string? sessionEpoch,
            string? requestId,
            string? inReplyTo,
            Dictionary<string, JsonElement> payload)
        {
            switch (type)
            {
                case ProtocolMessageTypes.Hello:
                    RequireAbsentEnvelopeField(requestId);
                    RequireAbsentEnvelopeField(inReplyTo);
                    return new HelloMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        RequireString(payload, ProtocolSchema.PeerVersionProperty),
                        RequireStringArray(payload, ProtocolSchema.CapabilitiesProperty),
                        RequireInt(payload, ProtocolSchema.MaxReceiveMessageBytesProperty),
                        OptionalString(payload, ProtocolSchema.AuthTokenProperty),
                        RequireInt(payload, ProtocolSchema.RecoveryWindowMsProperty),
                        protocol);
                case ProtocolMessageTypes.Welcome:
                    RequireAbsentEnvelopeField(requestId);
                    return new WelcomeMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        RequirePresent(inReplyTo),
                        RequireString(payload, ProtocolSchema.PeerVersionProperty),
                        RequireStringArray(payload, ProtocolSchema.CapabilitiesProperty),
                        RequireInt(payload, ProtocolSchema.MaxReceiveMessageBytesProperty),
                        protocol);
                case ProtocolMessageTypes.Error:
                    return new ErrorMessage(
                        messageId,
                        RequireString(payload, ProtocolSchema.CodeProperty),
                        RequireString(payload, ProtocolSchema.MessageProperty),
                        sessionEpoch,
                        requestId,
                        inReplyTo,
                        protocol);
                case ProtocolMessageTypes.Ping:
                    RequireAbsentEnvelopeField(requestId);
                    RequireAbsentEnvelopeField(inReplyTo);
                    return new PingMessage(messageId, sessionEpoch, protocol);
                case ProtocolMessageTypes.Pong:
                    RequireAbsentEnvelopeField(requestId);
                    return new PongMessage(
                        messageId,
                        RequirePresent(inReplyTo),
                        sessionEpoch,
                        protocol);
                case ProtocolMessageTypes.ExecuteInteraction:
                    RequireAbsentEnvelopeField(inReplyTo);
                    var command = RequireObject(payload, ProtocolSchema.CommandProperty);
                    return new ExecuteInteractionMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        RequirePresent(requestId),
                        RequireString(command, ProtocolSchema.NameProperty),
                        RequireInt(command, ProtocolSchema.VersionProperty),
                        RequireString(command, ProtocolSchema.TargetIdProperty),
                        RequireRawObject(command, ProtocolSchema.ArgumentsProperty),
                        OptionalString(payload, ProtocolSchema.CorrelationIdProperty),
                        OptionalString(payload, ProtocolSchema.IdempotencyKeyProperty),
                        protocol);
                case ProtocolMessageTypes.InteractionAccepted:
                    return new InteractionAcceptedMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        RequirePresent(requestId),
                        RequirePresent(inReplyTo),
                        RequireLong(payload, ProtocolSchema.SequenceProperty),
                        protocol);
                case ProtocolMessageTypes.InteractionResult:
                    return new InteractionResultMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        DecodeOutcome(
                            RequireObject(payload, ProtocolSchema.ResultProperty),
                            RequirePresent(requestId)),
                        inReplyTo,
                        protocol);
                case ProtocolMessageTypes.GetInteractionResult:
                    RequireAbsentEnvelopeField(inReplyTo);
                    return new GetInteractionResultMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        RequirePresent(requestId),
                        protocol);
                case ProtocolMessageTypes.InteractionStatus:
                    return new InteractionStatusMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        RequirePresent(requestId),
                        RequirePresent(inReplyTo),
                        RequireEnum<ProtocolRequestState>(payload, ProtocolSchema.StateProperty),
                        OptionalLong(payload, ProtocolSchema.SequenceProperty),
                        RequireBoolean(payload, ProtocolSchema.CancelRequestedProperty),
                        protocol);
                case ProtocolMessageTypes.CancelInteraction:
                    RequireAbsentEnvelopeField(inReplyTo);
                    return new CancelInteractionMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        RequirePresent(requestId),
                        protocol);
                case ProtocolMessageTypes.GetRegistrySnapshot:
                    RequireAbsentEnvelopeField(requestId);
                    RequireAbsentEnvelopeField(inReplyTo);
                    return new GetRegistrySnapshotMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        protocol);
                case ProtocolMessageTypes.RegistrySnapshot:
                    RequireAbsentEnvelopeField(requestId);
                    return new RegistrySnapshotMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        RequirePresent(inReplyTo),
                        RequireInt(payload, ProtocolSchema.ProbeVersionProperty),
                        RequireRawObject(payload, ProtocolSchema.SnapshotProperty),
                        protocol);
                case ProtocolMessageTypes.WaitFor:
                    RequireAbsentEnvelopeField(requestId);
                    RequireAbsentEnvelopeField(inReplyTo);
                    return new WaitForMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        RequireString(payload, ProtocolSchema.ConditionProperty),
                        OptionalString(payload, ProtocolSchema.TargetIdProperty),
                        RequireInt(payload, ProtocolSchema.TimeoutMsProperty),
                        protocol);
                case ProtocolMessageTypes.WaitResult:
                    RequireAbsentEnvelopeField(requestId);
                    return new WaitResultMessage(
                        messageId,
                        RequirePresent(sessionEpoch),
                        RequirePresent(inReplyTo),
                        RequireString(payload, ProtocolSchema.ConditionProperty),
                        RequireBoolean(payload, ProtocolSchema.SatisfiedProperty),
                        RequireLong(payload, ProtocolSchema.ElapsedMsProperty),
                        protocol);
                default:
                    return null;
            }
        }

        private static ProtocolInteractionOutcome DecodeOutcome(
            Dictionary<string, JsonElement> result,
            string requestId)
        {
            var command = RequireObject(result, ProtocolSchema.CommandProperty);
            var status = RequireEnum<InteractionStatus>(result, ProtocolSchema.StatusProperty);
            var stages = new List<InteractionStageProgress>();
            var stagesElement = RequireArray(result, ProtocolSchema.StagesProperty);
            var stageIndex = 0;
            foreach (var stageElement in stagesElement.EnumerateArray())
            {
                if (!TryReadObject(stageElement, out var stage))
                {
                    throw new MalformedPayloadException();
                }

                stages.Add(new InteractionStageProgress(
                    RequireString(stage, ProtocolSchema.StageIdProperty),
                    stageIndex,
                    RequireEnum<InteractionStageStatus>(stage, ProtocolSchema.StatusProperty)));
                stageIndex++;
            }

            // Code fields must be present exactly when their status requires
            // them: a succeeded result carrying a rejection code is contradictory
            // wire data, not a forward-compatible extension.
            InteractionRejectionCode? rejectionCode = null;
            if (status == InteractionStatus.Rejected)
            {
                rejectionCode = RequireEnum<InteractionRejectionCode>(
                    result,
                    ProtocolSchema.RejectionCodeProperty);
            }
            else
            {
                RequireAbsent(result, ProtocolSchema.RejectionCodeProperty);
            }

            string? faultCode = null;
            if (status == InteractionStatus.Faulted)
            {
                faultCode = RequireNullableString(result, ProtocolSchema.FaultCodeProperty);
            }
            else
            {
                RequireAbsent(result, ProtocolSchema.FaultCodeProperty);
            }

            var state = RequireObject(result, ProtocolSchema.StateProperty);
            return new ProtocolInteractionOutcome(
                RequireLong(result, ProtocolSchema.SequenceProperty),
                requestId,
                RequireString(result, ProtocolSchema.TargetIdProperty),
                RequireString(command, ProtocolSchema.NameProperty),
                RequireInt(command, ProtocolSchema.VersionProperty),
                RequireEnum<InteractionOrigin>(result, ProtocolSchema.OriginProperty),
                status,
                stages,
                rejectionCode,
                faultCode,
                DecodeObservation(state, ProtocolSchema.BeforeProperty),
                DecodeObservation(state, ProtocolSchema.AfterProperty));
        }

        private static StateObservation DecodeObservation(
            Dictionary<string, JsonElement> state,
            string propertyName)
        {
            var map = RequireObject(state, propertyName);
            var probes = new List<StateProbeObservation>(map.Count);
            foreach (var entry in map)
            {
                if (entry.Value.ValueKind != JsonValueKind.String)
                {
                    throw new MalformedPayloadException();
                }

                probes.Add(new StateProbeObservation(entry.Key, entry.Value.GetString()!));
            }

            return new StateObservation(probes);
        }

        private static ProtocolReadResult Malformed(
            string description,
            string? messageId,
            string? messageType)
        {
            return ProtocolReadResult.Failure(
                ProtocolReadStatus.Malformed,
                ProtocolErrorCodes.MalformedMessage,
                description,
                messageId,
                messageType);
        }

        private static bool TryReadObject(
            JsonElement element,
            out Dictionary<string, JsonElement> properties)
        {
            properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // System.Text.Json tolerates repeated members; typed objects reject
            // them here so two occurrences of a field can never disagree about
            // which value was validated (ADR 0007). Opaque payloads keep their
            // own strictness in the Core codecs.
            foreach (var property in element.EnumerateObject())
            {
                if (properties.ContainsKey(property.Name))
                {
                    return false;
                }

                properties.Add(property.Name, property.Value);
            }

            return true;
        }

        private static bool TryGetIdentifier(
            Dictionary<string, JsonElement> properties,
            string name,
            out string value)
        {
            value = string.Empty;
            if (!properties.TryGetValue(name, out var element)
                || element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var text = element.GetString()!;
            if (!ProtocolContract.IsIdentifier(text))
            {
                return false;
            }

            value = text;
            return true;
        }

        private static bool TryGetOptionalIdentifier(
            Dictionary<string, JsonElement> properties,
            string name,
            out string? value)
        {
            value = null;
            if (!properties.TryGetValue(name, out _))
            {
                return true;
            }

            if (!TryGetIdentifier(properties, name, out var present))
            {
                return false;
            }

            value = present;
            return true;
        }

        private static string RequirePresent(string? value)
        {
            return value ?? throw new MalformedPayloadException();
        }

        private static void RequireAbsentEnvelopeField(string? value)
        {
            if (value != null)
            {
                throw new MalformedPayloadException();
            }
        }

        private static void RequireAbsent(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (properties.ContainsKey(name))
            {
                throw new MalformedPayloadException();
            }
        }

        private static string RequireString(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (!properties.TryGetValue(name, out var element)
                || element.ValueKind != JsonValueKind.String)
            {
                throw new MalformedPayloadException();
            }

            return element.GetString()!;
        }

        private static string? OptionalString(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (!properties.TryGetValue(name, out var element))
            {
                return null;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                throw new MalformedPayloadException();
            }

            return element.GetString()!;
        }

        private static string? RequireNullableString(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (!properties.TryGetValue(name, out var element))
            {
                throw new MalformedPayloadException();
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                throw new MalformedPayloadException();
            }

            return element.GetString()!;
        }

        private static int RequireInt(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (!properties.TryGetValue(name, out var element)
                || element.ValueKind != JsonValueKind.Number
                || !element.TryGetInt32(out var value))
            {
                throw new MalformedPayloadException();
            }

            return value;
        }

        private static long RequireLong(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (!properties.TryGetValue(name, out var element)
                || element.ValueKind != JsonValueKind.Number
                || !element.TryGetInt64(out var value))
            {
                throw new MalformedPayloadException();
            }

            return value;
        }

        private static long? OptionalLong(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (!properties.TryGetValue(name, out _))
            {
                return null;
            }

            return RequireLong(properties, name);
        }

        private static bool RequireBoolean(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (!properties.TryGetValue(name, out var element))
            {
                throw new MalformedPayloadException();
            }

            if (element.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            throw new MalformedPayloadException();
        }

        private static Dictionary<string, JsonElement> RequireObject(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (!properties.TryGetValue(name, out var element)
                || !TryReadObject(element, out var value))
            {
                throw new MalformedPayloadException();
            }

            return value;
        }

        private static JsonElement RequireArray(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (!properties.TryGetValue(name, out var element)
                || element.ValueKind != JsonValueKind.Array)
            {
                throw new MalformedPayloadException();
            }

            return element;
        }

        private static string RequireRawObject(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            if (!properties.TryGetValue(name, out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                throw new MalformedPayloadException();
            }

            return element.GetRawText();
        }

        private static List<string> RequireStringArray(
            Dictionary<string, JsonElement> properties,
            string name)
        {
            var element = RequireArray(properties, name);
            var values = new List<string>();
            foreach (var entry in element.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                {
                    throw new MalformedPayloadException();
                }

                values.Add(entry.GetString()!);
            }

            return values;
        }

        // Strict name-only parsing: numeric strings and unknown names are wire
        // corruption, never coerced (mirrors the recording reader's rule).
        private static TEnum RequireEnum<TEnum>(
            Dictionary<string, JsonElement> properties,
            string name)
            where TEnum : struct
        {
            var text = RequireString(properties, name);
            if (Enum.TryParse<TEnum>(text, ignoreCase: false, out var parsed)
                && Enum.IsDefined(typeof(TEnum), parsed)
                && string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
            {
                return parsed;
            }

            throw new MalformedPayloadException();
        }

        // Internal control flow for payload decoding only: every throw site is a
        // schema violation that the single catch in ReadEnvelope converts into a
        // Malformed result. It never escapes the reader.
        private sealed class MalformedPayloadException : Exception
        {
        }
    }
}
