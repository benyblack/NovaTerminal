#!/usr/bin/env pwsh
# Build NovaTerminal, mirror the fresh output to a fixed sidecar directory, and launch it
# from there. Also mirrors the MCP dev-companion server, which is launched separately by an
# MCP client rather than by this script.
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
# NovaTerminal.McpServer gets the same treatment (#211). It is a long-lived process started
# by whatever MCP client is configured, and while it ran from the repo tree it held
# NovaTerminal.AgentHost.Contracts.dll open — which made *every* full repo build fail with
# MSB3027/MSB3021 on the McpServer copy step, whether or not the app was running. Point the
# MCP client at the sidecar path this script prints and the repo stays buildable.
#
# The DLL-lock problem this solves is Windows-specific, but the script runs on Linux/macOS too
# (rsync/Copy-Item fallback for mirroring, no .exe suffix) so it's usable as a generic
# always-fresh launcher anywhere.
#
# Usage:
#   scripts/run-sidecar.ps1                 # Debug build, build + mirror + launch
#   scripts/run-sidecar.ps1 -Configuration Release
#   scripts/run-sidecar.ps1 -NoBuild        # skip the build; just mirror current output + launch
#   scripts/run-sidecar.ps1 -SkipMcpServer  # app only; don't build/mirror the MCP server
#   scripts/run-sidecar.ps1 -SidecarRoot /some/path   # override sidecar location

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$TargetFramework = 'net10.0',

    [switch]$NoBuild,

    # The MCP server mirror is opt-out rather than opt-in: leaving it stale is how the repo
    # ends up locked again, which is the whole point of #211.
    [switch]$SkipMcpServer,

    # Default sidecar lives outside the repo so it never collides with repo bin/obj globbing,
    # IDE file watchers, or `git status`. $env:LOCALAPPDATA is Windows-only; fall back to the
    # XDG-ish data dir elsewhere so the script doesn't throw on a null Join-Path argument.
    [string]$SidecarRoot = $(
        if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'NovaTerminal-sidecar' }
        else { Join-Path $HOME '.local/share/NovaTerminal-sidecar' }
    )
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
# Build path segments with Join-Path (no embedded separators) so they're correct on every OS.
$appProject = Join-Path $repoRoot 'src' 'NovaTerminal.App'
$sourceDir = Join-Path $appProject 'bin' $Configuration $TargetFramework

$mcpProject = Join-Path $repoRoot 'src' 'NovaTerminal.McpServer'
$mcpSourceDir = Join-Path $mcpProject 'bin' $Configuration $TargetFramework
# Separate destination from the app's: both outputs contain the same shared assemblies (VT,
# Replay, AgentHost.Contracts...), and robocopy /MIR deletes anything not in its source - so
# mirroring both into one directory would have each delete the other's private files.
$mcpDestDir = Join-Path $SidecarRoot 'McpServer' $Configuration $TargetFramework

# Mirrors $from onto $to. Returns $true on success. Never throws: callers decide whether a
# failed mirror is fatal.
function Sync-Directory {
    param([string]$From, [string]$To)

    New-Item -ItemType Directory -Force -Path $To | Out-Null

    if ($IsWindows) {
        # robocopy /MIR mirrors source to dest (adds new, updates changed, deletes stale). Exit
        # codes 0-7 are success (8+ are real errors); robocopy uses bit flags, not 0=ok.
        robocopy $From $To /MIR /NJH /NJS /NDL /NP /R:2 /W:1 | Out-Null
        return $LASTEXITCODE -lt 8
    }

    if (Get-Command rsync -ErrorAction SilentlyContinue) {
        # Trailing slashes => mirror contents of source into dest; --delete removes stale files.
        rsync -a --delete "$From/" "$To/"
        return $LASTEXITCODE -eq 0
    }

    # No rsync: clear dest and recopy. Not incremental, but correct.
    #
    # The removal deliberately does NOT suppress errors. It used to (inherited from the
    # inline version of this code), which was harmless while nothing inspected the outcome -
    # but now that this function reports success to callers, a failed removal followed by a
    # successful copy would return $true with stale destination-only files still present.
    # That is a mirror reported as fresh when it is not, which is the exact failure this
    # script exists to prevent. Let it throw into the catch below.
    try {
        if (Test-Path $To) { Remove-Item -Recurse -Force (Join-Path $To '*') }
        Copy-Item -Recurse -Force (Join-Path $From '*') $To
        return $true
    }
    catch {
        Write-Warning "[sidecar] Mirror to $To failed: $($_.Exception.Message)"
        return $false
    }
}

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

    if (-not $SkipMcpServer) {
        Write-Host "[sidecar] Building NovaTerminal.McpServer ($Configuration)..." -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'build.ps1') build $mcpProject -c $Configuration
        if ($LASTEXITCODE -ne 0) {
            # Non-fatal: a stale MCP sidecar must not stop you launching the app. This build
            # can legitimately fail while an MCP server started from the *repo* path is still
            # running and holding AgentHost.Contracts.dll - which is the state #211 exists to
            # get out of.
            Write-Warning "[sidecar] MCP server build failed (exit $LASTEXITCODE); its sidecar copy may be stale."
            Write-Warning "[sidecar] If an MCP server is running from the repo tree, stop it (or repoint the client at the sidecar) and re-run."
            $SkipMcpServer = $true
        }
    }
}

if (-not (Test-Path $sourceDir)) {
    Write-Error "[sidecar] Build output not found: $sourceDir. Run without -NoBuild first."
    exit 1
}

$destDir = Join-Path $SidecarRoot $Configuration $TargetFramework

Write-Host "[sidecar] Mirroring fresh output -> $destDir" -ForegroundColor Cyan
if (-not (Sync-Directory -From $sourceDir -To $destDir)) {
    Write-Error "[sidecar] Mirror failed for the app output."
    exit 1
}

if (-not $SkipMcpServer) {
    if (Test-Path $mcpSourceDir) {
        Write-Host "[sidecar] Mirroring MCP server -> $mcpDestDir" -ForegroundColor Cyan
        if (Sync-Directory -From $mcpSourceDir -To $mcpDestDir) {
            $mcpDll = Join-Path $mcpDestDir 'NovaTerminal.McpServer.dll'
            Write-Host "[sidecar] MCP client should point at: dotnet `"$mcpDll`"" -ForegroundColor DarkGray
        }
        else {
            # Expected whenever an MCP client already has a server running from this sidecar:
            # the running process holds its own DLLs. The repo stays buildable either way,
            # which is the property that matters - the sidecar copy just lags until the client
            # is restarted.
            Write-Warning "[sidecar] Could not refresh the MCP sidecar (likely a running server holding its DLLs)."
            Write-Warning "[sidecar] Restart your MCP client to pick up a fresh build. The repo build is unaffected."
        }
    }
    else {
        Write-Warning "[sidecar] MCP server output not found: $mcpSourceDir (skipping its mirror)."
    }
}

$exeName = if ($IsWindows) { 'NovaTerminal.exe' } else { 'NovaTerminal' }
$exe = Join-Path $destDir $exeName
if (-not (Test-Path $exe)) {
    Write-Error "[sidecar] Expected executable not found after mirror: $exe"
    exit 1
}

# Zip/copy doesn't always preserve the execute bit on Unix; ensure the host is runnable.
if (-not $IsWindows) {
    chmod +x $exe 2>$null
}

$builtAt = (Get-Item $exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
Write-Host "[sidecar] Launching $exe (built $builtAt)" -ForegroundColor Green
# Launch detached so this shell returns immediately and the repo stays free to build/test.
Start-Process -FilePath $exe -WorkingDirectory $destDir
