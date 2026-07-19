#Requires -Version 7.0

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = git rev-parse --show-toplevel
if (-not $root) {
    throw 'Not inside a Git repository.'
}

Push-Location $root
try {
    git show-ref --verify --quiet refs/remotes/origin/main
    if ($LASTEXITCODE -eq 0) {
        $commits = @(git log --format=%H origin/main..HEAD)
    }
    else {
        $commits = @(git log --format=%H -5)
    }

    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to read the Git commit history.'
    }

    $pattern = '^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([^)]+\))?(!)?: .+'
    foreach ($commit in $commits) {
        $subject = git log -1 --format=%s $commit
        if ($subject -match '^Merge ') {
            continue
        }

        if ($subject -notmatch $pattern) {
            throw "Commit $commit does not follow Conventional Commits: $subject"
        }

        $body = (git log -1 --format=%B $commit) -join "`n"
        if ($body -notmatch '(?m)^Signed-off-by:') {
            throw "Commit $commit is missing a DCO sign-off."
        }
    }

    Write-Host 'All inspected commits follow Conventional Commits and have DCO sign-off.'
}
finally {
    Pop-Location
}
