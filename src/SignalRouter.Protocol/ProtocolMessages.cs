using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SignalRouter.Protocol
{
    // Typed model of protocol envelope v1 (design §18.3, ADR 0007). Every message
    // carries the shared envelope fields; payload fields live on the concrete
    // types. MessageId correlates one transmission (a resend uses a fresh
    // messageId), RequestId identifies one logical interaction across process
    // boundaries (design §7.2), and InReplyTo carries the messageId being answered.
    //
    // These are deliberately plain sealed classes: the default ToString prints
    // only the type name, so externally supplied field values (including the
    // reserved auth token) can never leak through diagnostics by accident.
    public abstract class ProtocolMessage
    {
        private protected ProtocolMessage(
            ProtocolVersion? protocol,
            string messageId,
            string? sessionEpoch,
            string? requestId,
            string? inReplyTo)
        {
            ProtocolContract.RequireIdentifier(messageId, nameof(messageId));
            ProtocolContract.RequireOptionalIdentifier(sessionEpoch, nameof(sessionEpoch));
            ProtocolContract.RequireOptionalIdentifier(requestId, nameof(requestId));
            ProtocolContract.RequireOptionalIdentifier(inReplyTo, nameof(inReplyTo));
            Protocol = protocol ?? ProtocolVersion.Current;
            MessageId = messageId;
            SessionEpoch = sessionEpoch;
            RequestId = requestId;
            InReplyTo = inReplyTo;
        }

        // The version the sender speaks. Locally built messages default to
        // ProtocolVersion.Current; decoded messages carry the peer's envelope
        // value. Tests exercise negotiation by passing explicit versions.
        public ProtocolVersion Protocol { get; }

        public string MessageId { get; }

        public string? SessionEpoch { get; }

        public string? RequestId { get; }

        public string? InReplyTo { get; }

        public abstract string Type { get; }
    }

    // Handshake open, runtime → host. The envelope version is the highest
    // protocol version the runtime supports; the welcome answers with the
    // selected version (ADR 0007). The session epoch is the runtime's current
    // registry epoch (design §13.3).
    public sealed class HelloMessage : ProtocolMessage
    {
        public HelloMessage(
            string messageId,
            string sessionEpoch,
            string peerVersion,
            IEnumerable<string> capabilities,
            int maxReceiveMessageBytes,
            string? authToken = null,
            int recoveryWindowMs = ProtocolLimits.DefaultRecoveryWindowMs,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
            ProtocolContract.RequireText(
                peerVersion,
                ProtocolLimits.MaxTextChars,
                nameof(peerVersion));
            Capabilities = ProtocolContract.CreateCapabilities(capabilities, nameof(capabilities));
            RequireReceiveLimit(maxReceiveMessageBytes);
            ProtocolContract.RequireOptionalIdentifier(authToken, nameof(authToken));
            if (recoveryWindowMs < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recoveryWindowMs),
                    recoveryWindowMs,
                    "The recovery window must be positive.");
            }

            PeerVersion = peerVersion;
            MaxReceiveMessageBytes = maxReceiveMessageBytes;
            AuthToken = authToken;
            RecoveryWindowMs = recoveryWindowMs;
        }

        public string PeerVersion { get; }

        public ReadOnlyCollection<string> Capabilities { get; }

        public int MaxReceiveMessageBytes { get; }

        // The runtime ledger's terminal-result retention, advertised so the
        // host knows how long results stay queryable and — critically — for
        // how long an unavailable query answer proves the request was never
        // received, making a byte-exact resend safe (ADR 0007).
        public int RecoveryWindowMs { get; }

        // Reserved for the §19 security pass: carried opaquely in v1 and validated
        // by the host once item 9 lands. Must never appear in logs, faults, or
        // error replies.
        public string? AuthToken { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.Hello; }
        }

        internal static void RequireReceiveLimit(int maxReceiveMessageBytes)
        {
            // A peer that cannot receive bootstrap-sized messages could never
            // complete a handshake or read an error reply, so such a declaration
            // is a contract violation rather than a negotiable preference.
            if (maxReceiveMessageBytes < ProtocolLimits.BootstrapMaxMessageBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxReceiveMessageBytes),
                    maxReceiveMessageBytes,
                    "Receive limits must be at least the bootstrap message size.");
            }
        }
    }

    // Handshake accept, host → runtime. The envelope version is the negotiated
    // protocol version the session will speak; the epoch echoes the hello's.
    public sealed class WelcomeMessage : ProtocolMessage
    {
        public WelcomeMessage(
            string messageId,
            string sessionEpoch,
            string inReplyTo,
            string peerVersion,
            IEnumerable<string> capabilities,
            int maxReceiveMessageBytes,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                ProtocolContract.RequireIdentifierValue(inReplyTo, nameof(inReplyTo)))
        {
            ProtocolContract.RequireText(
                peerVersion,
                ProtocolLimits.MaxTextChars,
                nameof(peerVersion));
            Capabilities = ProtocolContract.CreateCapabilities(capabilities, nameof(capabilities));
            HelloMessage.RequireReceiveLimit(maxReceiveMessageBytes);
            PeerVersion = peerVersion;
            MaxReceiveMessageBytes = maxReceiveMessageBytes;
        }

        public string PeerVersion { get; }

        public ReadOnlyCollection<string> Capabilities { get; }

        public int MaxReceiveMessageBytes { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.Welcome; }
        }
    }

    // Transport-plane error, either direction. The message text is bounded,
    // single-line, and must never echo payload content or credentials
    // (design §19).
    public sealed class ErrorMessage : ProtocolMessage
    {
        public ErrorMessage(
            string messageId,
            string code,
            string message,
            string? sessionEpoch = null,
            string? requestId = null,
            string? inReplyTo = null,
            ProtocolVersion? protocol = null)
            : base(protocol, messageId, sessionEpoch, requestId, inReplyTo)
        {
            ProtocolContract.RequireIdentifier(code, nameof(code));
            ProtocolContract.RequireText(
                message,
                ProtocolLimits.MaxErrorMessageChars,
                nameof(message));
            Code = code;
            Message = message;
        }

        public string Code { get; }

        public string Message { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.Error; }
        }
    }

    // Protocol-level liveness probe. Distinct from WebSocket control-frame pings:
    // this one is application data, so answering it proves the peer's receive
    // loop is alive, not merely its socket (the main-thread round trip is wired
    // in item 8).
    public sealed class PingMessage : ProtocolMessage
    {
        public PingMessage(
            string messageId,
            string? sessionEpoch = null,
            ProtocolVersion? protocol = null)
            : base(protocol, messageId, sessionEpoch, null, null)
        {
        }

        public override string Type
        {
            get { return ProtocolMessageTypes.Ping; }
        }
    }

    public sealed class PongMessage : ProtocolMessage
    {
        public PongMessage(
            string messageId,
            string inReplyTo,
            string? sessionEpoch = null,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                sessionEpoch,
                null,
                ProtocolContract.RequireIdentifierValue(inReplyTo, nameof(inReplyTo)))
        {
        }

        public override string Type
        {
            get { return ProtocolMessageTypes.Pong; }
        }
    }

    // Host → runtime. The host assigns the request ID (ADR 0007): the submitter
    // owning the identity is what makes the request queryable and safely
    // resendable after a disconnect. Arguments travel as an opaque JSON object;
    // the Core command catalog applies its strict per-command validation at
    // dispatch (lenient transport shell, strict command core).
    public sealed class ExecuteInteractionMessage : ProtocolMessage
    {
        public ExecuteInteractionMessage(
            string messageId,
            string sessionEpoch,
            string requestId,
            string commandName,
            int commandVersion,
            string targetId,
            string argumentsJson,
            string? correlationId = null,
            string? idempotencyKey = null,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                ProtocolContract.RequireIdentifierValue(requestId, nameof(requestId)),
                null)
        {
            ProtocolContract.RequireIdentifier(commandName, nameof(commandName));
            if (commandVersion < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandVersion),
                    commandVersion,
                    "Command version must be positive.");
            }

            ProtocolContract.RequireIdentifier(targetId, nameof(targetId));
            ProtocolContract.RequireJsonObject(
                argumentsJson,
                ProtocolLimits.ArgumentsMaxDepth,
                nameof(argumentsJson));
            ProtocolContract.RequireOptionalIdentifier(correlationId, nameof(correlationId));
            ProtocolContract.RequireOptionalIdentifier(idempotencyKey, nameof(idempotencyKey));
            CommandName = commandName;
            CommandVersion = commandVersion;
            TargetId = targetId;
            ArgumentsJson = argumentsJson;
            CorrelationId = correlationId;
            IdempotencyKey = idempotencyKey;
        }

        public string CommandName { get; }

        public int CommandVersion { get; }

        public string TargetId { get; }

        public string ArgumentsJson { get; }

        public string? CorrelationId { get; }

        public string? IdempotencyKey { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.ExecuteInteraction; }
        }
    }

    // Runtime → host. Acknowledges that the request was atomically reserved and
    // admitted to the FIFO with the given sequence. This is admission
    // bookkeeping, not identity distribution — the host already owns the
    // request ID (ADR 0007).
    public sealed class InteractionAcceptedMessage : ProtocolMessage
    {
        public InteractionAcceptedMessage(
            string messageId,
            string sessionEpoch,
            string requestId,
            string inReplyTo,
            long sequence,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                ProtocolContract.RequireIdentifierValue(requestId, nameof(requestId)),
                ProtocolContract.RequireIdentifierValue(inReplyTo, nameof(inReplyTo)))
        {
            if (sequence < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequence),
                    sequence,
                    "Sequence must be positive.");
            }

            Sequence = sequence;
        }

        public long Sequence { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.InteractionAccepted; }
        }
    }

    // Runtime → host. Carries the sanitized terminal outcome; the envelope
    // request ID always equals the outcome's, so the two can never diverge.
    // InReplyTo points at the execute_interaction or get_interaction_result
    // transmission being answered.
    public sealed class InteractionResultMessage : ProtocolMessage
    {
        public InteractionResultMessage(
            string messageId,
            string sessionEpoch,
            ProtocolInteractionOutcome result,
            string? inReplyTo = null,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                RequireResult(result).RequestId,
                inReplyTo)
        {
            Result = result;
        }

        public ProtocolInteractionOutcome Result { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.InteractionResult; }
        }

        private static ProtocolInteractionOutcome RequireResult(ProtocolInteractionOutcome result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return result;
        }
    }

    // Host → runtime. Queries the terminal outcome for a request ID after a
    // client-side timeout (design §18.2). Answered with interaction_result or
    // error(result_unavailable).
    public sealed class GetInteractionResultMessage : ProtocolMessage
    {
        public GetInteractionResultMessage(
            string messageId,
            string sessionEpoch,
            string requestId,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                ProtocolContract.RequireIdentifierValue(requestId, nameof(requestId)),
                null)
        {
        }

        public override string Type
        {
            get { return ProtocolMessageTypes.GetInteractionResult; }
        }
    }

    // Runtime → host. Answers get_interaction_result for a request that is
    // known but not yet terminal — the wire projection of the ledger's pending
    // states, which is what lets a reconnecting host distinguish "still
    // running, keep waiting" from "never received, safe to resend" (ADR 0007).
    // A terminal request answers with interaction_result instead, so Terminal
    // is not a legal state here.
    public sealed class InteractionStatusMessage : ProtocolMessage
    {
        public InteractionStatusMessage(
            string messageId,
            string sessionEpoch,
            string requestId,
            string inReplyTo,
            ProtocolRequestState state,
            long? sequence,
            bool cancelRequested,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                ProtocolContract.RequireIdentifierValue(requestId, nameof(requestId)),
                ProtocolContract.RequireIdentifierValue(inReplyTo, nameof(inReplyTo)))
        {
            ProtocolContract.RequireDefinedEnum(state, nameof(state));
            if (state == ProtocolRequestState.Terminal)
            {
                throw new ArgumentException(
                    "Terminal requests answer with interaction_result, not a status.",
                    nameof(state));
            }

            if ((state == ProtocolRequestState.Received) != (sequence == null))
            {
                throw new ArgumentException(
                    "A sequence is present exactly when the request was admitted to the FIFO.",
                    nameof(sequence));
            }

            if (sequence != null && sequence.Value < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequence),
                    sequence.Value,
                    "Sequence must be positive.");
            }

            State = state;
            Sequence = sequence;
            CancelRequested = cancelRequested;
        }

        public ProtocolRequestState State { get; }

        public long? Sequence { get; }

        public bool CancelRequested { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.InteractionStatus; }
        }
    }

    // Host → runtime. Requests cancellation of a pending or running interaction.
    // A transport disconnect deliberately does not cancel anything (design §8);
    // recovery combines explicit cancellation with result queries.
    public sealed class CancelInteractionMessage : ProtocolMessage
    {
        public CancelInteractionMessage(
            string messageId,
            string sessionEpoch,
            string requestId,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                ProtocolContract.RequireIdentifierValue(requestId, nameof(requestId)),
                null)
        {
        }

        public override string Type
        {
            get { return ProtocolMessageTypes.CancelInteraction; }
        }
    }

    // Host → runtime. Requests the agent-visible registry snapshot; the host
    // projects its MCP tool views (get_ui_tree, list_interactions) from the
    // reply (item 8).
    public sealed class GetRegistrySnapshotMessage : ProtocolMessage
    {
        public GetRegistrySnapshotMessage(
            string messageId,
            string sessionEpoch,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
        }

        public override string Type
        {
            get { return ProtocolMessageTypes.GetRegistrySnapshot; }
        }
    }

    // Host → runtime. Waits for a bounded runtime condition (design §18.2):
    // dispatcher idleness or the presence/absence of a target with a stable ID.
    // A timeout answers satisfied=false — a normal outcome, not an error. The
    // condition is checked once per frame on the main thread (§17.2).
    public sealed class WaitForMessage : ProtocolMessage
    {
        public WaitForMessage(
            string messageId,
            string sessionEpoch,
            string condition,
            string? targetId,
            int timeoutMs,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
            if (!string.Equals(condition, ProtocolWaitConditions.Idle, StringComparison.Ordinal)
                && !string.Equals(
                    condition,
                    ProtocolWaitConditions.TargetPresent,
                    StringComparison.Ordinal)
                && !string.Equals(
                    condition,
                    ProtocolWaitConditions.TargetAbsent,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The condition is not part of protocol v1.",
                    nameof(condition));
            }

            var needsTarget = !string.Equals(
                condition,
                ProtocolWaitConditions.Idle,
                StringComparison.Ordinal);
            if (needsTarget != (targetId != null))
            {
                throw new ArgumentException(
                    "A target ID is present exactly for the target_* conditions.",
                    nameof(targetId));
            }

            ProtocolContract.RequireOptionalIdentifier(targetId, nameof(targetId));
            if (timeoutMs < 1 || timeoutMs > ProtocolLimits.MaxWaitTimeoutMs)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeoutMs),
                    timeoutMs,
                    "The wait timeout must be within the protocol bound.");
            }

            Condition = condition;
            TargetId = targetId;
            TimeoutMs = timeoutMs;
        }

        public string Condition { get; }

        public string? TargetId { get; }

        public int TimeoutMs { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.WaitFor; }
        }
    }

    // Runtime → host. Answers a wait_for: whether the condition held before
    // the timeout, and how long the wait actually took.
    public sealed class WaitResultMessage : ProtocolMessage
    {
        public WaitResultMessage(
            string messageId,
            string sessionEpoch,
            string inReplyTo,
            string condition,
            bool satisfied,
            long elapsedMs,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                ProtocolContract.RequireIdentifierValue(inReplyTo, nameof(inReplyTo)))
        {
            ProtocolContract.RequireIdentifier(condition, nameof(condition));
            if (elapsedMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elapsedMs),
                    elapsedMs,
                    "Elapsed time must be non-negative.");
            }

            Condition = condition;
            Satisfied = satisfied;
            ElapsedMs = elapsedMs;
        }

        public string Condition { get; }

        public bool Satisfied { get; }

        public long ElapsedMs { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.WaitResult; }
        }
    }

    // The recording/replay acknowledgments arrive on the session they left the
    // runtime in, so their new-epoch payload must equal the validated envelope
    // epoch. Rejecting a mismatch keeps a contradictory acknowledgment — one
    // whose reported epoch is not the one actually active — off the wire
    // (Codex review, item 8d).
    internal static class RecordingEpochContract
    {
        public static void Require(string newSessionEpoch, string? envelopeEpoch)
        {
            ProtocolContract.RequireIdentifier(newSessionEpoch, "newSessionEpoch");
            if (!string.Equals(newSessionEpoch, envelopeEpoch, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The acknowledged epoch must equal the message's session epoch.",
                    "newSessionEpoch");
            }
        }
    }

    // Host → runtime. Begins recording a fresh session. Recording needs a new
    // dispatcher (the recorder is a constructor argument) and therefore a new
    // session epoch, so this recreates the runtime; the reply arrives on the
    // reconnected session under the new epoch and is correlated by the
    // host-assigned operationId, which survives the epoch change (ADR 0007,
    // item 8d). The optional label is agent bookkeeping, never a filename.
    public sealed class StartRecordingMessage : ProtocolMessage
    {
        public StartRecordingMessage(
            string messageId,
            string sessionEpoch,
            string operationId,
            string? label = null,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            if (label != null)
            {
                ProtocolContract.RequireText(label, ProtocolLimits.MaxLabelChars, nameof(label));
            }

            OperationId = operationId;
            Label = label;
        }

        public string OperationId { get; }

        public string? Label { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.StartRecording; }
        }
    }

    // Runtime → host. Acknowledges that recording began under a new session
    // epoch. Carries the runtime-generated recording handle (a filename stem,
    // never a path — design §19) and the new epoch the host must now speak.
    public sealed class RecordingStartedMessage : ProtocolMessage
    {
        public RecordingStartedMessage(
            string messageId,
            string sessionEpoch,
            string operationId,
            string recordingHandle,
            string newSessionEpoch,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            RecordingHandles.Require(recordingHandle, nameof(recordingHandle));
            RecordingEpochContract.Require(newSessionEpoch, sessionEpoch);
            OperationId = operationId;
            RecordingHandle = recordingHandle;
            NewSessionEpoch = newSessionEpoch;
        }

        public string OperationId { get; }

        public string RecordingHandle { get; }

        public string NewSessionEpoch { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.RecordingStarted; }
        }
    }

    // Host → runtime. Ends the active recording, recreating the runtime into a
    // fresh non-recording session.
    public sealed class StopRecordingMessage : ProtocolMessage
    {
        public StopRecordingMessage(
            string messageId,
            string sessionEpoch,
            string operationId,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            OperationId = operationId;
        }

        public string OperationId { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.StopRecording; }
        }
    }

    // Runtime → host. Confirms the finalized recording: its handle, the number
    // of interactions it captured, and the new (non-recording) session epoch.
    public sealed class RecordingStoppedMessage : ProtocolMessage
    {
        public RecordingStoppedMessage(
            string messageId,
            string sessionEpoch,
            string operationId,
            string recordingHandle,
            long entryCount,
            string newSessionEpoch,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            RecordingHandles.Require(recordingHandle, nameof(recordingHandle));
            if (entryCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(entryCount),
                    entryCount,
                    "The entry count must be non-negative.");
            }

            RecordingEpochContract.Require(newSessionEpoch, sessionEpoch);
            OperationId = operationId;
            RecordingHandle = recordingHandle;
            EntryCount = entryCount;
            NewSessionEpoch = newSessionEpoch;
        }

        public string OperationId { get; }

        public string RecordingHandle { get; }

        public long EntryCount { get; }

        public string NewSessionEpoch { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.RecordingStopped; }
        }
    }

    // Host → runtime. Replays a stored recording by handle. The runtime
    // reconstructs a session at the recorded epoch, runs the strict replayer
    // against a private (transport-invisible) runtime, then returns to a fresh
    // live session (item 8d, ADR 0006).
    public sealed class ReplayRecordingMessage : ProtocolMessage
    {
        public ReplayRecordingMessage(
            string messageId,
            string sessionEpoch,
            string operationId,
            string recordingHandle,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            RecordingHandles.Require(recordingHandle, nameof(recordingHandle));
            OperationId = operationId;
            RecordingHandle = recordingHandle;
        }

        public string OperationId { get; }

        public string RecordingHandle { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.ReplayRecording; }
        }
    }

    // Runtime → host. The sanitized strict-replay outcome (design §16.1: the
    // report never carries exception text or argument payloads) plus the new
    // live session epoch the replay left the runtime in. Detail is a bounded,
    // non-secret summary of the first divergence or stop reason.
    public sealed class ReplayReportMessage : ProtocolMessage
    {
        public ReplayReportMessage(
            string messageId,
            string sessionEpoch,
            string operationId,
            string outcomeKind,
            string newSessionEpoch,
            string? detail = null,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            if (!string.Equals(outcomeKind, ProtocolReplayOutcomes.Completed, StringComparison.Ordinal)
                && !string.Equals(outcomeKind, ProtocolReplayOutcomes.Diverged, StringComparison.Ordinal)
                && !string.Equals(outcomeKind, ProtocolReplayOutcomes.Stopped, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The replay outcome kind is not part of protocol v1.",
                    nameof(outcomeKind));
            }

            RecordingEpochContract.Require(newSessionEpoch, sessionEpoch);
            if (detail != null)
            {
                ProtocolContract.RequireText(
                    detail,
                    ProtocolLimits.MaxErrorMessageChars,
                    nameof(detail));
            }

            OperationId = operationId;
            OutcomeKind = outcomeKind;
            NewSessionEpoch = newSessionEpoch;
            Detail = detail;
        }

        public string OperationId { get; }

        public string OutcomeKind { get; }

        public string NewSessionEpoch { get; }

        public string? Detail { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.ReplayReport; }
        }
    }

    // Host → runtime. Queries a control operation by its host-assigned ID so a
    // host that timed out waiting for the acknowledgment, or reconnected after a
    // drop, can reconcile the operation's outcome without re-running it (item 8d
    // control-operation recovery). The runtime answers from its operation ledger
    // with a control_operation_result. Correlated by payload operationId, like
    // the other control messages.
    public sealed class GetControlOperationResultMessage : ProtocolMessage
    {
        public GetControlOperationResultMessage(
            string messageId,
            string sessionEpoch,
            string operationId,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            OperationId = operationId;
        }

        public string OperationId { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.GetControlOperationResult; }
        }
    }

    // Runtime → host. The operation ledger's answer to a query (or an unsolicited
    // late result). Non-terminal states (pending/in_progress) carry no result
    // payload — the host keeps waiting; terminal states (completed/refused) carry
    // the fields of whichever operation ran: a recordingHandle (start/stop), an
    // entryCount (stop), an outcomeKind and detail (replay), or a detail error
    // (refused). newSessionEpoch is always the runtime's current epoch and must
    // equal the envelope epoch, so a stale result can never be re-stamped onto a
    // newer session (Codex review, item 8d).
    public sealed class ControlOperationResultMessage : ProtocolMessage
    {
        public ControlOperationResultMessage(
            string messageId,
            string sessionEpoch,
            string operationId,
            string state,
            string newSessionEpoch,
            string? recordingHandle = null,
            long? entryCount = null,
            string? outcomeKind = null,
            string? detail = null,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                null)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            if (!IsKnownState(state))
            {
                throw new ArgumentException(
                    "The control operation state is not part of protocol v1.",
                    nameof(state));
            }

            RecordingEpochContract.Require(newSessionEpoch, sessionEpoch);

            var terminal = string.Equals(
                    state,
                    ProtocolControlOperationStates.Completed,
                    StringComparison.Ordinal)
                || string.Equals(
                    state,
                    ProtocolControlOperationStates.Refused,
                    StringComparison.Ordinal);
            if (!terminal
                && (recordingHandle != null
                    || entryCount != null
                    || outcomeKind != null
                    || detail != null))
            {
                // A still-running operation has no result to disclose; carrying a
                // payload would let the host act on a non-terminal answer.
                throw new ArgumentException(
                    "A non-terminal control operation result must not carry a payload.",
                    nameof(state));
            }

            if (recordingHandle != null)
            {
                RecordingHandles.Require(recordingHandle, nameof(recordingHandle));
            }

            if (entryCount != null && entryCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(entryCount),
                    entryCount,
                    "The entry count must be non-negative.");
            }

            if (outcomeKind != null
                && !string.Equals(outcomeKind, ProtocolReplayOutcomes.Completed, StringComparison.Ordinal)
                && !string.Equals(outcomeKind, ProtocolReplayOutcomes.Diverged, StringComparison.Ordinal)
                && !string.Equals(outcomeKind, ProtocolReplayOutcomes.Stopped, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The replay outcome kind is not part of protocol v1.",
                    nameof(outcomeKind));
            }

            if (detail != null)
            {
                ProtocolContract.RequireText(
                    detail,
                    ProtocolLimits.MaxErrorMessageChars,
                    nameof(detail));
            }

            // A terminal result must carry exactly one operation shape, so the
            // host can map it unambiguously and never report, say, a recording
            // start with a null handle (Codex review, item 8d):
            //   completed start  : recordingHandle only
            //   completed stop   : recordingHandle + entryCount
            //   completed replay : outcomeKind (+ optional detail)
            //   refused          : detail only
            if (string.Equals(state, ProtocolControlOperationStates.Refused, StringComparison.Ordinal))
            {
                if (recordingHandle != null
                    || entryCount != null
                    || outcomeKind != null
                    || detail == null)
                {
                    throw new ArgumentException(
                        "A refused control operation result must carry only a detail.",
                        nameof(state));
                }
            }
            else if (string.Equals(state, ProtocolControlOperationStates.Completed, StringComparison.Ordinal))
            {
                if (outcomeKind != null)
                {
                    if (recordingHandle != null || entryCount != null)
                    {
                        throw new ArgumentException(
                            "A completed replay result must carry only an outcome kind "
                            + "and optional detail.",
                            nameof(state));
                    }
                }
                else if (recordingHandle != null)
                {
                    if (detail != null)
                    {
                        throw new ArgumentException(
                            "A completed recording result must not carry a detail.",
                            nameof(state));
                    }
                }
                else
                {
                    throw new ArgumentException(
                        "A completed control operation result must carry a recording "
                        + "handle or a replay outcome.",
                        nameof(state));
                }
            }

            OperationId = operationId;
            State = state;
            NewSessionEpoch = newSessionEpoch;
            RecordingHandle = recordingHandle;
            EntryCount = entryCount;
            OutcomeKind = outcomeKind;
            Detail = detail;
        }

        public string OperationId { get; }

        public string State { get; }

        public string NewSessionEpoch { get; }

        public string? RecordingHandle { get; }

        public long? EntryCount { get; }

        public string? OutcomeKind { get; }

        public string? Detail { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.ControlOperationResult; }
        }

        private static bool IsKnownState(string state)
        {
            return string.Equals(state, ProtocolControlOperationStates.Pending, StringComparison.Ordinal)
                || string.Equals(state, ProtocolControlOperationStates.InProgress, StringComparison.Ordinal)
                || string.Equals(state, ProtocolControlOperationStates.Completed, StringComparison.Ordinal)
                || string.Equals(state, ProtocolControlOperationStates.Refused, StringComparison.Ordinal);
        }
    }

    // Runtime → host. Carries the canonical semantic-ui snapshot document
    // (agent view) verbatim, plus the probe schema version that governs its
    // shape (design §13, §16).
    public sealed class RegistrySnapshotMessage : ProtocolMessage
    {
        public RegistrySnapshotMessage(
            string messageId,
            string sessionEpoch,
            string inReplyTo,
            int probeVersion,
            string snapshotJson,
            ProtocolVersion? protocol = null)
            : base(
                protocol,
                messageId,
                ProtocolContract.RequireIdentifierValue(sessionEpoch, nameof(sessionEpoch)),
                null,
                ProtocolContract.RequireIdentifierValue(inReplyTo, nameof(inReplyTo)))
        {
            if (probeVersion < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(probeVersion),
                    probeVersion,
                    "Probe version must be positive.");
            }

            ProtocolContract.RequireJsonObject(
                snapshotJson,
                ProtocolLimits.SnapshotMaxDepth,
                nameof(snapshotJson));
            ProbeVersion = probeVersion;
            SnapshotJson = snapshotJson;
        }

        public int ProbeVersion { get; }

        public string SnapshotJson { get; }

        public override string Type
        {
            get { return ProtocolMessageTypes.RegistrySnapshot; }
        }
    }
}
