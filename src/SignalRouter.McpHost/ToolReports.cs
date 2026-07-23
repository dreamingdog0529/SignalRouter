using System.Buffers;
using System.Text;
using System.Text.Json;
using SignalRouter;
using SignalRouter.Protocol;

namespace SignalRouter.McpHost;

// JSON projections of wire payloads for the MCP tool surface. Tools never
// reconstruct Core types (plan 8c): everything an agent sees is derived from
// what actually crossed the wire, written with the same explicit writer style
// the codecs use.
internal static class ToolReports
{
    public static string FromExecuteReport(HostExecuteReport report)
    {
        return WriteObject(writer =>
        {
            writer.WriteString("status", report.Status);
            writer.WriteString("requestId", report.RequestId);
            if (report.Outcome != null)
            {
                writer.WritePropertyName("outcome");
                WriteOutcome(writer, report.Outcome);
            }

            if (report.ErrorCode != null)
            {
                writer.WriteString("errorCode", report.ErrorCode);
            }

            if (report.Detail != null)
            {
                writer.WriteString("detail", report.Detail);
            }
        });
    }

    public static string FromQueryReply(string requestId, ProtocolMessage? reply)
    {
        return WriteObject(writer =>
        {
            writer.WriteString("requestId", requestId);
            switch (reply)
            {
                case InteractionResultMessage result:
                    writer.WriteString("status", "completed");
                    writer.WritePropertyName("outcome");
                    WriteOutcome(writer, result.Result);
                    return;
                case InteractionStatusMessage status:
                    writer.WriteString("status", "pending");
                    writer.WriteString("state", status.State.ToString());
                    if (status.Sequence != null)
                    {
                        writer.WriteNumber("sequence", status.Sequence.Value);
                    }

                    writer.WriteBoolean("cancelRequested", status.CancelRequested);
                    return;
                case ErrorMessage error:
                    writer.WriteString(
                        "status",
                        string.Equals(
                            error.Code,
                            ProtocolErrorCodes.ResultUnavailable,
                            StringComparison.Ordinal)
                            ? "outcome_unknown"
                            : "error");
                    writer.WriteString("errorCode", error.Code);
                    return;
                default:
                    // No reply arrived: the runtime is unreachable, which per
                    // design §8 must surface as an unknown outcome, never an
                    // invented one.
                    writer.WriteString("status", "outcome_unknown");
                    writer.WriteString("detail", "runtime_unreachable");
                    return;
            }
        });
    }

    public static string FromUiTree(RegistrySnapshotMessage snapshot)
    {
        return WriteObject(writer =>
        {
            writer.WriteNumber("probeVersion", snapshot.ProbeVersion);
            writer.WritePropertyName("tree");
            writer.WriteRawValue(snapshot.SnapshotJson);
        });
    }

    // list_interactions is a host-side projection of the same snapshot
    // (ADR 0007): target IDs and their available interactions only.
    public static string FromInteractionList(RegistrySnapshotMessage snapshot)
    {
        using var document = JsonDocument.Parse(snapshot.SnapshotJson);
        return WriteObject(writer =>
        {
            if (document.RootElement.TryGetProperty("revision", out var revision))
            {
                writer.WritePropertyName("revision");
                revision.WriteTo(writer);
            }

            writer.WritePropertyName("targets");
            writer.WriteStartArray();
            if (document.RootElement.TryGetProperty("targets", out var targets)
                && targets.ValueKind == JsonValueKind.Array)
            {
                foreach (var target in targets.EnumerateArray())
                {
                    writer.WriteStartObject();
                    if (target.TryGetProperty("id", out var id))
                    {
                        writer.WritePropertyName("id");
                        id.WriteTo(writer);
                    }

                    if (target.TryGetProperty("availableInteractions", out var interactions))
                    {
                        writer.WritePropertyName("availableInteractions");
                        interactions.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        });
    }

    public static string FromWaitResult(string condition, WaitResultMessage? result)
    {
        return WriteObject(writer =>
        {
            writer.WriteString("condition", condition);
            if (result == null)
            {
                writer.WriteString("status", "runtime_unreachable");
                return;
            }

            writer.WriteBoolean("satisfied", result.Satisfied);
            writer.WriteNumber("elapsedMs", result.ElapsedMs);
        });
    }

    public static string Disconnected()
    {
        return WriteObject(writer =>
        {
            writer.WriteString("status", "disconnected");
            writer.WriteString("detail", "No runtime is connected to the host.");
        });
    }

    private static void WriteOutcome(Utf8JsonWriter writer, ProtocolInteractionOutcome outcome)
    {
        writer.WriteStartObject();
        writer.WriteString("status", outcome.Status.ToString());
        writer.WriteNumber("sequence", outcome.Sequence);
        writer.WriteString("targetId", outcome.TargetId);
        writer.WriteString("commandName", outcome.CommandName);
        writer.WriteNumber("commandVersion", outcome.CommandVersion);
        writer.WriteString("origin", outcome.Origin.ToString());
        writer.WritePropertyName("stages");
        writer.WriteStartArray();
        foreach (var stage in outcome.Stages)
        {
            writer.WriteStartObject();
            writer.WriteString("id", stage.Id);
            writer.WriteString("status", stage.Status.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (outcome.RejectionCode != null)
        {
            writer.WriteString("rejectionCode", outcome.RejectionCode.Value.ToString());
        }

        if (outcome.Status == InteractionStatus.Faulted)
        {
            if (outcome.FaultCode == null)
            {
                writer.WriteNull("faultCode");
            }
            else
            {
                writer.WriteString("faultCode", outcome.FaultCode);
            }
        }

        writer.WritePropertyName("state");
        writer.WriteStartObject();
        WriteObservation(writer, "before", outcome.Before);
        WriteObservation(writer, "after", outcome.After);
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

    private static string WriteObject(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
