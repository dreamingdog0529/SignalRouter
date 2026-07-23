# Developing SignalRouter

> **Current status:** The command model, immutable command catalog and codecs, structured
> result model, semantic registry, FIFO dispatch, deterministic stage execution, hash-level
> state probes, semantic-UI property-level diffs (matched-target scalar fields, per-field
> target add/remove, and nested interaction/argument changes), the JSON Lines recorder
> with its recovery-capable reader, the strict replayer with its structured divergence
> reports, the uGUI integration (interaction runtime, button/text-input adapters,
> editor scene validator, BasicUi sample scene, and the PlayMode suite), and the
> versioned runtime protocol contract (envelope model and codecs, handshake negotiation,
> connection phase enforcement, and the bounded request ledger — ADR 0007), the Core
> split-phase submission API with cancellation by external request ID, the WebSocket
> transport with the Unity runtime bridge (channel framing, reconnect loop, and
> query-first result recovery, verified end-to-end over a live loopback socket in
> PlayMode), and the MCP host process (`SignalRouter.McpHost`: stdio MCP tools over a
> Kestrel loopback WebSocket endpoint — execute_interaction, get_interaction_result,
> get_ui_tree, list_interactions, wait_for) are implemented and verified. The
> recording and replay tools (start_recording, stop_recording, replay_recording)
> remain unimplemented.

## Prerequisites

- Unity 6000.5.4f1 installed at the platform's standard Unity Hub path
- .NET SDK 10.0.302
- PowerShell 7
- [Task](https://taskfile.dev/)
- [typos](https://github.com/crate-ci/typos) for the full check
- Git

`global.json` disables SDK roll-forward so an absent 10.0.302 SDK fails immediately.
Unity version resolution reads `src/SignalRouter.Unity/ProjectSettings/ProjectVersion.txt`
and also fails if that exact Editor is absent.

## Build and test

Use the repository wrappers:

```bash
task build
task test
task check
```

`task build` performs these checks:

1. Restore the pinned NuGetForUnity CLI tool.
2. Build and pack `SignalRouter.slnx`, publishing the `SignalRouter.Core` and
   `SignalRouter.Protocol` packages to the shared local feed (`../.local-feed`).
3. Evict cached copies of the SignalRouter packages (the feed repacks a fixed
   version, so a cached package would silently pin Unity to a stale SDK build).
4. Restore the packages into the Unity project with NuGetForUnity.
5. Compile the Unity development project in batch mode and confirm NuGetForUnity restored
   `SignalRouter.Core.dll` and `SignalRouter.Protocol.dll`.

`task test` runs the NUnit projects on `net10.0`, then runs the Unity EditMode and
PlayMode suites in separate batch launches. Each Unity result XML must exist, discover at
least one test, pass every test, and report no skipped or inconclusive cases.

`task check` adds spelling, Conventional Commit, and DCO checks before build and test.
Build logs and Unity result XML are written below `.artifacts/`.

## Running the MCP host

```bash
dotnet run --project src/SignalRouter.McpHost
```

The host speaks MCP over stdio (register the command above in an MCP client) and
listens for the Unity runtime on `ws://127.0.0.1:8017/` (loopback only; override the
port with `SIGNALROUTER_PORT`). On the Unity side, add an `InteractionRuntimeBridge`
component next to the `InteractionRuntime` and point its endpoint at the same port;
the bridge reconnects automatically across domain reloads. All host logging goes to
stderr because stdout carries the MCP transport.

## Compatibility boundary

The distributable Core and Protocol sources live under:

- `src/SignalRouter.Core`
- `src/SignalRouter.Protocol`

These are standalone SDK-style projects that build as `netstandard2.1` with `LangVersion`
set to `9.0` and warnings treated as errors, and pack to the shared local feed. The Unity
project consumes the resulting `SignalRouter.Core` and `SignalRouter.Protocol` NuGet
packages through NuGetForUnity; it does not recompile their source. Keeping the shipped
assemblies at C# 9 means package consumers — Unity's Mono/IL2CPP toolchain among them — do
not need preview language features. Core and Protocol code therefore must not use
`record struct`, `required`, or PolySharp-generated types. Commands use ordinary
`readonly struct` types with explicit value equality. Immutable result, schema, and
descriptor classes use defensive copies and structural equality.

The Unity development project separately sets the Standalone Player additional compiler
argument to `-langversion:preview`. [Unity 6 officially supports C# 9][unity-csharp]; this
override is outside the official support boundary and enables the preview understood by
the Editor's bundled compiler, not an exact C# 11 ceiling. The EditMode test compiles and
runs `record struct` and `required` constructs to verify the C# 11 features this project
uses.

One hard limit of that override: types Unity itself must associate with their `.cs`
assets — MonoBehaviours and other Unity-serialized types — MUST be declared in braced
namespaces. Unity's script-to-asset association does not understand file-scoped
namespaces even though Roslyn compiles them; an affected component saves into scenes as
an embedded transient MonoScript stub and loads back as a missing script. The sample
scene generator fails fast if any component it saves resolves to a transient MonoScript.
Test-only classes that never serialize into assets may use file-scoped namespaces.

[PolySharp 1.16.0][polysharp] is restored into the ignored `Assets/Packages` directory
before Unity starts. `Assets/Default.globalconfig` disables PolySharp's embedded marker
because that marker name is reserved by the compiler used with preview mode. This
preserves the required-member and init-only polyfills without masking compilation
failures. Unity documents project-wide analyzer configuration through
[`Default.globalconfig`][unity-globalconfig].

NuGetForUnity restores `SignalRouter.Core`, `SignalRouter.Protocol`, VitalRouter, and
System.Text.Json into the ignored `Assets/Packages` directory. The EditMode test asmdef
sets `overrideReferences: true` and names every assembly it needs as a precompiled
reference — `SignalRouter.Core.dll`, `SignalRouter.Protocol.dll`, `System.Text.Json.dll`,
`VitalRouter.dll`, and `nunit.framework.dll` — so it compiles against the restored
packages. See Unity's [assembly reference rules][unity-assembly-references].

`CompilerSettings.props` sets `LangVersion` to `11.0` in Unity-generated IDE projects.
CsprojModifier imports it into every generated `.csproj`; this affects IDE analysis only.
Unity's actual compilation language is controlled by Player Settings.

## Pinned dependencies

| Dependency | Version | Purpose |
|------------|---------|---------|
| [.NET SDK][dotnet-sdk] | 10.0.302 | SDK projects and NUnit tests |
| [NUnit][nunit] | 4.6.1 | Pure C# tests |
| [NUnit3TestAdapter][nunit-adapter] | 6.2.0 | Test discovery |
| [Microsoft.NET.Test.Sdk][test-sdk] | 18.8.1 | `dotnet test` host |
| [System.Text.Json][system-text-json] | 8.0.6 | Explicit command schemas and codecs (pinned to the line Unity 6000.5 bundles) |
| [VitalRouter][vitalrouter] | 2.8.0 | Public command marker contract |
| [NuGetForUnity / CLI][nuget-for-unity] | 4.5.0 | Restore NuGet assets before Unity compilation |
| [PolySharp][polysharp] | 1.16.0 | Unity development-project language polyfills |
| [CsprojModifier][csproj-modifier] | 1.3.0 | IDE project import customization |

## Git hooks

Install local hooks for Conventional Commits and DCO sign-off:

```powershell
pwsh ./scripts/install-git-hooks.ps1
# or
task setup
```

Use `git commit -s` and one logical Conventional Commit per change. See
[CONTRIBUTING.md](../.github/CONTRIBUTING.md) for the full workflow.

## Releases

Releases remain automated with
[Release Please](https://github.com/googleapis/release-please):

1. Merge Conventional Commits to the default branch.
2. Release Please opens or updates the release PR.
3. Merge the release PR to create the tag and GitHub Release.

Do not edit `CHANGELOG.md`, `version.txt`, or release tags during normal development.
The 0.1.0 foundation initialization was an explicit one-time exception for the version
manifest files.

[csproj-modifier]: https://github.com/Cysharp/CsprojModifier/releases/tag/1.3.0
[dotnet-sdk]: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
[nuget-for-unity]: https://github.com/GlitchEnzo/NuGetForUnity/releases/tag/v4.5.0
[nunit-adapter]: https://www.nuget.org/packages/NUnit3TestAdapter/6.2.0
[nunit]: https://www.nuget.org/packages/NUnit/4.6.1
[polysharp]: https://www.nuget.org/packages/PolySharp/1.16.0
[test-sdk]: https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.8.1
[system-text-json]: https://www.nuget.org/packages/System.Text.Json/8.0.6
[unity-assembly-references]: https://docs.unity3d.com/6000.0/Documentation/Manual/assembly-definitions-referencing.html
[unity-csharp]: https://docs.unity3d.com/6000.0/Documentation/Manual/csharp-compiler.html
[unity-globalconfig]: https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Compilation.ScriptCompilerOptions.AnalyzerConfigPath.html
[vitalrouter]: https://www.nuget.org/packages/VitalRouter/2.8.0
