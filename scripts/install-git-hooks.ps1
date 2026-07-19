#Requires -Version 5.1
<#
.SYNOPSIS
  Install local git hooks (Conventional Commits + DCO).

.DESCRIPTION
  Prefers lefthook when available (same as oss-project-template / `task setup`).
  Falls back to core.hooksPath = .githooks for environments without lefthook.

.EXAMPLE
  pwsh ./scripts/install-git-hooks.ps1
#>

$ErrorActionPreference = 'Stop'

$root = git rev-parse --show-toplevel 2>$null
if (-not $root) {
    Write-Error "Not inside a Git repository."
    exit 1
}

Push-Location $root
try {
    $lefthook = Get-Command lefthook -ErrorAction SilentlyContinue
    if ($lefthook) {
        & lefthook install
        Write-Host "lefthook installed (pre-commit spellcheck, commit-msg conventional + DCO)."
        Write-Host 'Example:  git commit -s -m "fix: correct off-by-one in parser"'
        return
    }

    $hooksPath = Join-Path $root '.githooks'
    if (-not (Test-Path -LiteralPath (Join-Path $hooksPath 'commit-msg'))) {
        Write-Error "Expected hook not found: $hooksPath\commit-msg"
        exit 1
    }

    git config core.hooksPath .githooks
    $current = git config --get core.hooksPath
    Write-Host "lefthook not found; using core.hooksPath = $current"
    Write-Host "Local commit-msg hook enabled (Conventional Commits + DCO)."
    Write-Host 'Example:  git commit -s -m "fix: correct off-by-one in parser"'
    Write-Host "Optional: install lefthook and re-run this script, or run: task setup"
}
finally {
    Pop-Location
}
