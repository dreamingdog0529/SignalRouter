<a id="readme-top"></a>

<div align="center">

English | [日本語](./README_ja.md)

<!-- TODO: add your logo and uncomment
<img src="assets/logo.png" alt="SignalRouter logo" width="120" height="120">
-->

<h1>SignalRouter</h1>

<p><em>Simulate and replay UI operations as structured commands for reproducible debugging and screenshot-free MCP agent control (Pure C# + Unity).</em></p>

[![CI](https://github.com/dreamingdog0529/SignalRouter/actions/workflows/ci.yml/badge.svg)](https://github.com/dreamingdog0529/SignalRouter/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/dreamingdog0529/SignalRouter?include_prereleases&sort=semver)](https://github.com/dreamingdog0529/SignalRouter/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/dreamingdog0529/SignalRouter/badge)](https://securityscorecards.dev/viewer/?uri=github.com/dreamingdog0529/SignalRouter)

<p>
  <a href="docs/development.md"><strong>Explore the docs »</strong></a>
  <br /><br />
  <a href="https://github.com/dreamingdog0529/SignalRouter/issues/new?template=bug_report.yml">Report Bug</a>
  ·
  <a href="https://github.com/dreamingdog0529/SignalRouter/issues/new?template=feature_request.yml">Request Feature</a>
  ·
  <a href="https://github.com/dreamingdog0529/SignalRouter/discussions">Discussions</a>
</p>

</div>

<details>
  <summary>Table of Contents</summary>
  <ol>
    <li><a href="#about">About The Project</a></li>
    <li><a href="#features">Features</a></li>
    <li>
      <a href="#getting-started">Getting Started</a>
      <ul>
        <li><a href="#prerequisites">Prerequisites</a></li>
        <li><a href="#installation">Installation</a></li>
      </ul>
    </li>
    <li><a href="#usage">Usage</a></li>
    <li><a href="#development">Development</a></li>
    <li><a href="#roadmap">Roadmap</a></li>
    <li><a href="#contributing">Contributing</a></li>
    <li><a href="#project-docs">Project Docs</a></li>
    <li><a href="#license">License</a></li>
    <li><a href="#acknowledgments">Acknowledgments</a></li>
  </ol>
</details>

<a id="about"></a>

## About The Project

<!-- TODO: add a screenshot and uncomment
<img src="assets/screenshot.png" alt="SignalRouter screenshot">
-->

SignalRouter is a Unity runtime (with a Pure C# core) that represents UI operations as
structured, serializable commands. Instead of driving the UI through pixels and
screenshots, it exposes a **semantic UI tree** — every interactive element with its
`id`, `role`, `label`, current value, `enabled`/`visible` state, and the operations it
currently allows — so command sequences can be executed, recorded, and deterministically
replayed.

That enables two things: **reproducible debugging** (capture a failing session and replay
the exact command sequence, application handlers and all) and **screenshot-free control by
AI agents over MCP** (an agent enumerates the operations available in the current screen
and drives them directly). It is aimed at teams building Unity apps and games who want
their UI to be observable and controllable as data.

> **Status:** Design phase — the MVP has not started yet. The architecture and the
> interfaces below are drafts. See the [design notes](docs/design.md) for the decision
> trail and open questions.

### Built With

- **[Unity](https://unity.com/)** (uGUI / UI Toolkit) — the UI runtime being observed and driven
- **Pure C#** (.NET Standard 2.1) — the core has no Unity dependency
- **[MessagePipe](https://github.com/Cysharp/MessagePipe)** — in-process `RequestAll` fan-out at the core request/response seam
- **[VitalRouter](https://github.com/hadashiA/VitalRouter)** — command bus option for element-local pub/sub
- **[Model Context Protocol](https://modelcontextprotocol.io/)** (MCP) — agent-facing tool surface, bridged to the runtime over WebSocket

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<a id="features"></a>

## Features

- **Semantic UI tree** — observe every interactive element (`id` / `role` / `label` / value / state) as a screenshot-free source of truth for what can be done right now.
- **Structured commands** — UI operations modeled as serializable `record struct` commands (`click`, `set_text`, …) so agent, test, and real-play input share one command type.
- **Record & replay** — every command passes a single core seam where it is recorded, so sessions replay deterministically with host-completion confirmation (queue-accepted ≠ done).
- **Deterministic fault model** — sequential execution separates `Rejected` (validation, zero side effects) from `Faulted` (failed at stage *k*, with *k−1* applied) and reproduces the exact interruption point.
- **MCP agent control** — `get_ui_tree`, `wait_for`, and execution tools let agents drive the UI without pixels; exceptions are caught at the seam and never leak across the MCP boundary.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<a id="getting-started"></a>

## Getting Started

The project is in its design phase, so there is no released package yet. To follow along
or experiment with the architecture, clone the repository.

<a id="prerequisites"></a>

### Prerequisites

- Unity 2022 LTS or newer (uGUI and/or UI Toolkit)
- .NET Standard 2.1 toolchain for the Pure C# core
- [MessagePipe](https://github.com/Cysharp/MessagePipe) for the request/response seam
- An MCP-capable client if you want to drive the UI from an agent

<a id="installation"></a>

### Installation

```sh
git clone https://github.com/dreamingdog0529/SignalRouter.git
cd SignalRouter
```

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<a id="usage"></a>

## Usage

The public API is still being designed. The intended shape is a single core contract that
each interactive element implements, with the core owning validation, ordering, recording,
and exception handling at the request/response seam:

```csharp
// The only contract the core requires of a UI element.
public interface IInteractable {
    ElementDescriptor Describe();                 // powers get_ui_tree
    bool CanAccept(in UiCommand cmd);             // validation, before publish
    UniTask<ExecuteResult> ExecuteAsync(          // publish -> await all subs -> respond
        UiCommand cmd, CancellationToken ct);
}
```

An MCP agent then calls `get_ui_tree` to see the available operations, issues commands, and
uses `wait_for` to await multi-frame settling — no screenshots involved. Concrete
signatures will land with the MVP.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<a id="development"></a>

## Development

```sh
dotnet build
dotnet test
```

Full development and build instructions: **[docs/development.md](docs/development.md)**
How to contribute: **[CONTRIBUTING.md](.github/CONTRIBUTING.md)**

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<a id="roadmap"></a>

## Roadmap

See the [open issues](https://github.com/dreamingdog0529/SignalRouter/issues) and
[ROADMAP.md](ROADMAP.md) for planned features and known issues.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<a id="contributing"></a>

## Contributing

Contributions are welcome. Please read **[CONTRIBUTING.md](.github/CONTRIBUTING.md)** for the
workflow (Conventional Commits, DCO sign-off, PR process) and our
[Code of Conduct](.github/CODE_OF_CONDUCT.md).

Thanks to everyone who has contributed to SignalRouter. This list is updated automatically from git history.

<!-- readme: contributors -start -->
<table>
	<tbody>
		<tr>
            <td align="center">
                <a href="https://github.com/dreamingdog0529">
                    <img src="https://avatars.githubusercontent.com/u/301185108?v=4" width="100;" alt="dreamingdog0529"/>
                    <br />
                    <sub><b>dreamingdog0529</b></sub>
                </a>
            </td>
		</tr>
	<tbody>
</table>
<!-- readme: contributors -end -->

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<a id="project-docs"></a>

## Project Docs

Repository automation and community files are adapted from
[container-registry/oss-project-template](https://github.com/container-registry/oss-project-template).

| Document | Purpose |
|----------|---------|
| [CONTRIBUTING.md](.github/CONTRIBUTING.md) | Develop, test, PRs, DCO, CI/CD, releases |
| [SUPPORT.md](.github/SUPPORT.md) | How to get help |
| [ROADMAP.md](ROADMAP.md) | Direction and how to propose work |
| [CODE_OF_CONDUCT.md](.github/CODE_OF_CONDUCT.md) | Community standards |
| [SECURITY.md](.github/SECURITY.md) | Private vulnerability reporting |
| [CODEOWNERS](CODEOWNERS) | Default code review owners |
| [CHANGELOG.md](CHANGELOG.md) | Release history |
| [LICENSE](LICENSE) | MIT license text |

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<a id="license"></a>

## License

Distributed under the MIT License. See [LICENSE](LICENSE) for more information.

MIT © 2026 dreamingdog0529

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<a id="acknowledgments"></a>

## Acknowledgments

<!-- TODO: List the resources, libraries, and people your project builds on. Replace the example below. -->

- [Resource name](https://example.com) — what it provided

<p align="right">(<a href="#readme-top">back to top</a>)</p>
