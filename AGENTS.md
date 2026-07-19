## Information Accuracy
Verify anything that changes (versions, APIs, pricing) against official docs and cite the source URL; if none is reliable, say so and never fabricate one. What's checkable in code or tests, confirm by running it.

## No Guessing, Question Premises
- Ambiguous or missing info → ask before implementing.
- Two viable approaches → present both with trade-offs and let me choose.
- When presenting discrete choices to me, use AskUserQuestion.
- Don't take my premises at face value. Reproduce bugs and confirm against the codebase before fixing. If a design is flawed, say so with evidence and propose an alternative.

## Plan First
Use plan mode for any 3+ step task or significant design decision, writing a spec before starting. If work stalls, stop and re-plan rather than pushing on momentum. Delegate research and parallel analysis to subagents (one task per subagent); reserve the main thread for decisions and implementation.

## Quality Bar
Build it right the first time. No stubs, placeholders, temp data, or commented-out code in delivered work. If a proper solution needs more time, info, or a design decision, stop and say so instead of silently shipping a makeshift version (exception: when I explicitly ask for a prototype).

## Error Handling (fail fast)
No silent fallbacks, default values, or degraded modes that mask a failure. Never swallow exceptions (no empty catches, no catch-log-continue); catch only where meaningfully handled, else re-raise with context. Add intentional fallbacks (retries, graceful degradation) only after approval.

## Refactoring
Before a significant change, ask "Is there a more elegant way?" and prefer restructuring over patching symptoms. Don't over-engineer obvious small fixes. For large rewrites, present scope, risks, migration, and what breaks in plan mode and get approval.

## Build, Run, Test
Use the [Task](https://taskfile.dev/) wrappers:

```bash
task build
task test
task check   # spellcheck + commit-lint + dco-check + build + test
```

Run `task check` before every push — it mirrors CI, so a green run locally is the fastest path to a green PR.

## Definition of Done
Never report "done" without evidence. Done = builds cleanly, tests pass (`task test`), edge cases handled. Confirm the intended effect via logs, tests, and before/after diffs. Treat visual/image inspection as a last resort, used only when no programmatic alternative exists.

## Git
- **Conventional Commits v1.0.0.** Body explains *why*. Mark breaking changes with `!` + a `BREAKING CHANGE:` footer; reference issues in the footer. Types: `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`, `revert`.
- **DCO sign-off on every commit** (`git commit -s`, adding `Signed-off-by:`). The `commit-msg` hook and CI enforce both this and the Conventional format — install hooks with `pwsh ./scripts/install-git-hooks.ps1` or `task setup`.
- One logical change per commit; each commit builds and passes tests on its own.
- **Trunk-based:** keep the default branch always deployable. Do non-trivial work on short-lived branches (`<type>/<short-kebab-desc>`, e.g. `feat/oauth-login`), merged and deleted within a day or two. Rebase before merge; squash WIP.
- **Squash-merge only:** the **PR title** becomes the single commit on the default branch — keep it Conventional (CI lints it). Open a PR, fill in the template, and wait for CI; if it fails, fix and re-push until green before merging.
- **Releases are automated (Release Please):** never hand-edit `CHANGELOG.md`, `version.txt`, or tags. Merging Conventional Commits to the default branch drives the version bump, release PR, tag, and GitHub Release.
- Commit and push frequently within the scope you implemented, without asking for confirmation.

## Tool Boundaries
Don't automate complex, state-sensitive, or unverifiable external-tool operations; give me step-by-step instructions to run myself. Automate only simple, reliably verifiable actions.

## Communication
Lead with the conclusion, then the reasoning. Concise, analytical, no flattery or filler.