using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;

namespace SignalRouter.Protocol
{
    // Encodes one protocol message as one UTF-8 JSON envelope — the unit that
    // becomes one complete WebSocket text message in item 8. Encoding enforces
    // the peer's negotiated receive limit while writing, not after, so an
    // oversized payload can never force an unbounded allocation (design §19).
    public static class ProtocolMessageWriter
    {
        public static byte[] Encode(ProtocolMessage message, int maxMessageBytes)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (maxMessageBytes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxMessageBytes),
                    maxMessageBytes,
                    "The size limit must be positive.");
            }

            var buffer = new BoundedBufferWriter(maxMessageBytes);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteEnvelope(writer, message);
            }

            return buffer.ToArray();
        }

        private static void WriteEnvelope(Utf8JsonWriter writer, ProtocolMessage message)
        {
            writer.WriteStartObject();
            writer.WriteString(ProtocolSchema.ProtocolProperty, message.Protocol.ToString());
            writer.WriteString(ProtocolSchema.MessageIdProperty, message.MessageId);
            writer.WriteString(ProtocolSchema.TypeProperty, message.Type);
            if (message.SessionEpoch != null)
            {
                writer.WriteString(ProtocolSchema.SessionEpochProperty, message.SessionEpoch);
            }

            if (message.RequestId != null)
            {
                writer.WriteString(ProtocolSchema.RequestIdProperty, message.RequestId);
            }

            if (message.InReplyTo != null)
            {
                writer.WriteString(ProtocolSchema.InReplyToProperty, message.InReplyTo);
            }

            writer.WritePropertyName(ProtocolSchema.PayloadProperty);
            writer.WriteStartObject();
            WritePayload(writer, message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private static void WritePayload(Utf8JsonWriter writer, ProtocolMessage message)
        {
            switch (message)
            {
                case HelloMessage hello:
                    WriteHandshakeFields(
                        writer,
                        hello.PeerVersion,
                        hello.Capabilities,
                        hello.MaxReceiveMessageBytes);
                    writer.WriteNumber(
                        ProtocolSchema.RecoveryWindowMsProperty,
                        hello.RecoveryWindowMs);
                    if (hello.AuthToken != null)
                    {
                        writer.WriteString(ProtocolSchema.AuthTokenProperty, hello.AuthToken);
                    }

                    return;
                case WelcomeMessage welcome:
                    WriteHandshakeFields(
                        writer,
                        welcome.PeerVersion,
                        welcome.Capabilities,
                        welcome.MaxReceiveMessageBytes);
                    return;
                case ErrorMessage error:
                    writer.WriteString(ProtocolSchema.CodeProperty, error.Code);
                    writer.WriteString(ProtocolSchema.MessageProperty, error.Message);
                    return;
                case PingMessage _:
                case PongMessage _:
                case GetInteractionResultMessage _:
                case CancelInteractionMessage _:
                case GetRegistrySnapshotMessage _:
                    return;
                case ExecuteInteractionMessage execute:
                    writer.WritePropertyName(ProtocolSchema.CommandProperty);
                    writer.WriteStartObject();
                    writer.WriteString(ProtocolSchema.NameProperty, execute.CommandName);
                    writer.WriteNumber(ProtocolSchema.VersionProperty, execute.CommandVersion);
                    writer.WriteString(ProtocolSchema.TargetIdProperty, execute.TargetId);
                    writer.WritePropertyName(ProtocolSchema.ArgumentsProperty);
                    writer.WriteRawValue(execute.ArgumentsJson);
                    writer.WriteEndObject();
                    if (execute.CorrelationId != null)
                    {
                        writer.WriteString(
                            ProtocolSchema.CorrelationIdProperty,
                            execute.CorrelationId);
                    }

                    if (execute.IdempotencyKey != null)
                    {
                        writer.WriteString(
                            ProtocolSchema.IdempotencyKeyProperty,
                            execute.IdempotencyKey);
                    }

                    return;
                case InteractionAcceptedMessage accepted:
                    writer.WriteNumber(ProtocolSchema.SequenceProperty, accepted.Sequence);
                    return;
                case InteractionStatusMessage status:
                    writer.WriteString(
                        ProtocolSchema.StateProperty,
                        status.State.ToString());
                    if (status.Sequence != null)
                    {
                        writer.WriteNumber(
                            ProtocolSchema.SequenceProperty,
                            status.Sequence.Value);
                    }

                    writer.WriteBoolean(
                        ProtocolSchema.CancelRequestedProperty,
                        status.CancelRequested);
                    return;
                case InteractionResultMessage result:
                    writer.WritePropertyName(ProtocolSchema.ResultProperty);
                    WriteOutcome(writer, result.Result);
                    return;
                case RegistrySnapshotMessage snapshot:
                    writer.WriteNumber(ProtocolSchema.ProbeVersionProperty, snapshot.ProbeVersion);
                    writer.WritePropertyName(ProtocolSchema.SnapshotProperty);
                    writer.WriteRawValue(snapshot.SnapshotJson);
                    return;
                case WaitForMessage waitFor:
                    writer.WriteString(ProtocolSchema.ConditionProperty, waitFor.Condition);
                    if (waitFor.TargetId != null)
                    {
                        writer.WriteString(ProtocolSchema.TargetIdProperty, waitFor.TargetId);
                    }

                    writer.WriteNumber(ProtocolSchema.TimeoutMsProperty, waitFor.TimeoutMs);
                    return;
                case WaitResultMessage waitResult:
                    writer.WriteString(ProtocolSchema.ConditionProperty, waitResult.Condition);
                    writer.WriteBoolean(ProtocolSchema.SatisfiedProperty, waitResult.Satisfied);
                    writer.WriteNumber(ProtocolSchema.ElapsedMsProperty, waitResult.ElapsedMs);
                    return;
                case StartRecordingMessage startRecording:
                    writer.WriteString(
                        ProtocolSchema.OperationIdProperty,
                        startRecording.OperationId);
                    if (startRecording.Label != null)
                    {
                        writer.WriteString(ProtocolSchema.LabelProperty, startRecording.Label);
                    }

                    return;
                case RecordingStartedMessage recordingStarted:
                    writer.WriteString(
                        ProtocolSchema.OperationIdProperty,
                        recordingStarted.OperationId);
                    writer.WriteString(
                        ProtocolSchema.RecordingHandleProperty,
                        recordingStarted.RecordingHandle);
                    writer.WriteString(
                        ProtocolSchema.NewSessionEpochProperty,
                        recordingStarted.NewSessionEpoch);
                    return;
                case StopRecordingMessage stopRecording:
                    writer.WriteString(
                        ProtocolSchema.OperationIdProperty,
                        stopRecording.OperationId);
                    return;
                case RecordingStoppedMessage recordingStopped:
                    writer.WriteString(
                        ProtocolSchema.OperationIdProperty,
                        recordingStopped.OperationId);
                    writer.WriteString(
                        ProtocolSchema.RecordingHandleProperty,
                        recordingStopped.RecordingHandle);
                    writer.WriteNumber(
                        ProtocolSchema.EntryCountProperty,
                        recordingStopped.EntryCount);
                    writer.WriteString(
                        ProtocolSchema.NewSessionEpochProperty,
                        recordingStopped.NewSessionEpoch);
                    return;
                case ReplayRecordingMessage replayRecording:
                    writer.WriteString(
                        ProtocolSchema.OperationIdProperty,
                        replayRecording.OperationId);
                    writer.WriteString(
                        ProtocolSchema.RecordingHandleProperty,
                        replayRecording.RecordingHandle);
                    return;
                case ReplayReportMessage replayReport:
                    writer.WriteString(
                        ProtocolSchema.OperationIdProperty,
                        replayReport.OperationId);
                    writer.WriteString(
                        ProtocolSchema.OutcomeKindProperty,
                        replayReport.OutcomeKind);
                    writer.WriteString(
                        ProtocolSchema.NewSessionEpochProperty,
                        replayReport.NewSessionEpoch);
                    if (replayReport.Detail != null)
                    {
                        writer.WriteString(ProtocolSchema.DetailProperty, replayReport.Detail);
                    }

                    return;
                default:
                    throw new ArgumentException(
                        "The message type '" + message.Type + "' has no v1 payload writer.",
                        nameof(message));
            }
        }

        private static void WriteHandshakeFields(
            Utf8JsonWriter writer,
            string peerVersion,
            IReadOnlyList<string> capabilities,
            int maxReceiveMessageBytes)
        {
            writer.WriteString(ProtocolSchema.PeerVersionProperty, peerVersion);
            writer.WritePropertyName(ProtocolSchema.CapabilitiesProperty);
            writer.WriteStartArray();
            for (var index = 0; index < capabilities.Count; index++)
            {
                writer.WriteStringValue(capabilities[index]);
            }

            writer.WriteEndArray();
            writer.WriteNumber(
                ProtocolSchema.MaxReceiveMessageBytesProperty,
                maxReceiveMessageBytes);
        }

        // The wire outcome mirrors the recording's sanitized projection rules
        // (design §19): status-specific codes only, probe hashes only, and the
        // request ID lives on the envelope rather than in the payload.
        private static void WriteOutcome(Utf8JsonWriter writer, ProtocolInteractionOutcome outcome)
        {
            writer.WriteStartObject();
            writer.WriteNumber(ProtocolSchema.SequenceProperty, outcome.Sequence);
            writer.WriteString(ProtocolSchema.TargetIdProperty, outcome.TargetId);
            writer.WritePropertyName(ProtocolSchema.CommandProperty);
            writer.WriteStartObject();
            writer.WriteString(ProtocolSchema.NameProperty, outcome.CommandName);
            writer.WriteNumber(ProtocolSchema.VersionProperty, outcome.CommandVersion);
            writer.WriteEndObject();
            writer.WriteString(ProtocolSchema.OriginProperty, outcome.Origin.ToString());
            writer.WriteString(ProtocolSchema.StatusProperty, outcome.Status.ToString());
            writer.WritePropertyName(ProtocolSchema.StagesProperty);
            writer.WriteStartArray();
            foreach (var stage in outcome.Stages)
            {
                writer.WriteStartObject();
                writer.WriteString(ProtocolSchema.StageIdProperty, stage.Id);
                writer.WriteString(ProtocolSchema.StatusProperty, stage.Status.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (outcome.Status == InteractionStatus.Rejected)
            {
                writer.WriteString(
                    ProtocolSchema.RejectionCodeProperty,
                    outcome.RejectionCode!.Value.ToString());
            }

            if (outcome.Status == InteractionStatus.Faulted)
            {
                if (outcome.FaultCode == null)
                {
                    writer.WriteNull(ProtocolSchema.FaultCodeProperty);
                }
                else
                {
                    writer.WriteString(ProtocolSchema.FaultCodeProperty, outcome.FaultCode);
                }
            }

            writer.WritePropertyName(ProtocolSchema.StateProperty);
            writer.WriteStartObject();
            WriteObservation(writer, ProtocolSchema.BeforeProperty, outcome.Before);
            WriteObservation(writer, ProtocolSchema.AfterProperty, outcome.After);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private static void WriteObservation(
            Utf8JsonWriter writer,
            string propertyName,
            StateObservation observation)
        {
            writer.WritePropertyName(propertyName);
            writer.WriteStartObject();
            foreach (var probe in observation.Probes)
            {
                writer.WriteString(probe.ProbeId, probe.Hash);
            }

            writer.WriteEndObject();
        }

        // Enforces the size limit as bytes are committed. Utf8JsonWriter flushes
        // through Advance, so an overflowing encode fails mid-write instead of
        // after materializing the whole oversized message.
        private sealed class BoundedBufferWriter : IBufferWriter<byte>
        {
            private readonly ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();
            private readonly int maxBytes;

            public BoundedBufferWriter(int maxBytes)
            {
                this.maxBytes = maxBytes;
            }

            // A span request is sized for the worst-case encoding of one token,
            // which for any message that could still fit the limit is bounded by
            // a small multiple of it (UTF-16 → escaped UTF-8 expands at most
            // 6x). Requests beyond that can only come from a payload that is
            // guaranteed to overflow, so they are refused before the underlying
            // buffer grows towards the payload's size.
            private const int TokenExpansionBound = 7;

            public void Advance(int count)
            {
                if (buffer.WrittenCount + count > maxBytes)
                {
                    throw Overflow();
                }

                buffer.Advance(count);
            }

            public Memory<byte> GetMemory(int sizeHint = 0)
            {
                RequireBoundedRequest(sizeHint);
                return buffer.GetMemory(sizeHint);
            }

            public Span<byte> GetSpan(int sizeHint = 0)
            {
                RequireBoundedRequest(sizeHint);
                return buffer.GetSpan(sizeHint);
            }

            public byte[] ToArray()
            {
                return buffer.WrittenSpan.ToArray();
            }

            private void RequireBoundedRequest(int sizeHint)
            {
                if (buffer.WrittenCount + (long)sizeHint > (long)maxBytes * TokenExpansionBound)
                {
                    throw Overflow();
                }
            }

            private InvalidOperationException Overflow()
            {
                return new InvalidOperationException(
                    "The encoded message exceeds the "
                    + maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "-byte size limit.");
            }
        }
    }
}
