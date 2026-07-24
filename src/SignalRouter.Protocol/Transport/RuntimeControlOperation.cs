using System;

namespace SignalRouter.Protocol.Transport
{
    // The runtime-side view of a recording/replay control operation (item 8d).
    // The session hands the runtime one of these and receives a RuntimeControlAck
    // back; it never interprets recording semantics itself, only maps the ack onto
    // the wire. Correlation is by the host-assigned OperationId; ControlMessageId
    // is retained so a refusal can reply to the exact control message.
    public enum RuntimeControlKind
    {
        StartRecording,
        StopRecording,
        ReplayRecording,
    }

    public readonly struct RuntimeControlRequest
    {
        public RuntimeControlRequest(
            RuntimeControlKind kind,
            string operationId,
            string controlMessageId,
            string? recordingHandle,
            string? label)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            ProtocolContract.RequireIdentifier(controlMessageId, nameof(controlMessageId));
            Kind = kind;
            OperationId = operationId;
            ControlMessageId = controlMessageId;
            RecordingHandle = recordingHandle;
            Label = label;
        }

        public RuntimeControlKind Kind { get; }

        public string OperationId { get; }

        public string ControlMessageId { get; }

        public string? RecordingHandle { get; }

        public string? Label { get; }
    }

    public enum RuntimeControlAckKind
    {
        RecordingStarted,
        RecordingStopped,
        ReplayReport,
        Refused,
    }

    // The runtime's answer to a control operation. Terminal by construction: the
    // session maps it onto the corresponding wire message (recording_started /
    // recording_stopped / replay_report) or, for Refused, an error correlated to
    // the control message.
    public readonly struct RuntimeControlAck
    {
        private RuntimeControlAck(
            RuntimeControlAckKind kind,
            string operationId,
            string? recordingHandle,
            long entryCount,
            string? outcomeKind,
            string? detail,
            string? refusalCode)
        {
            Kind = kind;
            OperationId = operationId;
            RecordingHandle = recordingHandle;
            EntryCount = entryCount;
            OutcomeKind = outcomeKind;
            Detail = detail;
            RefusalCode = refusalCode;
        }

        public RuntimeControlAckKind Kind { get; }

        public string OperationId { get; }

        public string? RecordingHandle { get; }

        public long EntryCount { get; }

        public string? OutcomeKind { get; }

        public string? Detail { get; }

        public string? RefusalCode { get; }

        public static RuntimeControlAck RecordingStarted(string operationId, string recordingHandle)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            RecordingHandles.Require(recordingHandle, nameof(recordingHandle));
            return new RuntimeControlAck(
                RuntimeControlAckKind.RecordingStarted,
                operationId,
                recordingHandle,
                0,
                null,
                null,
                null);
        }

        public static RuntimeControlAck RecordingStopped(
            string operationId,
            string recordingHandle,
            long entryCount)
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

            return new RuntimeControlAck(
                RuntimeControlAckKind.RecordingStopped,
                operationId,
                recordingHandle,
                entryCount,
                null,
                null,
                null);
        }

        public static RuntimeControlAck ReplayReport(
            string operationId,
            string outcomeKind,
            string? detail)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            ProtocolContract.RequireIdentifier(outcomeKind, nameof(outcomeKind));
            return new RuntimeControlAck(
                RuntimeControlAckKind.ReplayReport,
                operationId,
                null,
                0,
                outcomeKind,
                detail,
                null);
        }

        public static RuntimeControlAck Refused(
            string operationId,
            string refusalCode,
            string? detail)
        {
            ProtocolContract.RequireIdentifier(operationId, nameof(operationId));
            ProtocolContract.RequireIdentifier(refusalCode, nameof(refusalCode));
            return new RuntimeControlAck(
                RuntimeControlAckKind.Refused,
                operationId,
                null,
                0,
                null,
                detail,
                refusalCode);
        }
    }

    // The runtime ledger's answer to a get_control_operation_result query: a
    // lifecycle state, plus the terminal ack when the operation has finished.
    public readonly struct RuntimeControlQueryResult
    {
        private RuntimeControlQueryResult(string state, RuntimeControlAck? terminalAck)
        {
            State = state;
            TerminalAck = terminalAck;
        }

        public string State { get; }

        public RuntimeControlAck? TerminalAck { get; }

        public static RuntimeControlQueryResult Pending()
        {
            return new RuntimeControlQueryResult(ProtocolControlOperationStates.Pending, null);
        }

        public static RuntimeControlQueryResult InProgress()
        {
            return new RuntimeControlQueryResult(ProtocolControlOperationStates.InProgress, null);
        }

        public static RuntimeControlQueryResult Terminal(RuntimeControlAck ack)
        {
            var state = ack.Kind == RuntimeControlAckKind.Refused
                ? ProtocolControlOperationStates.Refused
                : ProtocolControlOperationStates.Completed;
            return new RuntimeControlQueryResult(state, ack);
        }
    }
}
