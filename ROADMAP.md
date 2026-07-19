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

## Themes (near term)

Ideas under consideration (not commitments):

- **Prior-art evaluation** — assess [Gua](https://github.com/link1345/gua) (semantic UI tree, record/replay, MCP bridge) before building, and decide between contributing, building a complementary bus-integration layer, or not building.
- **Semantic UI registry** — the source of truth that tracks every interactive element and answers "what can be done here now?" (`get_ui_tree`).
- **Core request/response seam** — validation, total-order sequencing, recording, and fail-fast exception handling built on MessagePipe `RequestAll`.
- **Deterministic record & replay** — sequential execution with `Rejected`/`Faulted` separation and exact interruption-point reproduction; `StageTracker` via filters.
- **MCP bridge & tool surface** — external MCP server bridged to the Unity runtime over WebSocket, with domain-reload resilience and main-thread marshalling.
- **Security posture** — treat external drive as an RCE surface: localhost-only, token auth, disabled by default in release builds.

## How to Contribute

### Suggesting Features

1. Check [Issues](https://github.com/dreamingdog0529/SignalRouter/issues) for existing plans
2. Open a [Feature Request](https://github.com/dreamingdog0529/SignalRouter/issues/new?template=feature_request.yml) or [Proposal](https://github.com/dreamingdog0529/SignalRouter/issues/new?template=proposal.yml)

### Discussing Roadmap

- Participate in [GitHub Discussions](https://github.com/dreamingdog0529/SignalRouter/discussions)

## Release Cadence

This project uses automated releases via Release Please (conventional commits on the default branch).
See [GitHub Releases](https://github.com/dreamingdog0529/SignalRouter/releases) for the latest versions and [CONTRIBUTING.md](.github/CONTRIBUTING.md) for the process.
