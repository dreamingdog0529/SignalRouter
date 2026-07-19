#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Compile', 'Test')]
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

if ($Mode -eq 'Compile') {
    $logPath = Join-Path $artifactDirectory 'compile.log'
    $arguments = $commonArguments + @('-logFile', $logPath, '-quit')
}
else {
    $logPath = Join-Path $artifactDirectory 'editmode.log'
    $resultsPath = Join-Path $artifactDirectory 'editmode-results.xml'
    Remove-Item -LiteralPath $resultsPath -Force -ErrorAction SilentlyContinue
    $arguments = $commonArguments + @(
        '-runTests'
        '-testPlatform'
        'EditMode'
        '-testResults'
        $resultsPath
        '-logFile'
        $logPath
    )
}

Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $unityEditor -ArgumentList $arguments -PassThru -WindowStyle Hidden
$process.WaitForExit()
$exitCode = $process.ExitCode

if ($exitCode -ne 0) {
    $tail = if (Test-Path -LiteralPath $logPath) {
        (Get-Content -LiteralPath $logPath -Tail 80) -join [Environment]::NewLine
    }
    else {
        'Unity did not create a log file.'
    }

    throw "Unity $Mode failed with exit code $exitCode.`n$tail"
}

if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
    throw "Unity $Mode succeeded without creating the expected log: $logPath"
}

$compileErrors = Select-String -LiteralPath $logPath -Pattern '\berror CS\d{4}\b|Compilation failed' -CaseSensitive
if ($compileErrors) {
    throw "Unity $Mode logged compilation errors:`n$($compileErrors -join [Environment]::NewLine)"
}

if ($Mode -eq 'Compile') {
    $scriptAssemblies = Join-Path $projectPath 'Library/ScriptAssemblies'
    foreach ($assemblyName in @('SignalRouter.Core.dll', 'SignalRouter.Protocol.dll')) {
        $assemblyPath = Join-Path $scriptAssemblies $assemblyName
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
            throw "Unity compile did not produce $assemblyPath."
        }
    }

    Write-Host "Unity $unityVersion compiled SignalRouter.Core and SignalRouter.Protocol."
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

$total = [int] $testRun.total
$failed = [int] $testRun.failed
if ($total -lt 1) {
    throw 'Unity reported success but discovered no EditMode tests.'
}

if ($failed -ne 0 -or $testRun.result -ne 'Passed') {
    throw "Unity EditMode tests did not pass: total=$total failed=$failed result=$($testRun.result)"
}

Write-Host "Unity $unityVersion EditMode tests passed: $total total."
