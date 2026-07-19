# Template Adoption Checklist

Based on [container-registry/oss-project-template](https://github.com/container-registry/oss-project-template) CHECKLIST.md.
Once your project is set up and everything works, this file may be deleted.

## 1. Initialize (replace placeholders)

Run the initializer once. It replaces every `{{TOKEN}}` across the repo and
removes the template banner from the READMEs:

```powershell
pwsh ./scripts/init-template.ps1
```

It prompts for (or accepts as parameters):

| Token | Meaning |
|-------|---------|
| `{{PROJECT_NAME}}` | Project display name |
| `{{REPO_NAME}}` | GitHub repository name (defaults to project name) |
| `{{OWNER}}` | GitHub owner/org |
| `{{DESCRIPTION}}` | Short description (English) |
| `{{DESCRIPTION_JA}}` | Short description (Japanese) |
| `{{AUTHOR}}` | Copyright holder (LICENSE) |
| `{{YEAR}}` | Copyright year |
| `{{BUILD_COMMAND}}` | Build command for CI / Task |
| `{{TEST_COMMAND}}` | Test command for CI / Task |

After running, review the diff and fill in the remaining `TODO` markers in
`README.md`, `README_ja.md`, `ROADMAP.md`, and `docs/development.md`.

## 2. Wire up your project

- [ ] Add your application/library source under `src/` and tests under `tests/`
- [ ] Set real `build`/`test` commands (they were seeded from your answers)
- [ ] Enable the matching ecosystem in `.github/dependabot.yml`
- [ ] Adjust `.github/labeler.yml` paths to your layout
- [ ] Add project languages to the matrix in `.github/workflows/codeql.yml` (template ships `actions` only)

## 3. Maintainer one-time (GitHub UI / secrets)

- [ ] Settings → Actions → Workflow permissions: **read/write**; allow Actions to create PRs
- [ ] Optional: create a classic PAT with `repo` scope → secret `SETTINGS_TOKEN` (full `settings.yml` apply)
- [ ] Optional: install [dco-2](https://github.com/apps/dco-2) for DCO checks on PRs
- [ ] Merge a settings change so labels from `.github/settings.yml` are created
- [ ] Confirm squash-only merge is applied (or set manually to match `settings.yml`)
- [ ] Enable GitHub Discussions and Dependency graph (Settings → General / Code security)
- [ ] Review [docs/security-hardening.md](docs/security-hardening.md) and work through its security health checklist

## 4. Verification

- [ ] Open a test issue — forms render
- [ ] Open a test PR — auto-labels (path + size), PR title lint
- [ ] Actions: CI, spellcheck, dependency-review run green
- [ ] Push a `feat:` commit to the default branch — Release Please PR appears
- [ ] Merge the Release PR — tag and GitHub Release are created

## Local development

```powershell
# hooks (lefthook if installed, else .githooks)
pwsh ./scripts/install-git-hooks.ps1

# or
task setup
task check
```

After everything works, delete this file and `scripts/init-template.ps1`
(the initializer offers to remove them for you).
