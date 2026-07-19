# Developing SignalRouter

## Prerequisites

<!-- TODO: List the toolchain and versions your project needs. Examples:
- Node.js 22+
- Go 1.23+
- Python 3.12+
- .NET 10 SDK
- Rust (stable)
-->

Optional developer tools (used by CI and local hooks):

- [typos](https://github.com/crate-ci/typos) — spell check
- [lefthook](https://github.com/evilmartians/lefthook) — git hooks
- [Task](https://taskfile.dev/) — task runner

## Build, run, test

```sh
dotnet build
dotnet test
```

Or via [Task](https://taskfile.dev/):

```bash
task build
task test
task check   # spellcheck + commit-lint + dco-check + build + test
```

## Git hooks

Install local hooks (Conventional Commits + DCO sign-off):

```powershell
pwsh ./scripts/install-git-hooks.ps1
# or
task setup
```

- **pre-commit**: spellcheck staged markdown/yaml (via lefthook)
- **commit-msg**: enforce Conventional Commits + DCO `Signed-off-by`

## Commit & PR conventions

- Use [Conventional Commits](https://www.conventionalcommits.org/): `type: description`
- Sign off every commit: `git commit -s`
- Squash-merge only; the PR title becomes the commit on the default branch

See [CONTRIBUTING.md](../.github/CONTRIBUTING.md) for the full workflow.

## Releases

Releases are automated with [Release Please](https://github.com/googleapis/release-please):

1. Merge Conventional Commits to the default branch.
2. Release Please opens/updates a release PR (version bump + CHANGELOG).
3. Merge the release PR to create the tag and GitHub Release.

<!-- TODO: If you build release artifacts (binaries, packages, installers),
document the process here and wire up a release-assets workflow. -->
