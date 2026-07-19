# Contributing Guide For SignalRouter

Thanks for contributing. This project follows the same principles as
[container-registry/oss-project-template](https://github.com/container-registry/oss-project-template):

| Principle | Implementation |
|-----------|----------------|
| **Conventional Commits** | PR titles (and commits) use `type: description` |
| **Squash merge only** | One commit per PR on the default branch |
| **PR-based workflow** | All changes via pull requests |
| **DCO sign-off** | `git commit -s` (`Signed-off-by`) |
| **Automated releases** | Release Please from commit types |
| **Local-first** | Lefthook / Task / git hooks before push |
| **GitHub-native** | Labels, settings, Scorecard, dependency review |

## Code of conduct

Participation is governed by our [Code of Conduct](CODE_OF_CONDUCT.md).

## Getting help

See [SUPPORT.md](SUPPORT.md). Roadmap notes: [ROADMAP.md](../ROADMAP.md).

## Development environment

| Requirement | Notes |
|-------------|--------|
| Toolchain | See [docs/development.md](../docs/development.md) |
| Optional | [typos](https://github.com/crate-ci/typos), [lefthook](https://github.com/evilmartians/lefthook), [Task](https://taskfile.dev/) |
| Git hooks | `pwsh ./scripts/install-git-hooks.ps1` or `task setup` |

### Build, run, test

```sh
dotnet build
dotnet test
```

Or with Task:

```bash
task build
task test
task check
```

See [docs/development.md](../docs/development.md) for full details.

### Project layout

```
src/       Application / library source
tests/     Test suite
docs/      Documentation
scripts/   Dev utilities and helpers
.github/   Workflows, issue/PR templates, settings
```

## Issues, requests & ideas

Use [GitHub Issues](https://github.com/dreamingdog0529/SignalRouter/issues) with the YAML forms:

- Bug Report
- Feature Request
- Proposal (larger design changes)

Labels such as `bug`, `enhancement`, `proposal`, and `needs-triage` are applied by the forms.

## Contribution checklist

- [ ] Clean, simple, well-styled code
- [ ] Conventional Commits (`type: description`); related issues referenced by number
- [ ] DCO sign-off on every commit (`git commit -s`)
- [ ] Tests pass (`dotnet test` / `task test`)
- [ ] Minimize dependencies; prefer Apache-2.0, BSD-3, MIT, ISC, MPL
- [ ] No secrets or build artifacts (`bin/`, `obj/`, `dist/`, `build/`)

## Creating a pull request

1. Search Issues; open one if needed.
2. Fork/clone and create a focused branch.
3. Commit with **Conventional Commits** and **DCO**:

   ```bash
   git commit -s -m "fix: correct off-by-one in parser"
   ```

4. Push and open a PR against the default branch. Fill in the PR template.
5. Ensure CI is green (build/test, PR title, spellcheck, dependency review, etc.).

> Sync your fork before opening the PR if you forked.

### Commit message format

```
<type>[optional scope]: <description>

[optional body]

Signed-off-by: Name <email>
```

**Types:** `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`, `revert`

| Prefix | Version impact |
|--------|----------------|
| `feat:` | minor |
| `fix:` and most others | patch |
| `feat!:` / `fix!:` / `BREAKING CHANGE:` | major |

Before `1.0.0`, `feat:` still bumps **minor** (`bump-minor-pre-major`).

**Squash merge:** the **PR title** becomes the commit on the default branch — keep it Conventional (CI: `pr-title.yml`).

Rules of thumb:

- Format: `type(optional-scope): description` (description required; prefer lowercase start).
- **Bots exempt from PR title CI:** Dependabot (`chore(deps):` via `dependabot.yml`) and Release Please (`github-actions[bot]` / `release-please--*` branches; title is usually `chore(main): release X.Y.Z`).
- Local hook also accepts release subjects such as `chore(main): release 1.2.3` and `Release 1.2.3` so release commits are not blocked by mistake.
- **Merge commits are skipped** (e.g. `Merge branch 'main' into your-branch`): the local hook detects `MERGE_HEAD` / `CHERRY_PICK_HEAD` / `REVERT_HEAD`, and `validate-commit-subject.sh` also skips merge/revert subjects.

### Local hooks

```powershell
pwsh ./scripts/install-git-hooks.ps1
# or: task setup
```

- **pre-commit** (lefthook): spellcheck staged markdown/yaml
- **commit-msg**: Conventional Commits + DCO

## DCO

This project uses the [Developer Certificate of Origin](https://developercertificate.org/).
Sign off every commit with `-s`. Maintainers may enable the [dco2](https://github.com/apps/dco2) GitHub App (see `.github/dco.yml`).

## Automation

| Area | Workflow / config |
|------|-------------------|
| Build & test | `ci.yml` |
| PR title (Conventional Commits) | `pr-title.yml` |
| PR labels (paths) | `labeler.yml` + `.github/labeler.yml` |
| PR size labels | `pr-size-labeler.yml` |
| Welcome first-timers | `welcome.yml` |
| Spell check | `spellcheck.yml` + `.typos.toml` |
| Dependency review | `dependency-review.yml` |
| OpenSSF Scorecard | `scorecard.yml` |
| Repo settings / labels | `settings.yml` + `apply-settings.yml` |
| Contributors | `contributors.yml` → `README.md` (Contributors section) |
| Version & changelog | `release-please.yml` + `release-please-config.json` |

### Release process (maintainers)

1. Merge PRs to the default branch with Conventional Commits (squash).
2. Release Please opens/updates a release PR.
3. Merge the release PR → tag + GitHub Release.
4. (Optional) Add a `release-assets` job to build and attach release artifacts.

One-time: Actions read/write + allow Actions to create PRs. Optional: `SETTINGS_TOKEN` (repo PAT) for full `settings.yml` apply; install dco2 for DCO checks on PRs.

## Security

Do not file public issues for vulnerabilities. See [SECURITY.md](SECURITY.md).

## License

By contributing, you agree that your contributions are licensed under the project [MIT License](../LICENSE) and that you have the right to submit them under the DCO.
