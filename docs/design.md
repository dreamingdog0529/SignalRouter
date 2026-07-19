# SignalRouter Architecture and Design

> **Status:** Accepted for MVP implementation  
> **Document version:** 1.0  
> **Last updated:** July 19, 2026  
> **Initial compatibility target:** Unity 6, Windows Editor, Mono, uGUI  
> **Pure C# target:** .NET Standard 2.1  
> **Command bus:** VitalRouter 2.8.0

## 1. Purpose

SignalRouter is a command-driven interaction runtime for Unity. It represents user
interface operations as structured commands and routes input from people, automated
tests, replay sessions, and MCP agents through the same application code path.

The project is designed to provide three capabilities:

1. A semantic UI tree that describes what can be observed and operated at runtime.
2. Deterministic recording and replay of application-level interactions.
3. Precise failure reporting for partially completed operations, including the stage
   that failed and the stages that completed before it.

SignalRouter is a standalone implementation. It does not require or wrap another UI
automation framework.

This document defines the architecture approved for implementation. Features described
as part of the MVP are design commitments, not claims about the current implementation.
The repository remains in the design phase until the corresponding acceptance tests pass.

## 2. Design commitments

The following commitments are normative for the MVP:

- **One execution boundary:** every interaction MUST pass through
  `IInteractionDispatcher`, regardless of its origin.
- **Data-only commands:** commands MUST NOT contain callbacks, tasks, Unity objects, or
  transport-specific metadata.
- **Validation before side effects:** invalid interactions MUST be rejected before any
  stage begins.
- **Global ordering:** accepted requests MUST enter one FIFO execution queue.
- **Deterministic stages:** stages MUST run sequentially in an explicit, stable order.
- **Fail-fast execution:** the first stage exception MUST stop all later stages.
- **Structured outcomes:** exceptions MUST be converted into `InteractionResult` before
  crossing the runtime, WebSocket, or MCP boundary.
- **Verified replay:** replay MUST compare outcomes and state observations; queue
  acceptance alone is not success.
- **Explicit exposure:** commands, targets, and state probes MUST be explicitly registered
  before they can be accessed through MCP.

These rules are intended to keep human input, agent input, tests, and replay behavior
equivalent and auditable.

## 3. Scope and compatibility

### 3.1 MVP support

| Area | Initial support |
|---|---|
| Unity | Unity 6; development project uses 6000.5.4f1 |
| Runtime | Windows Editor, Mono |
| UI framework | uGUI |
| Controls | Button and text input |
| Commands | `ClickCommand`, `SetValueCommand` |
| Target identity | Explicit stable IDs |
| Execution | Global FIFO; sequential, fail-fast stages |
| Recording | Append-only JSON Lines |
| Replay | Strict, sequential verification |
| Agent access | External MCP host over a loopback WebSocket |
| Testing | Pure C# unit tests and Unity EditMode/PlayMode tests |

### 3.2 Explicitly outside the MVP

- UI Toolkit and TextMeshPro integration
- Toggle, slider, dropdown, scrolling, focus, and keyboard commands
- IL2CPP and standalone Player builds
- Platforms other than Windows
- Coordinate-based fallback
- Screenshot recognition and visual comparison
- Parallel stages
- Automatic rollback after partial failure
- Automatic conversion of existing UnityEvents or custom input systems
- Remote network access

Unsupported items are not necessarily rejected long-term. They are excluded from the
initial compatibility contract so that the MVP can be tested against a narrow, reliable
surface.

## 4. Terminology and public naming

`UiCommand` is not used in the public API. The approved vocabulary is:

| Name | Responsibility |
|---|---|
| `IInteractionCommand` | Marker contract for all domain interaction commands |
| `ClickCommand` | Activates a target |
| `SetValueCommand` | Replaces the value of a target |
| `IInteractionDispatcher` | The only public execution entry point |
| `InteractionResult` | The structured terminal outcome |
| `InteractionStatus` | `Succeeded`, `Rejected`, `Faulted`, or `Cancelled` |
| `IInteractionTarget` | A registered semantic interaction target |
| `InteractionDescriptor` | Observable target state and available operations |
| `IInteractionStage<TCommand>` | One ordered side-effect step |
| `InteractionOrigin` | `Human`, `Agent`, `Replay`, or `Test` |
| `InteractionRequest` | A transport DTO; not a domain command |

Concrete command names remain concise. For example, `ClickCommand` is preferred over
`ClickInteractionCommand`; the `Interaction` prefix is reserved for shared contracts and
runtime services.

## 5. System architecture

```text
Human uGUI ─┐
Tests ──────┼──> IInteractionDispatcher ──> FIFO ──> VitalRouter ──> Target pipeline
Replay ─────┤             │                   │                           │
MCP ────────┘             ├── Registry        │                    stage 10: state
                          ├── Validation      │                    stage 20: presentation
                          ├── Recorder        │                    stage 30: sound
                          ├── State probes    │                           │
                          └── Result boundary <───────────────────────────┘

MCP client <stdio> SignalRouter.McpHost <loopback WebSocket> Unity runtime
```

### 5.1 Component responsibilities

| Component | Responsibility |
|---|---|
| Command catalog | Registers wire names, schema versions, codecs, and exposure policy |
| Semantic registry | Tracks targets, stable IDs, descriptors, revisions, and lifetimes |
| Dispatcher | Queues, validates, sequences, snapshots, publishes, records, and returns results |
| VitalRouter adapter | Owns a private router and routes concrete commands to target pipelines |
| Stage pipeline | Executes explicit stages in order and tracks partial progress |
| State probe registry | Captures canonical state snapshots, hashes, and selected differences |
| Recorder | Persists commands and terminal outcomes |
| Replayer | Re-executes records and detects divergence |
| uGUI adapter | Converts real input into commands and maintains semantic descriptors |
| Runtime bridge | Implements the authenticated WebSocket protocol and main-thread handoff |
| MCP host | Maps MCP tools to the runtime protocol |

No transport component may bypass the dispatcher.

The production router is private to the dispatcher. Applications receive
`IInteractionDispatcher`, not the underlying `ICommandPublisher`. This prevents a direct
VitalRouter publish from bypassing validation, recording, ordering, or result handling.

## 6. Command model

Commands are immutable values with no execution behavior:

```csharp
public interface IInteractionCommand : VitalRouter.ICommand
{
    string TargetId { get; }
}

public readonly struct ClickCommand :
    IInteractionCommand,
    IEquatable<ClickCommand>
{
    public ClickCommand(string targetId) => TargetId = targetId;

    public string TargetId { get; }

    public bool Equals(ClickCommand other) =>
        string.Equals(TargetId, other.TargetId, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is ClickCommand other && Equals(other);

    public override int GetHashCode() =>
        TargetId is null ? 0 : StringComparer.Ordinal.GetHashCode(TargetId);
}

public readonly struct SetValueCommand :
    IInteractionCommand,
    IEquatable<SetValueCommand>
{
    public SetValueCommand(string targetId, string value)
    {
        TargetId = targetId;
        Value = value;
    }

    public string TargetId { get; }

    public string Value { get; }

    public bool Equals(SetValueCommand other) =>
        string.Equals(TargetId, other.TargetId, StringComparison.Ordinal) &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is SetValueCommand other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            TargetId is null ? 0 : StringComparer.Ordinal.GetHashCode(TargetId),
            Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value));
}
```

The following data does not belong in a command:

- sequence numbers or request IDs;
- timestamps or durations;
- origin and authorization metadata;
- cancellation tokens;
- recorder state;
- result callbacks or `TaskCompletionSource`;
- Unity object references.

Keeping commands as pure data makes a human-generated command directly comparable with
the corresponding agent, test, or replay command.

### 6.1 Command catalog

Every command type is registered before use:

```csharp
catalog.Register<ClickCommand>(
    wireName: "click",
    version: 1,
    schema: ClickCommandSchema.Instance,
    agentVisible: true);
```

The persistent command identity is the pair `wireName + version`. Assembly-qualified
.NET type names MUST NOT be written to recordings or the wire protocol.

A version retains its original meaning permanently. A breaking field or semantic change
requires a new command schema version and an explicit migration strategy.

## 7. Execution API

```csharp
public interface IInteractionDispatcher
{
    ValueTask<InteractionResult> DispatchAsync<TCommand>(
        TCommand command,
        InteractionDispatchOptions options,
        CancellationToken cancellationToken = default)
        where TCommand : struct, IInteractionCommand;
}

public readonly struct InteractionDispatchOptions :
    IEquatable<InteractionDispatchOptions>
{
    public InteractionDispatchOptions(
        InteractionOrigin origin,
        string? correlationId = null,
        string? idempotencyKey = null)
    {
        Origin = origin;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
    }

    public InteractionOrigin Origin { get; }

    public string? CorrelationId { get; }

    public string? IdempotencyKey { get; }

    public bool Equals(InteractionDispatchOptions other) =>
        Origin == other.Origin &&
        string.Equals(CorrelationId, other.CorrelationId, StringComparison.Ordinal) &&
        string.Equals(IdempotencyKey, other.IdempotencyKey, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is InteractionDispatchOptions other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Origin, CorrelationId, IdempotencyKey);
}
```

Transport requests are decoded through the command catalog into concrete command values.
The catalog invokes a precompiled generic dispatch delegate so that dynamic MCP requests
and direct C# calls use the same execution implementation.

### 7.1 Dispatch algorithm

For each request, the dispatcher performs the following steps:

1. Verify command registration and decode/validate arguments.
2. Enqueue the request and assign its sequence number.
3. Append the recording request event, when recording is active.
4. On dequeue, establish the single active `InteractionExecutionScope`.
5. Capture the before-state observation.
6. Resolve the target and validate its current descriptor and command pipeline.
7. Publish the concrete command through the private VitalRouter instance.
8. Capture stage progress, any exception, and the after-state observation.
9. Construct the terminal `InteractionResult`.
10. Append the terminal recording event and release the execution scope.

The active scope is an internal runtime service, not static global state. The single FIFO
guarantees that at most one scope is active. The VitalRouter subscriber reads that scope
to resolve the already-authorized target pipeline and update stage progress.

The subscriber MUST reject a missing or mismatched scope as a SignalRouter invariant
violation. It MUST NOT perform a second, independent dispatch.

### 7.2 Request identity

- `RequestId` identifies one submitted request across process boundaries.
- `Sequence` is assigned when the request enters the local FIFO.
- `CorrelationId` groups related requests but does not affect ordering.
- `IdempotencyKey`, when provided, prevents accidental duplicate submission within a
  bounded runtime cache.

Request identity is runtime metadata and does not alter command equality.

## 8. Execution lifecycle

```text
Queued
  ├── cancelled before start ─────────────> Cancelled (no side effects)
  └── Dequeued
       ├── validation failed ─────────────> Rejected  (no side effects)
       └── Started
            ├── every stage completed ───> Succeeded
            ├── stage threw ─────────────> Faulted   (partial effects possible)
            └── cancellation observed ──> Cancelled (partial effects possible)
```

### 8.1 Status definitions

| Status | Definition |
|---|---|
| `Succeeded` | Every registered stage completed |
| `Rejected` | Validation failed before the first stage began |
| `Faulted` | A stage began and execution stopped because of an exception |
| `Cancelled` | Cancellation was observed before or during execution |

`Rejected` guarantees zero stage side effects. `Faulted` and an in-progress
`Cancelled` result may contain partial side effects and therefore always include stage
progress and before/after state observations.

Timeout is not a core interaction status. A caller implements a timeout by cancelling its
token and waiting for a terminal runtime result. If the transport disconnects before a
terminal result can be confirmed, the MCP host reports `OutcomeUnknown` as a transport
error; it MUST NOT invent an interaction outcome.

## 9. Bus selection and ownership

VitalRouter is the only production command bus for the MVP. SignalRouter will not provide
a MessagePipe/VitalRouter compile-time selection layer.

### 9.1 Why VitalRouter

[VitalRouter](https://github.com/hadashiA/VitalRouter) was selected because its model
aligns with the interaction boundary:

- commands implement a small `ICommand` contract;
- immutable struct commands are supported;
- asynchronous routes and interceptors are first-class;
- `PublishContext` carries cancellation and execution-scoped metadata;
- Unity and .NET Standard 2.1 targets are available;
- source-generated routing is suitable for Unity;
- the router can apply sequential handling to overlapping publishes.

The initial implementation pins VitalRouter 2.8.0. Dependency upgrades require the
SignalRouter test suite to pass before the supported version changes.

### 9.2 Responsibilities retained by SignalRouter

VitalRouter routes commands and awaits handlers. SignalRouter retains ownership of:

- validation and rejection semantics;
- global sequence assignment;
- FIFO request ordering;
- terminal result construction;
- recording;
- state observation;
- stage ordering and progress;
- exception normalization;
- replay verification.

VitalRouter does not return a domain response value. This is intentional: the dispatcher
constructs `InteractionResult` from the execution context, stage progress, captured
exception, and before/after state.

The router's subscriber registration order MUST NOT define application stage order.
The router instance and its publisher interface remain internal so callers cannot bypass
the dispatcher.

## 10. Deterministic stage pipeline

An interaction target executes one or more explicitly registered stages:

```csharp
public interface IInteractionStage<TCommand>
    where TCommand : struct, IInteractionCommand
{
    string Id { get; }
    int Order { get; }

    ValueTask ExecuteAsync(
        TCommand command,
        InteractionContext context,
        CancellationToken cancellationToken);
}
```

### 10.1 Stage rules

- `Id` MUST be stable and unique within a command pipeline.
- `Order` MUST be unique within a command pipeline.
- Duplicate IDs or orders MUST fail during startup validation.
- Stages execute in ascending `Order`.
- The tracker records a stage immediately before invocation and after successful return.
- The first exception or observed cancellation stops every later stage.
- Rollback is not attempted in the MVP.

Example stage IDs:

```text
click.apply-state
click.transition
click.sound
```

Presentation and sound stages are not silently ignored. If either throws, the interaction
is `Faulted`. Introducing non-critical stages later would change outcome semantics and
therefore requires a separate architecture decision and recording schema revision.

## 11. Global ordering and reentrancy

Requests from every origin share one FIFO. Sequence numbers are assigned at enqueue time,
which provides a total order for diagnostics and recording.

VitalRouter's sequential ordering is used as a secondary safety mechanism. The
SignalRouter FIFO remains the authority for request ordering and sequence assignment.

Awaiting a nested `DispatchAsync` call from an active stage can deadlock a single-consumer
queue and obscure replay order. The MVP rejects such calls with `ReentrantDispatch`.

Stages that need a follow-up interaction use
`InteractionContext.EnqueueContinuation`. A continuation receives a new request ID and
sequence only after the current interaction reaches a terminal state.

## 12. Structured results

```csharp
public sealed record InteractionResult(
    long Sequence,
    string RequestId,
    string TargetId,
    string CommandName,
    int CommandVersion,
    InteractionOrigin Origin,
    InteractionStatus Status,
    RejectionInfo? Rejection,
    FaultInfo? Fault,
    StageProgress Stages,
    StateObservation Before,
    StateObservation After,
    StateDiff Diff);
```

### 12.1 Standard rejection codes

- `TargetNotFound`
- `DuplicateTargetId`
- `NotVisible`
- `Disabled`
- `OperationNotAvailable`
- `InvalidArguments`
- `CommandNotRegistered`
- `ReentrantDispatch`
- `ReleaseBuildDisabled`

For a rejected interaction, before and after state hashes MUST match. A mismatch is a
SignalRouter invariant violation and is reported separately from the application
rejection.

### 12.2 Fault information

`FaultInfo` records:

- exception type;
- message;
- stack trace;
- a stable application fault code, when supplied;
- failed stage ID and index;
- completed stage IDs.

Replay does not compare stack traces byte-for-byte because compiler and build changes can
alter them. Strict replay compares status, stable fault code, failed stage ID, completed
stage IDs, and state hashes.

Sensitive values MUST be redacted before fault information is logged, recorded, or
returned over MCP.

## 13. Semantic UI registry

### 13.1 Stable target IDs

MVP target IDs are explicitly assigned by the application developer.

- An ID MUST be unique within one runtime session.
- Hierarchy paths, labels, and sibling indexes MUST NOT be persistent fallbacks.
- A logically identical target SHOULD retain its ID when its GameObject is recreated.
- Duplicate IDs are detected by both Editor validation and runtime registration.
- A duplicate target is never resolved by "first match."

This avoids recordings that silently control the wrong element after a hierarchy or
localization change.

### 13.2 Target contract

```csharp
public interface IInteractionTarget
{
    string Id { get; }

    InteractionDescriptor Describe();

    bool TryGetPipeline<TCommand>(
        out IInteractionPipeline<TCommand> pipeline)
        where TCommand : struct, IInteractionCommand;
}

public interface IInteractionPipeline<TCommand>
    where TCommand : struct, IInteractionCommand
{
    InteractionValidation Validate(in TCommand command);

    ValueTask ExecuteAsync(
        TCommand command,
        InteractionContext context,
        CancellationToken cancellationToken);
}
```

The registry resolves one target by stable ID. The target then resolves one typed
pipeline for the concrete command. A missing pipeline is rejected as
`OperationNotAvailable`; it is not treated as a successful no-op.

The typed pipeline owns its ordered `IInteractionStage<TCommand>` collection. Validation
MUST NOT mutate target or application state.

### 13.3 Descriptors

```csharp
public sealed record InteractionDescriptor(
    string Id,
    string? ParentId,
    string Role,
    string Label,
    InteractionValue? Value,
    bool Visible,
    bool Enabled,
    IReadOnlyList<AvailableInteraction> AvailableInteractions);
```

Initial roles:

- `button`
- `textbox`

Each available interaction includes its wire name, schema version, argument schema, and
sensitive-field metadata. The semantic tree is the authoritative answer to "what can be
done on the current screen?"

The registry maintains:

- a monotonically increasing `Revision` when observable state changes;
- a `SessionEpoch` that changes when the Unity runtime is recreated or reconnects.

Clients MUST treat a changed session epoch as a new runtime session, even if revision
numbers or target IDs overlap.

## 14. State observation

The semantic tree cannot represent every application side effect. SignalRouter therefore
supports state probes:

```csharp
public interface IInteractionStateProbe
{
    string Id { get; }
    int Version { get; }
    StateProbeSnapshot Capture();
}
```

Built-in probes:

- `semantic-ui`: the relevant semantic UI scope;
- `interaction-runtime`: session epoch, registry revision, and queue state.

Applications may register probes for inventory, navigation, scene state, or other domain
data. Agent access to probe contents is opt-in.

Snapshots use canonical JSON and SHA-256 hashes. Redaction occurs before canonicalization
and hashing so that secret values do not enter recordings indirectly.

The MVP state diff contains:

- before and after hashes for each probe;
- property-level changes for semantic UI descriptors.

Generic JSON Patch output is deferred until a stable canonicalization and size policy has
been proven.

## 15. Recording

Recordings use append-only JSON Lines. The first line is a session header. Each submitted
interaction produces a request event before execution and, when known, one terminal event:

```json
{"kind":"session","schemaVersion":1,"sessionId":"...","appBuild":"...","startedAt":"..."}
{"kind":"interaction_requested","sequence":12,"requestId":"...","origin":"Agent",
 "command":{"name":"click","version":1,"targetId":"menu.start","arguments":{}}}
{"kind":"interaction_completed","sequence":12,"requestId":"...",
 "result":{"status":"Faulted","failedStageId":"click.sound",
   "completedStageIds":["click.apply-state","click.transition"],
   "faultCode":"AudioDeviceUnavailable"},
 "state":{"beforeHash":"...","afterHash":"..."}}
```

JSON Lines was selected so a process interruption can be recovered by discarding only an
incomplete final line.

### 15.1 Recording guarantees

- Commands are recorded in sequence order.
- A request event is durable before its first stage begins.
- A known outcome is written as exactly one terminal event with the same request ID and
  sequence.
- Sensitive command fields are replaced with a secret key; plaintext is never persisted.
- Recording schema versions are independent of command schema versions.
- Readers reject unsupported schema versions rather than guessing.
- File paths are constrained to the configured artifact root.

If the process terminates while an interaction is queued or running, the recording
contains a request event without a terminal event. Readers mark that request
`OutcomeUnknown`; they do not convert it to `Faulted`. Strict replay stops before an
outcome-unknown entry unless the caller supplies an explicit recovery policy.

## 16. Replay

Replay executes one command at a time through the normal dispatcher.

### 16.1 Strict replay

Strict mode is the MVP default. For every recorded entry it verifies:

1. the command name and version exist in the catalog;
2. required secrets can be resolved in memory;
3. the recorded before-state hash matches the current state;
4. the dispatcher returns a terminal result;
5. the status matches;
6. faulted results match fault code and stage progress;
7. the recorded after-state hash matches the resulting state.

Replay stops at the first divergence and returns a structured report containing the
recorded expectation, actual outcome, and state differences.

`Rejected` entries are dispatched again so the same rejection code and zero-side-effect
guarantee can be verified. `Faulted` entries run through the same stages and must fail at
the recorded stage with the recorded partial state.

### 16.2 Adaptive replay

Condition-based timing and tolerance for selected state differences are intentionally
deferred. The MVP supports strict replay and explicit `wait_for` steps only.

## 17. uGUI integration

Initial runtime components:

- `InteractionRuntime`
- `InteractionScope`
- `InteractionButton`
- `InteractionTextInput`

### 17.1 Human input

`InteractionButton` converts a uGUI `Button.onClick` notification into a
`ClickCommand`. Application side effects belong in registered stages, not in additional
UnityEvent listeners.

Persistent listeners that bypass the command boundary cause the Editor validator to fail.
Runtime-added listeners cannot always be attributed reliably, so the public integration
guidance prohibits direct application-side `Button.onClick.AddListener` usage on managed
targets.

`InteractionTextInput` converts committed human edits into `SetValueCommand`. A
suppression scope prevents agent or replay updates from recursively generating new human
commands.

Whether text changes dispatch per edit or on edit completion remains an implementation
choice that MUST be settled before the text-input acceptance tests are written.

### 17.2 Main-thread policy

The semantic registry, uGUI objects, and application stages are accessed only on the
Unity main thread.

The WebSocket receiver may parse and validate transport envelopes on a background thread,
but it only places accepted requests into a thread-safe handoff queue.
`InteractionRuntime.Update` transfers those requests to the dispatcher on the main
thread.

## 18. MCP and runtime protocol

### 18.1 Process boundary

The MCP server runs in a separate .NET process named `SignalRouter.McpHost`. Unity does
not host the MCP SDK.

The MCP host listens on a loopback WebSocket endpoint. The Unity runtime connects as a
client. This direction allows Unity to reconnect after a domain reload without requiring
the MCP client process to restart.

### 18.2 Initial MCP tools

- `get_ui_tree`
- `list_interactions`
- `execute_interaction`
- `wait_for`
- `start_recording`
- `stop_recording`
- `replay_recording`
- `get_interaction_result`

`execute_interaction` returns a terminal `InteractionResult`, not queue acceptance.
If a client-side timeout occurs, the caller can query the request ID with
`get_interaction_result`.

### 18.3 Protocol envelope

Every protocol message includes:

- protocol version;
- message ID;
- request ID, when applicable;
- session epoch;
- message type;
- typed payload.

The initial handshake exchanges protocol version, runtime version, capabilities, and
maximum payload size. Incompatible major protocol versions fail the handshake with an
explicit error.

Unknown fields follow the schema's forward-compatibility policy; unknown message types are
never executed.

## 19. Security model

External interaction control is a privileged capability. The MVP applies the following
defaults:

- WebSocket endpoints bind only to `127.0.0.1` and `::1`.
- Each runtime launch uses a cryptographically random 256-bit authentication token.
- Token comparison is timing-safe.
- The bridge and MCP control surface are disabled by default in release builds.
- Commands, targets, and state probes require explicit agent-visible registration.
- The protocol does not accept arbitrary .NET type names, reflection calls, C# code, or
  unrestricted filesystem paths.
- Payload size, tree size, recording size, pending requests, and history length are
  bounded.
- Artifact paths are normalized and required to remain under one configured root.
- Sensitive fields are redacted from recordings, logs, faults, and MCP responses.
- Authentication failures and policy rejections are recorded without echoing credentials.

Remote binding is not an MVP configuration option.

## 20. Source and package layout

The proposed repository layout keeps one source of truth for code compiled by both Unity
and SDK-style .NET projects:

```text
src/
  SignalRouter/                         # UPM package root
    package.json
    Runtime/
      Core/                             # No Unity dependency
      VitalRouter/
      Protocol/                         # No Unity dependency
      Unity/
    Editor/
    Tests/
    Samples~/
  SignalRouter.Core/
    SignalRouter.Core.csproj
  SignalRouter.Protocol/
    SignalRouter.Protocol.csproj
  SignalRouter.McpHost/
    SignalRouter.McpHost.csproj
  SignalRouter.Unity/                   # Development and integration-test project
tests/
  SignalRouter.Core.Tests/
  SignalRouter.Protocol.Tests/
```

SDK-style projects compile the same files under `Runtime/Core` and `Runtime/Protocol`.
Pure C# code is not copied into a second implementation tree.

Planned assemblies:

- `SignalRouter.Core`
- `SignalRouter.VitalRouter`
- `SignalRouter.Protocol`
- `SignalRouter.Unity`
- `SignalRouter.Editor`
- `SignalRouter.Tests`

## 21. Verification strategy

### 21.1 Pure C# tests

- command codec round trips;
- concurrent enqueue and FIFO execution;
- rejection with zero side effects;
- explicit stage ordering and fail-fast behavior;
- cancellation before and during execution;
- reentrant-dispatch rejection and continuation ordering;
- recording recovery after a truncated final line or an unpaired request event;
- strict replay success and divergence;
- secret redaction;
- canonical state hashing.

### 21.2 Unity EditMode tests

- duplicate stable-ID detection;
- descriptor generation;
- command and target registration;
- invalid UnityEvent listener detection.

### 21.3 Unity PlayMode tests

- human click to `ClickCommand` to registered stages;
- an agent-equivalent request using the identical command path;
- replay using the identical command path;
- deterministic failure at a known stage;
- main-thread enforcement;
- session-epoch changes after runtime recreation;
- WebSocket reconnect behavior.

## 22. MVP acceptance criteria

The MVP is complete only when all of the following are demonstrated in automated tests:

1. A real uGUI click and an MCP-originated click produce equivalent `ClickCommand`
   execution.
2. Concurrent submissions are assigned and executed in one observable FIFO order.
3. Invalid targets are rejected without running any stage.
4. A three-stage interaction that fails in stage two records stage one as completed,
   stage two as failed, and stage three as not started.
5. Strict replay reproduces both a successful interaction and the same stage-two failure.
6. A replay state mismatch stops execution and reports a structured divergence.
7. Secret text is absent from recordings, logs, faults, and MCP output.
8. Unity API access occurs on the main thread.
9. An unauthenticated or non-loopback control attempt cannot execute an interaction.
10. The Pure C# build and Unity EditMode/PlayMode suites pass in CI.

## 23. Implementation order

1. UPM package, assembly definitions, and Pure C# test projects
2. Command model, command catalog, result model, and semantic registry
3. FIFO dispatcher and VitalRouter integration
4. Stage pipeline, progress tracker, and state probes
5. Recorder and strict replayer
6. uGUI button/text adapters and sample scene
7. Versioned runtime WebSocket protocol
8. External MCP host and tool surface
9. Security limits, authentication, and redaction
10. Package build, CI, and user documentation

## 24. Accepted architecture decisions

| ID | Decision |
|---|---|
| D1 | SignalRouter is a standalone implementation |
| D2 | Human, agent, test, and replay input share `IInteractionDispatcher` |
| D3 | Public command naming uses `IInteractionCommand` and concise concrete commands |
| D4 | VitalRouter is the single production command bus |
| D5 | SignalRouter owns results and deterministic stages; the bus owns routing |
| D6 | Requests use a global FIFO; stages are sequential and fail-fast |
| D7 | The MVP targets Unity 6, Windows Editor, Mono, and uGUI |
| D8 | Persistent target identity requires an explicit stable ID |
| D9 | MCP runs externally; Unity connects as a loopback WebSocket client |
| D10 | Replay verifies terminal outcomes and state hashes |

## 25. Remaining implementation-level decisions

These items do not change the accepted architecture, but they must be resolved and tested
before their respective components are considered stable:

- canonical JSON and serializer implementation;
- the MCP host target .NET version;
- text-input dispatch timing;
- default artifact-root location;
- state-snapshot size limits;
- retention limits for idempotency and completed-result caches.

Decisions that change public compatibility, failure semantics, persistent schemas, or the
security boundary require an Architecture Decision Record and a corresponding test-plan
update.
