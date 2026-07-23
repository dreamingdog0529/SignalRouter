#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Compile', 'Test', 'PlayTest')]
    [string] $Mode
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $root 'src/SignalRouter.Unity'
$projectVersionPath = Join-Path $projectPath 'ProjectSettings/ProjectVersion.txt'
$versionLine = Get-Content -LiteralPath $projectVersionPath |
    Where-Object { $_ -match '^m_EditorVersion:\s+' } |
    Select-Object -First 1

if (-not $versionLine) {
    throw "Unity version is missing from $projectVersionPath."
}

$unityVersion = ($versionLine -split ':\s+', 2)[1]
if ($IsWindows) {
    $unityEditor = "C:\Program Files\Unity\Hub\Editor\$unityVersion\Editor\Unity.exe"
}
elseif ($IsMacOS) {
    $unityEditor = "/Applications/Unity/Hub/Editor/$unityVersion/Unity.app/Contents/MacOS/Unity"
}
else {
    $unityEditor = "$HOME/Unity/Hub/Editor/$unityVersion/Editor/Unity"
}

if (-not (Test-Path -LiteralPath $unityEditor -PathType Leaf)) {
    throw "Unity $unityVersion was not found at the standard Unity Hub path: $unityEditor"
}

$artifactDirectory = Join-Path $root '.artifacts/unity'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

$commonArguments = @(
    '-batchmode'
    '-nographics'
    '-accept-apiupdate'
    '-projectPath'
    $projectPath
)

$resultsPath = $null
if ($Mode -eq 'Compile') {
    $runLabel = 'Compile'
    $logPath = Join-Path $artifactDirectory 'compile.log'
    $arguments = $commonArguments + @('-logFile', $logPath, '-quit')
}
else {
    $platform = if ($Mode -eq 'Test') { 'EditMode' } else { 'PlayMode' }
    $runLabel = "$platform tests"
    $slug = $platform.ToLowerInvariant()
    $logPath = Join-Path $artifactDirectory "$slug.log"
    $resultsPath = Join-Path $artifactDirectory "$slug-results.xml"
    Remove-Item -LiteralPath $resultsPath -Force -ErrorAction SilentlyContinue
    $arguments = $commonArguments + @(
        '-runTests'
        '-testPlatform'
        $platform
        '-testResults'
        $resultsPath
        '-logFile'
        $logPath
    )
}

Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $unityEditor -ArgumentList $arguments -PassThru -WindowStyle Hidden

# A hung batch run must fail the gate instead of blocking it forever.
$timeoutMinutes = 30
if (-not $process.WaitForExit($timeoutMinutes * 60 * 1000)) {
    $process.Kill($true)
    throw "Unity $runLabel did not finish within $timeoutMinutes minutes; the process was terminated. Log: $logPath"
}

$process.WaitForExit()
$exitCode = $process.ExitCode

function Get-FailureSummary {
    param([string] $ResultsFile)

    if (-not $ResultsFile -or -not (Test-Path -LiteralPath $ResultsFile -PathType Leaf)) {
        return $null
    }

    [xml] $document = Get-Content -LiteralPath $ResultsFile -Raw
    $failures = $document.SelectNodes("//test-case[@result!='Passed']")
    if (-not $failures -or $failures.Count -eq 0) {
        return $null
    }

    $lines = foreach ($case in $failures) {
        $message = $case.failure.message.'#cdata-section'
        if (-not $message) {
            $message = $case.failure.message
        }

        "  $($case.result): $($case.fullname)`n    $message"
    }

    return "Failing test cases:`n" + ($lines -join [Environment]::NewLine)
}

if ($exitCode -ne 0) {
    # Failed test names beat a raw log tail; fall back to the tail when the
    # run died before producing results.
    $summary = Get-FailureSummary -ResultsFile $resultsPath
    if (-not $summary) {
        $summary = if (Test-Path -LiteralPath $logPath) {
            (Get-Content -LiteralPath $logPath -Tail 80) -join [Environment]::NewLine
        }
        else {
            'Unity did not create a log file.'
        }
    }

    throw "Unity $runLabel failed with exit code $exitCode.`n$summary"
}

if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
    throw "Unity $runLabel succeeded without creating the expected log: $logPath"
}

$compileErrors = Select-String -LiteralPath $logPath -Pattern '\berror CS\d{4}\b|Compilation failed' -CaseSensitive
if ($compileErrors) {
    throw "Unity $runLabel logged compilation errors:`n$($compileErrors -join [Environment]::NewLine)"
}

if ($Mode -eq 'Compile') {
    # SignalRouter.Core/Protocol are consumed as NuGet packages restored by
    # NuGetForUnity from the local feed, not compiled from source by Unity.
    # Verify the restore delivered the assemblies the project depends on.
    $packagesRoot = Join-Path $projectPath 'Assets/Packages'
    foreach ($assemblyName in @('SignalRouter.Core.dll', 'SignalRouter.Protocol.dll')) {
        $restored = Get-ChildItem -LiteralPath $packagesRoot -Recurse -Filter $assemblyName -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $restored) {
            throw "NuGetForUnity did not restore $assemblyName under $packagesRoot; build and pack the SDK projects into the local feed before restoring."
        }
    }

    Write-Host "Unity $unityVersion compiled cleanly; SignalRouter.Core and SignalRouter.Protocol restored from the local feed."
    return
}

if (-not (Test-Path -LiteralPath $resultsPath -PathType Leaf)) {
    throw "Unity tests did not create the expected results file: $resultsPath"
}

[xml] $results = Get-Content -LiteralPath $resultsPath -Raw
$testRun = $results.'test-run'
if (-not $testRun) {
    throw "Unity test results have no test-run root: $resultsPath"
}

# Every gate suite must discover tests, pass them all, and skip nothing:
# a silently skipped or inconclusive case would read as green coverage.
$total = [int] $testRun.total
$passed = [int] $testRun.passed
$failed = [int] $testRun.failed
$skipped = [int] $testRun.skipped
$inconclusive = [int] $testRun.inconclusive
if ($total -lt 1 -or $passed -lt 1) {
    throw "Unity reported success but discovered no $runLabel."
}

if ($failed -ne 0 -or $skipped -ne 0 -or $inconclusive -ne 0 -or $testRun.result -ne 'Passed') {
    $summary = Get-FailureSummary -ResultsFile $resultsPath
    $detail = "total=$total passed=$passed failed=$failed skipped=$skipped inconclusive=$inconclusive result=$($testRun.result)"
    if ($summary) {
        $detail = "$detail`n$summary"
    }

    throw "Unity $runLabel did not pass: $detail"
}

Write-Host "Unity $unityVersion $runLabel passed: $total total."
