#!/usr/bin/env pwsh
# Build NovaTerminal, mirror the fresh output to a fixed sidecar directory, and launch it
# from there.
#
# Why this exists: running the app holds a lock on its DLLs, so a `dotnet build` of the main
# repo fails with "file in use" while an instance is running. The workaround has been to copy
# bin/ by hand and run the copy side-by-side — but a hand copy goes stale silently, and you
# end up chasing bugs that were already fixed (see the GlobalHotkey crash incident: a stale
# "net10.0 - Copy" binary still had the pre-fix WndProc bug).
#
# This script removes the manual step: every launch rebuilds and re-mirrors, so the sidecar
# is always current, while the main repo bin stays unlocked for `dotnet build`/`dotnet test`.
# The running build is identifiable in debug.log via the "Build: sha=... built=... path=..."
# line (the sidecar path makes it obvious you're on the sidecar, not the repo output).
#
# Usage:
#   scripts/run-sidecar.ps1                 # Debug build, build + mirror + launch
#   scripts/run-sidecar.ps1 -Configuration Release
#   scripts/run-sidecar.ps1 -NoBuild        # skip the build; just mirror current output + launch
#   scripts/run-sidecar.ps1 -SidecarRoot D:\nova-sidecar   # override sidecar location

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$TargetFramework = 'net10.0',

    [switch]$NoBuild,

    # Default sidecar lives outside the repo so it never collides with repo bin/obj globbing,
    # IDE file watchers, or `git status`.
    [string]$SidecarRoot = (Join-Path $env:LOCALAPPDATA 'NovaTerminal-sidecar')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\NovaTerminal.App'
$sourceDir = Join-Path $appProject "bin\$Configuration\$TargetFramework"

if (-not $NoBuild) {
    Write-Host "[sidecar] Building NovaTerminal.App ($Configuration)..." -ForegroundColor Cyan
    # Use the build wrapper so MSBuild/dotnet daemons don't outlive the build and hang on
    # captured stdout (see CLAUDE.md). The main repo bin is free to build because the
    # currently-running instance is the sidecar copy, not this output.
    & (Join-Path $PSScriptRoot 'build.ps1') build $appProject -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Error "[sidecar] Build failed (exit $LASTEXITCODE). Not launching."
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path $sourceDir)) {
    Write-Error "[sidecar] Build output not found: $sourceDir. Run without -NoBuild first."
    exit 1
}

$destDir = Join-Path $SidecarRoot "$Configuration\$TargetFramework"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

Write-Host "[sidecar] Mirroring fresh output -> $destDir" -ForegroundColor Cyan
# robocopy /MIR mirrors source to dest (adds new, updates changed, deletes stale). Exit codes
# 0-7 are success (8+ are real errors); robocopy uses bit flags, not the usual 0=ok convention.
robocopy $sourceDir $destDir /MIR /NJH /NJS /NDL /NP /R:2 /W:1 | Out-Null
$rc = $LASTEXITCODE
if ($rc -ge 8) {
    Write-Error "[sidecar] robocopy failed (exit $rc)."
    exit $rc
}

$exe = Join-Path $destDir 'NovaTerminal.exe'
if (-not (Test-Path $exe)) {
    Write-Error "[sidecar] Expected executable not found after mirror: $exe"
    exit 1
}

$builtAt = (Get-Item $exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
Write-Host "[sidecar] Launching $exe (built $builtAt)" -ForegroundColor Green
# Launch detached so this shell returns immediately and the repo stays free to build/test.
Start-Process -FilePath $exe -WorkingDirectory $destDir
