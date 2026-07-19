#!/usr/bin/env bash
# Validate a single commit subject against Conventional Commits.
# Kept in sync with lefthook.yml / .githooks/commit-msg and PR title rules (pr-title.yml).
#
# Usage: validate-commit-subject.sh <subject>
# Exit 0 if valid (or skipped), 1 if invalid, 2 on usage error.

set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <subject>" >&2
  exit 2
fi

subject=$1
# Drop CR from Windows editors / git
subject=${subject//$'\r'/}

# Empty subject (should not happen for a real commit)
if [[ -z "${subject// }" ]]; then
  echo "Commit message subject is empty." >&2
  exit 1
fi

# Skip merge / revert subjects (git defaults, GitHub, and common variants).
# Case-insensitive; also "Merged …". CI additionally skips multi-parent commits.
if [[ "$subject" =~ ^[Mm]erge[d]?[[:space:]] ]] ||
   [[ "$subject" =~ ^[Rr]evert[[:space:]] ]]; then
  exit 0
fi

# Release Please titles/subjects (and common variants). Prefer conventional form:
#   chore(main): release 1.2.3
# Also accept bare "release X.Y.Z" so automated / emergency release commits are not blocked.
if [[ "$subject" =~ ^[Rr]elease[[:space:]]+[vV]?[0-9] ]] ||
   [[ "$subject" =~ ^Prepare[[:space:]]+[Rr]elease[[:space:]]+[vV]?[0-9] ]] ||
   [[ "$subject" =~ ^(build|chore|ci)(\([a-zA-Z0-9._/-]+\))?(!)?:[[:space:]]+[Rr]elease[[:space:]] ]]; then
  exit 0
fi

# type(scope)!: description — description required, non-empty
pattern='^(build|chore|ci|docs|feat|fix|perf|refactor|revert|style|test)(\([a-zA-Z0-9._/-]+\))?(!)?: .+'

if [[ "$subject" =~ $pattern ]]; then
  exit 0
fi

echo "Commit message is not Conventional Commits: $subject" >&2
echo "" >&2
echo "Use Conventional Commits, for example:" >&2
echo "  fix: correct off-by-one in parser" >&2
echo "  feat: add config file support" >&2
echo "  chore: update dependencies" >&2
echo "  ci: tighten release workflow" >&2
echo "" >&2
echo "See .github/CONTRIBUTING.md. Format: type(optional-scope): description" >&2
exit 1
