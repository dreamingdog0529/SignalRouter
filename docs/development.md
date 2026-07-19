# Developing SignalRouter

> **Current status:** The command model, immutable command catalog and codecs, structured
> result model, and semantic registry are implemented and verified. FIFO dispatch, stage
> execution and state probes, record/replay, Unity UI, WebSocket, and MCP behavior remain
> unimplemented.

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

1. Restore the pinned NuGetForUnity CLI tool and Unity NuGet dependencies.
2. Build `SignalRouter.slnx`.
3. Compile the Unity development project in batch mode.
4. Confirm Unity emitted `SignalRouter.Core.dll` and `SignalRouter.Protocol.dll`.

`task test` runs the NUnit projects on `net10.0`, then runs Unity EditMode tests. The
Unity result XML must exist, contain at least one test, and report no failures.

`task check` adds spelling, Conventional Commit, and DCO checks before build and test.
Build logs and Unity result XML are written below `.artifacts/`.

## Compatibility boundary

The distributable Runtime sources live under:

- `src/SignalRouter/Runtime/Core`
- `src/SignalRouter/Runtime/Protocol`

The SDK-style Core and Protocol projects compile those same files as `netstandard2.1`
with `LangVersion` set to `9.0` and warnings treated as errors. This is the enforcement
boundary for the UPM package: Runtime code must not use `record struct`, `required`, or
PolySharp-generated types. Commands use ordinary `readonly struct` types with explicit
value equality. Immutable result, schema, and descriptor classes use defensive copies and
structural equality.

The Unity development project separately sets the Standalone Player additional compiler
argument to `-langversion:preview`. [Unity 6 officially supports C# 9][unity-csharp]; this
override is outside the official support boundary and enables the preview understood by
the Editor's bundled compiler, not an exact C# 11 ceiling. The EditMode test compiles and
runs `record struct` and `required` constructs to verify the C# 11 features this project
uses.

[PolySharp 1.16.0][polysharp] is restored into the ignored `Assets/Packages` directory
before Unity starts. `Assets/Default.globalconfig` disables PolySharp's embedded marker
because that marker name is reserved by the compiler used with preview mode. This
preserves the required-member and init-only polyfills without masking compilation
failures. Unity documents project-wide analyzer configuration through
[`Default.globalconfig`][unity-globalconfig].

NuGetForUnity also restores VitalRouter and System.Text.Json into the ignored package
directory. Runtime asmdefs keep `overrideReferences: false`; Unity automatically
references precompiled plug-ins in that mode. The EditMode test asmdef additionally names
`VitalRouter.dll` because its test doubles directly implement public generic contracts
whose constraints include `VitalRouter.ICommand`. See Unity's
[assembly reference rules][unity-assembly-references].

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
| [System.Text.Json][system-text-json] | 10.0.10 | Explicit command schemas and codecs |
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
[system-text-json]: https://www.nuget.org/packages/System.Text.Json/10.0.10
[unity-assembly-references]: https://docs.unity3d.com/6000.0/Documentation/Manual/assembly-definitions-referencing.html
[unity-csharp]: https://docs.unity3d.com/6000.0/Documentation/Manual/csharp-compiler.html
[unity-globalconfig]: https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Compilation.ScriptCompilerOptions.AnalyzerConfigPath.html
[vitalrouter]: https://www.nuget.org/packages/VitalRouter/2.8.0
