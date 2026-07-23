using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SignalRouter.Protocol;

namespace SignalRouter.McpHost;

// The initial MCP tool surface (design §18.2), mapped 1:1 onto the runtime
// protocol via the host bridge. Every response is a JSON projection of wire
// payloads; execute responses always carry the request ID — including the
// pending shape a tool timeout answers — so a caller can continue with
// get_interaction_result after its own timeout (ADR 0007).
[McpServerToolType]
public sealed class SignalRouterTools
{
    private readonly HostBridge bridge;

    public SignalRouterTools(HostBridge bridge)
    {
        this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    [McpServerTool(Name = "execute_interaction")]
    [Description(
        "Executes one interaction on the connected Unity runtime and waits for its "
        + "terminal outcome. If the tool times out first, the response reports "
        + "status \"pending\" with the request ID to query later.")]
    public async Task<string> ExecuteInteraction(
        [Description("Stable target ID from get_ui_tree.")] string targetId,
        [Description("Command wire name, e.g. \"click\" or \"set_value\".")] string commandName,
        [Description("Command schema version, e.g. 1.")] int commandVersion,
        [Description("Command arguments as a JSON object; {} when the command takes none.")]
        JsonElement arguments,
        [Description("Optional caller-owned request ID; supply one to make later "
            + "get_interaction_result queries possible after a client-side timeout.")]
        string? requestId = null,
        [Description("Optional idempotency key forwarded to the runtime ledger.")]
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The arguments must be a JSON object.", nameof(arguments));
        }

        var report = await bridge.ExecuteInteractionAsync(
            requestId,
            targetId,
            commandName,
            commandVersion,
            arguments.GetRawText(),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return ToolReports.FromExecuteReport(report);
    }

    [McpServerTool(Name = "get_interaction_result")]
    [Description(
        "Queries a previously submitted interaction by request ID: answers the "
        + "terminal outcome, a pending status, or outcome_unknown when the result "
        + "is no longer retained.")]
    public async Task<string> GetInteractionResult(
        [Description("The request ID returned by execute_interaction.")] string requestId,
        CancellationToken cancellationToken = default)
    {
        if (!bridge.IsConnected)
        {
            return ToolReports.Disconnected();
        }

        var reply = await bridge.GetInteractionResultAsync(requestId, cancellationToken)
            .ConfigureAwait(false);
        return ToolReports.FromQueryReply(requestId, reply);
    }

    [McpServerTool(Name = "get_ui_tree")]
    [Description(
        "Returns the agent-visible semantic UI tree: the canonical registry "
        + "snapshot with session epoch, revision, and per-target descriptors.")]
    public async Task<string> GetUiTree(CancellationToken cancellationToken = default)
    {
        var snapshot = await bridge.GetRegistrySnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        return snapshot == null ? ToolReports.Disconnected() : ToolReports.FromUiTree(snapshot);
    }

    [McpServerTool(Name = "list_interactions")]
    [Description(
        "Lists every agent-visible target ID with its available interactions "
        + "(wire name, version, and argument schema).")]
    public async Task<string> ListInteractions(CancellationToken cancellationToken = default)
    {
        var snapshot = await bridge.GetRegistrySnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        return snapshot == null
            ? ToolReports.Disconnected()
            : ToolReports.FromInteractionList(snapshot);
    }

    [McpServerTool(Name = "wait_for")]
    [Description(
        "Waits until the runtime is idle or a target appears or disappears, up to "
        + "a bounded timeout. A timeout answers satisfied=false as a normal result.")]
    public async Task<string> WaitFor(
        [Description("One of \"idle\", \"target_present\", \"target_absent\".")] string condition,
        [Description("The target ID for the target_* conditions; omit for idle.")]
        string? targetId = null,
        [Description("Timeout in milliseconds, 1..30000. Default 5000.")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        if (!bridge.IsConnected)
        {
            return ToolReports.Disconnected();
        }

        var result = await bridge.WaitForAsync(condition, targetId, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        return ToolReports.FromWaitResult(condition, result);
    }

    [McpServerTool(Name = "start_recording")]
    [Description(
        "Starts recording interactions into a new session. Returns the recording "
        + "handle and the new session epoch; the runtime is recreated, so any "
        + "in-flight interaction ends as outcome_unknown.")]
    public async Task<string> StartRecording(
        [Description("Optional human label stored with the recording for bookkeeping.")]
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        var report = await bridge.StartRecordingAsync(label, cancellationToken)
            .ConfigureAwait(false);
        return ToolReports.FromOperationReport(report);
    }

    [McpServerTool(Name = "stop_recording")]
    [Description(
        "Stops the active recording and returns its handle and captured entry "
        + "count. The runtime is recreated into a fresh non-recording session.")]
    public async Task<string> StopRecording(CancellationToken cancellationToken = default)
    {
        var report = await bridge.StopRecordingAsync(cancellationToken).ConfigureAwait(false);
        return ToolReports.FromOperationReport(report);
    }

    [McpServerTool(Name = "replay_recording")]
    [Description(
        "Replays a stored recording by handle and returns the sanitized strict-"
        + "replay outcome (completed, diverged, or stopped). The runtime is left "
        + "in a fresh live session afterward.")]
    public async Task<string> ReplayRecording(
        [Description("The recording handle from start_recording / stop_recording.")]
        string recordingHandle,
        CancellationToken cancellationToken = default)
    {
        var report = await bridge.ReplayRecordingAsync(recordingHandle, cancellationToken)
            .ConfigureAwait(false);
        return ToolReports.FromOperationReport(report);
    }
}
