# Roadmap

## Project Board

The project roadmap is tracked with GitHub Issues, milestones, and projects:

**[Issues](https://github.com/dreamingdog0529/SignalRouter/issues)** ·
**[Milestones](https://github.com/dreamingdog0529/SignalRouter/milestones)** ·
**[Projects](https://github.com/dreamingdog0529/SignalRouter/projects)**

These provide:
- Real-time status of planned features
- Release milestones and timelines
- Priority and progress tracking
- Links to related issues and PRs

## MVP Workstreams

The accepted MVP architecture and completion criteria are defined in
[docs/design.md](docs/design.md). Near-term work is organized into these workstreams:

- **Core interaction model** — data-only `IInteractionCommand` values, command catalog,
  structured results, stable target IDs, and the semantic UI registry.
- **Deterministic execution** — one global FIFO, VitalRouter command routing, explicit
  stage ordering, fail-fast execution, and partial-progress reporting.
- **State-aware record and replay** — append-only recordings, state probes, secret
  redaction, strict sequential replay, and structured divergence reports.
- **uGUI integration** — Button and text-input adapters that route human input through
  `IInteractionDispatcher`, plus EditMode and PlayMode validation.
- **MCP runtime bridge** — an external MCP host connected to Unity over an authenticated
  loopback WebSocket, with domain-reload recovery and main-thread handoff.
- **Security and operability** — release-build opt-in, bounded payloads and queues,
  artifact-root confinement, diagnostics, and compatibility checks.

The initial compatibility target is Unity 6 on Windows Editor with Mono and uGUI.
Additional controls, UI Toolkit, Player builds, IL2CPP, and other platforms follow only
after the MVP acceptance criteria are met.

## How to Contribute

### Suggesting Features

1. Check [Issues](https://github.com/dreamingdog0529/SignalRouter/issues) for existing plans
2. Open a [Feature Request](https://github.com/dreamingdog0529/SignalRouter/issues/new?template=feature_request.yml) or [Proposal](https://github.com/dreamingdog0529/SignalRouter/issues/new?template=proposal.yml)

### Discussing Roadmap

- Participate in [GitHub Discussions](https://github.com/dreamingdog0529/SignalRouter/discussions)

## Release Cadence

This project uses automated releases via Release Please (conventional commits on the default branch).
See [GitHub Releases](https://github.com/dreamingdog0529/SignalRouter/releases) for the latest versions and [CONTRIBUTING.md](.github/CONTRIBUTING.md) for the process.
