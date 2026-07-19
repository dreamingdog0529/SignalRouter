#Requires -Version 5.1
<#
.SYNOPSIS
  Initialize this repository from the template: replace {{PLACEHOLDER}} tokens
  and strip the template banner from the READMEs.

.DESCRIPTION
  Walks the repository, replaces the template tokens in every text file, and
  removes the <!-- TEMPLATE:START -->...<!-- TEMPLATE:END --> blocks from
  README.md and README_ja.md. Files are written back as UTF-8 (no BOM) with
  their original line endings preserved.

  Any parameter you omit is prompted for interactively.

.EXAMPLE
  pwsh ./scripts/init-template.ps1

.EXAMPLE
  pwsh ./scripts/init-template.ps1 -ProjectName Widget -Owner acme `
    -Description "A small widget" -DescriptionJa "小さなウィジェット" `
    -BuildCommand "make build" -TestCommand "make test"
#>

[CmdletBinding()]
param(
    [string]$ProjectName,
    [string]$RepoName,
    [string]$Owner,
    [string]$Description,
    [string]$DescriptionJa,
    [string]$Author,
    [string]$Year,
    [string]$BuildCommand,
    [string]$TestCommand,
    # Keep CHECKLIST.md and this init script instead of removing them at the end.
    [switch]$KeepInitFiles,
    # Skip all prompts; fail if a required value is missing.
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'

$scriptPath = $MyInvocation.MyCommand.Path
$repoRoot   = Split-Path (Split-Path $scriptPath -Parent) -Parent

function Get-Value {
    param([string]$Current, [string]$Prompt, [string]$Default)
    if ($Current) { return $Current }
    if ($NonInteractive) {
        if ($Default) { return $Default }
        throw "Missing required value: $Prompt (running with -NonInteractive)"
    }
    $suffix = if ($Default) { " [$Default]" } else { "" }
    $answer = Read-Host "$Prompt$suffix"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer
}

# --- Collect values -------------------------------------------------------
$ProjectName   = Get-Value $ProjectName   "Project display name"          "MyProject"
$RepoName      = Get-Value $RepoName       "GitHub repository name"        $ProjectName
$Owner         = Get-Value $Owner          "GitHub owner/org"              ""
$Description    = Get-Value $Description    "Short description (English)"    "A great project"
$DescriptionJa = Get-Value $DescriptionJa  "Short description (Japanese)"   $Description
$Author        = Get-Value $Author         "Copyright holder"              "$ProjectName contributors"
$Year          = Get-Value $Year           "Copyright year"                ([string](Get-Date).Year)
$BuildCommand  = Get-Value $BuildCommand   "Build command"                 'echo "TODO: build"'
$TestCommand   = Get-Value $TestCommand    "Test command"                  'echo "TODO: test"'

if (-not $Owner) { throw "GitHub owner/org is required." }

$replacements = [ordered]@{
    '{{PROJECT_NAME}}'    = $ProjectName
    '{{REPO_NAME}}'       = $RepoName
    '{{OWNER}}'           = $Owner
    '{{DESCRIPTION_JA}}'  = $DescriptionJa   # must precede {{DESCRIPTION}}
    '{{DESCRIPTION}}'     = $Description
    '{{AUTHOR}}'          = $Author
    '{{YEAR}}'            = $Year
    '{{BUILD_COMMAND}}'   = $BuildCommand
    '{{TEST_COMMAND}}'    = $TestCommand
}

Write-Host ""
Write-Host "Applying template values:" -ForegroundColor Cyan
foreach ($k in $replacements.Keys) { "  {0,-20} {1}" -f $k, $replacements[$k] | Write-Host }
Write-Host ""

# --- File selection -------------------------------------------------------
$binaryExt = @(
    '.png','.jpg','.jpeg','.gif','.bmp','.ico','.pdf','.zip','.gz','.7z',
    '.exe','.dll','.pdb','.msi','.p12','.pfx','.cer','.crt','.key',
    '.woff','.woff2','.ttf','.otf','.eot','.mp3','.mp4','.mov','.webp'
)
# Excluded from token replacement (removed later or intentionally literal).
$scriptRel    = 'scripts/init-template.ps1'
$checklistRel = 'CHECKLIST.md'

$files = Get-ChildItem -LiteralPath $repoRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '[\\/]\.git[\\/]' -and
        $binaryExt -notcontains $_.Extension.ToLowerInvariant()
    }

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$changed = 0

foreach ($file in $files) {
    $rel = $file.FullName.Substring($repoRoot.Length + 1) -replace '\\','/'
    if ($rel -eq $scriptRel -or $rel -eq $checklistRel) { continue }

    $content = [System.IO.File]::ReadAllText($file.FullName)
    $original = $content

    foreach ($k in $replacements.Keys) {
        $content = $content.Replace($k, $replacements[$k])
    }

    # Strip template banner blocks (README files). Handles CRLF and LF, and
    # consumes trailing blank lines so the document starts cleanly.
    $content = [regex]::Replace(
        $content,
        '(?s)<!--\s*TEMPLATE:START\s*-->.*?<!--\s*TEMPLATE:END\s*-->\s*',
        ''
    )

    if ($content -ne $original) {
        [System.IO.File]::WriteAllText($file.FullName, $content, $utf8NoBom)
        Write-Host "  updated  $rel"
        $changed++
    }
}

Write-Host ""
Write-Host "Done. $changed file(s) updated." -ForegroundColor Green

# --- Leftover token check -------------------------------------------------
$leftover = Get-ChildItem -LiteralPath $repoRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '[\\/]\.git[\\/]' -and
        $binaryExt -notcontains $_.Extension.ToLowerInvariant()
    } |
    Where-Object {
        $rel = $_.FullName.Substring($repoRoot.Length + 1) -replace '\\','/'
        if ($rel -eq $scriptRel -or $rel -eq $checklistRel) { return $false }
        (Select-String -LiteralPath $_.FullName -Pattern '\{\{[A-Z_]+\}\}' -Quiet)
    }
if ($leftover) {
    Write-Warning "Unreplaced tokens remain in:"
    $leftover | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Yellow }
}

# --- Next steps -----------------------------------------------------------
Write-Host ""
Write-Host "Next steps (maintainer, one-time on GitHub):" -ForegroundColor Cyan
Write-Host "  1. Settings -> Actions -> Workflow permissions: read/write; allow Actions to create PRs"
Write-Host "  2. Optional: add a classic PAT (repo scope) as secret SETTINGS_TOKEN for full settings.yml apply"
Write-Host "  3. Optional: install the dco-2 GitHub App for DCO checks on PRs"
Write-Host "  4. Enable Discussions and the Dependency graph (Settings -> General / Code security)"
Write-Host "  5. Merge a change to .github/settings.yml so labels are created"
Write-Host ""
Write-Host "Fill in remaining TODO markers in README.md, README_ja.md, ROADMAP.md, docs/development.md."
Write-Host "See CHECKLIST.md for the full list."
Write-Host ""

# --- Self-removal ---------------------------------------------------------
if (-not $KeepInitFiles) {
    $doRemove = $true
    if (-not $NonInteractive) {
        $ans = Read-Host "Remove CHECKLIST.md and this init script now? [Y/n]"
        if ($ans -match '^(n|no)$') { $doRemove = $false }
    }
    if ($doRemove) {
        Remove-Item -LiteralPath (Join-Path $repoRoot 'CHECKLIST.md') -ErrorAction SilentlyContinue
        Write-Host "Removed CHECKLIST.md and scripts/init-template.ps1." -ForegroundColor Green
        Remove-Item -LiteralPath $scriptPath -ErrorAction SilentlyContinue
    }
}
