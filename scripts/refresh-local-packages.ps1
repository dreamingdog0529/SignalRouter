#Requires -Version 7.0

# NuGetForUnity serves same-version packages from its cache and skips packages
# already present under Assets/Packages, but the local feed repacks
# SignalRouter.Core/Protocol at a fixed version on every build. Without an
# eviction step a restore silently pins the Unity project to a stale SDK build.
# Evict the SignalRouter entries from the cache and the Unity project so the
# next restore always installs the freshly packed assemblies.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$installRoot = Join-Path $root 'src/SignalRouter.Unity/Assets/Packages'
$cacheRoot = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'NuGet/Cache' } else { $null }

foreach ($packageId in @('SignalRouter.Core', 'SignalRouter.Protocol')) {
    if ($cacheRoot -and (Test-Path -LiteralPath $cacheRoot)) {
        Get-ChildItem -LiteralPath $cacheRoot -File -Filter "$packageId.*.nupkg" |
            Remove-Item -Force
    }

    if (Test-Path -LiteralPath $installRoot) {
        Get-ChildItem -LiteralPath $installRoot -Directory -Filter "$packageId.*" |
            ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
                $meta = "$($_.FullName).meta"
                if (Test-Path -LiteralPath $meta) {
                    Remove-Item -LiteralPath $meta -Force
                }
            }
    }
}

Write-Host 'Evicted cached SignalRouter packages; the next restore reinstalls them from the local feed.'
