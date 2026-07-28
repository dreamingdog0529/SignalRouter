# Developing SignalRouter

> **Current status:** The engine-agnostic core is implemented — the single-owner
> kernel and its typed contracts, the observation/materialization layer with
> content-addressed canonical state, durable recording (the E1–E8 evidence
> artifact, RecordingEventSchema 1.1), isolated-twin replay with typed
> three-valued comparison, the seal evaluator, and the adapter TCK the in-repo
> reference adapter passes end to end. The protocol gateway (MCP surface) and
> the Unity adapter are the next roadmap items; until the Unity adapter lands,
> the repository builds and tests with the .NET SDK alone.

## Prerequisites

- .NET SDK 10.0.302 (pinned by `global.json`)
- PowerShell 7 and [Task](https://taskfile.dev/)
- [typos](https://github.com/crate-ci/typos) for `task check`

## Build, test, check

Run the supported wrappers from the repository root:

```sh
task build   # dotnet build ./SignalRouter.slnx
task test    # dotnet test  ./SignalRouter.slnx
task check   # spellcheck + commit lint + build + test (mirrors CI)
```

CI runs the same solution in Debug, Release (the allocation gates of
[spec/performance.md](spec/performance.md) only mean something against
optimized code), and on a Windows runner.

## Toolchain and compatibility boundary

Every runtime assembly under `src/` targets `netstandard2.1` with
`LangVersion` 9.0, zero `PackageReference`s, and warnings as errors — the
distribution constraint that keeps the assemblies consumable by Unity's
Mono/IL2CPP toolchain without preview language settings. Test projects target
`net10.0` and may take test-only packages (NUnit, BenchmarkDotNet).

Public API surfaces are snapshotted per assembly in
`tests/SignalRouter.ApiSurface.Tests/Baselines/`; an intentional surface
change regenerates them with `SIGNALROUTER_API_BASELINE_REGENERATE=1` and the
diff is reviewed with the change that caused it.

## Layout

| Path | Contents |
|---|---|
| `src/` | The runtime assemblies (`Contracts`, `Kernel`, `AdapterSdk`, the codec leaves, `Comparison`, `Recording`, `Replay`, `ReferenceAdapter`, `Tck`) |
| `tests/` | NUnit suites per assembly, the API-surface snapshots, and the performance gates |
| `bench/` | The BenchmarkDotNet harness and the measured baseline/profile documents |
| `docs/` | Philosophy, architecture, the normative spec set, and the ADRs |