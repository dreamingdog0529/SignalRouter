# Security Hardening

Operational guidance for maintainers of projects created from this template. It
focuses on software supply-chain defenses: the CI/CD pipeline, third-party
dependencies, and — increasingly — AI agent tooling.

For reporting vulnerabilities in SignalRouter itself, see
[SECURITY.md](../.github/SECURITY.md).

## Why this matters

Supply-chain attacks target the components developers trust implicitly. Recent
incidents show the attack surface widening across three layers:

1. **Package registries** — typosquatted or dependency-confusion packages on npm,
   PyPI, and others, often with a malicious `postinstall`/build step.
2. **CI/CD pipelines** — compromised GitHub Actions whose tags are force-pushed to
   point at attacker code, exfiltrating the elevated secrets available at build
   time (cloud credentials, registry and publish tokens).
3. **AI agent skills / MCP servers** — repository-jacking, skill-squatting, and
   prompt injection through a tool's own description text.

The common thread: a name, a tag, or a URL that used to be safe silently starts
resolving to something else. Pinning, auditing, and least privilege remove that
implicit trust.

## What this template already ships

| Control | Where |
|---------|-------|
| GitHub Actions pinned to commit SHAs | `.github/workflows/*.yml` |
| Automated action/dependency update PRs | `.github/dependabot.yml` |
| Dependency review on PRs (`fail-on-severity: high`) | `.github/workflows/dependency-review.yml` |
| OpenSSF Scorecard analysis | `.github/workflows/scorecard.yml` |
| CodeQL SAST on push/PR + weekly | `.github/workflows/codeql.yml` |
| Secret scanning + push protection, Dependabot security updates | `.github/settings.yml` |
| Least-privilege `permissions:` (read at top-level, write per-job) | `.github/workflows/*.yml` |
| npm install pinned via lockfile integrity hashes | `.github/scripts/package-lock.json` |
| DCO sign-off + Conventional Commits (provenance) | `lefthook.yml`, `.githooks/` |

The sections below explain how to keep these working and how to extend them once
your project has real source code.

## 1. Pin GitHub Actions to commit SHAs

A tag (`@v4`) or a major-version branch (`@v5`) is a **mutable** pointer: the
owner — or an attacker who compromises the owner — can move it to different code
without changing what your workflow says. A 40-character commit SHA is immutable.

This template pins every action to a SHA with the version in a trailing comment:

```yaml
- uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7
```

The comment keeps the file readable and lets Dependabot recognize and bump the
pin. Keep the convention when you add actions.

### Resolving a tag to a SHA

```bash
# Lightweight tag: object.sha is the commit; type is "commit".
gh api repos/<owner>/<repo>/git/ref/tags/<tag> --jq '.object'

# Annotated tag (type "tag"): dereference to the underlying commit.
gh api repos/<owner>/<repo>/git/tags/<tag-sha> --jq '.object.sha'
```

Always confirm the SHA points at the tag you intended before committing it —
that verification step is the whole point.

**Caveat — some actions ship the major line as a branch, not a tag.** For
example `actions/dependency-review-action@v5` resolves to `refs/heads/v5`, a
moving branch. Pin the immutable release commit instead (the SHA behind the
`vX.Y.Z` tag), which is what this repo does.

SHA pins do not auto-receive upstream fixes, so pair them with Dependabot (the
`github-actions` ecosystem is already enabled in `.github/dependabot.yml`) and
review the update PRs. See
[Keeping your actions up to date with Dependabot](https://docs.github.com/en/code-security/dependabot/working-with-dependabot/keeping-your-actions-up-to-date-with-dependabot).

To convert an existing repository in bulk, StepSecurity's
[secure-repo](https://github.com/step-security/secure-repo) rewrites workflow
tags to SHAs.

## 2. Least-privilege workflow permissions

Every workflow declares an explicit top-level `permissions:` block that is
**read-only** (OpenSSF Scorecard Token-Permissions). Jobs that need write access
elevate only the scopes they use:

```yaml
permissions:
  contents: read

jobs:
  release:
    permissions:
      contents: write
      pull-requests: write
```

Never grant write at the workflow top level, and never rely on the default token
scope.

## 3. Dependency auditing (after you choose a stack)

This template is language-neutral, so no package manifest exists yet and there is
nothing to audit at the package level. Once you add source and a lockfile:

1. **Enable the matching Dependabot ecosystem** in `.github/dependabot.yml`
   (commented examples are provided for npm, pip, gomod, cargo, and more).
2. **Run an audit in CI.** Add a step to `ci.yml` (or a dedicated workflow), for
   example:

   ```yaml
   # Node.js
   - run: npm audit --audit-level=high

   # Python
   - run: pip install pip-audit && pip-audit -r requirements.txt
   ```

3. **Review lockfile diffs in PRs.** An unexpected change to
   `package-lock.json` / `poetry.lock` / `go.sum` is a signal worth reading.
4. **Distrust install-time scripts.** When evaluating an unfamiliar npm package,
   install with `npm install --ignore-scripts` and run build steps only after you
   trust it.

The existing dependency-review workflow already blocks PRs that introduce
high-severity advisories once the Dependency graph is enabled.

## 4. Secret management and rotation

CI/CD secrets are high value because the pipeline runs with elevated privileges.

- **Keep an inventory** of every secret and what it grants
  (`gh secret list --repo <owner>/<repo>`).
- **Prefer short-lived, narrowly-scoped credentials**: a GitHub App token or a
  fine-grained PAT over a classic PAT; OIDC over long-lived cloud keys.
- **Set expiry / rotate on a schedule**, and rotate immediately if an action you
  used may have been compromised.
- **Never echo secrets** into logs; treat build logs as attacker-readable.

Note: this template's optional `SETTINGS_TOKEN` (for applying `settings.yml`) is a
classic PAT by design constraint — scope it to the single repository and rotate
it like any other credential.

## 5. Vetting third-party AI agent skills and MCP servers

If your project or its contributors use AI agents that load third-party skills,
plugins, or MCP servers, treat those the way you treat any dependency — a skill's
description text is injected directly into the model's prompt, so a malicious one
can hijack the agent, not just run bad code.

Before adding one:

- **Confirm the source repository is genuine.** Check that the owner exists and is
  active, and that the account is not brand new
  (`gh api users/<owner> --jq '{login,created_at,public_repos}'`).
- **Check for repository-jacking.** A redirect on the repo URL can mean the
  original owner renamed or deleted the account and someone else claimed the name:
  `curl -sI https://github.com/<owner>/<repo> | grep -i location`.
- **Read the code and the metadata.** Review not just the source but the `name`,
  `description`, and instructions for hidden directives or external network calls.
- **Apply least privilege.** Grant the skill only the permissions it genuinely
  needs, and verify the MCP server endpoint is the one you intend.
- **Prefer signed, provenance-backed sources** and pin to a specific revision
  rather than a floating reference where the framework allows it.

## Security health checklist

Use this to audit a project periodically. Starting with the first two items —
SHA-pinned actions and dependency auditing in CI — already raises the bar
substantially.

**GitHub Actions**

- [ ] All actions are pinned to commit SHAs with a version comment
- [ ] Dependabot is enabled for the `github-actions` ecosystem
- [ ] Every workflow sets top-level read-only `permissions:` and elevates per job
- [ ] Shell package installs use a lockfile (`npm ci`) rather than floating `npm install`
- [ ] CodeQL (or another SAST) runs on push and pull requests
- [ ] Secret scanning and push protection are enabled

**Dependencies**

- [ ] The matching Dependabot ecosystem is enabled for your stack
- [ ] `npm audit` / `pip-audit` (or equivalent) runs in CI
- [ ] Lockfile changes are reviewed in PRs
- [ ] Unnecessary install-time scripts are disabled or scrutinized

**Secrets**

- [ ] An inventory of CI/CD secrets exists
- [ ] Secrets have expiry or a documented rotation cadence
- [ ] Fine-grained PATs / GitHub App tokens are used instead of classic PATs
- [ ] No secret is written to workflow logs

**AI agents (if used)**

- [ ] Third-party skill/MCP sources are verified before adoption
- [ ] Skills request only the permissions they need
- [ ] Packages an agent installs automatically go through an approval step

## References

- [Security hardening for GitHub Actions](https://docs.github.com/en/actions/security-guides/security-hardening-for-github-actions)
- [Keeping your actions up to date with Dependabot](https://docs.github.com/en/code-security/dependabot/working-with-dependabot/keeping-your-actions-up-to-date-with-dependabot)
- [OpenSSF Scorecard](https://github.com/ossf/scorecard)
- [StepSecurity secure-repo](https://github.com/step-security/secure-repo)
