#!/usr/bin/env bash
# Wrapper around `dotnet build`/`dotnet test`/etc. that prevents long-lived MSBuild
# worker nodes and the dotnet MSBuild build server from outliving the invocation.
#
# Why this exists: `dotnet build` spawns daemons that inherit the caller's stdout/stderr
# handles. When a parent (test harness, CI runner, Claude Code's Bash tool) captures
# stdout via pipes, the daemons hold the write end of the pipe after the build exits,
# so ReadToEnd() never sees EOF and the parent hangs indefinitely. The hang typically
# surfaces in BuildCliShim because that target's nested `dotnet build` is the last to
# emit output.
#
# Usage: scripts/build.sh [args...]   # passed to `dotnet`, e.g. `build src/...` or `test`
#
# Defaults: `dotnet build` if no args given.

set -euo pipefail

export DOTNET_CLI_USE_MSBUILD_SERVER=0

if [ $# -eq 0 ]; then
    set -- build
fi

# Insert -nodeReuse:false immediately after the verb (build/test/publish/etc.) so it
# applies to the MSBuild driver, not as a project argument. `restore` and `run` are
# deliberately omitted: restore does no compilation so the flag is unnecessary, and
# `dotnet run`'s argument parser splits options across the run/build/app boundaries
# in ways that make a generic insert here unsafe.
verb="$1"
shift

# Sweep processes that lock this tree's build output before compiling: MCP servers that
# clients left running, and test hosts from an interrupted `test` run. On Windows those hold
# the DLLs open, so the next run fails with "file is in use" or sits there looking hung
# (#317) - the same sweep build.ps1 does, which this wrapper was missing entirely, so which
# wrapper you happened to use decided whether you got the protection.
#
# Windows-only by nature: this is a file-locking problem. Delegated to PowerShell because the
# match needs each process's command line to scope it to THIS tree. NOVA_KEEP_STALE_HOSTS=1
# opts out (a genuinely concurrent `test` run from this tree would otherwise be killed too).
sweep_stale_hosts() {
    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*) ;;
        *) return 0 ;;
    esac
    command -v powershell.exe >/dev/null 2>&1 || return 0

    local repo_root
    repo_root="$(cd "$(dirname "$0")/.." && pwd -W 2>/dev/null || cd "$(dirname "$0")/.." && pwd)"

    KEEP_STALE="${NOVA_KEEP_STALE_HOSTS:-0}" REPO_ROOT="$repo_root" powershell.exe -NoProfile -NonInteractive -Command '
        # pwd -W hands us forward slashes; process command lines carry backslashes, so a
        # -like match on the raw value would never fire and the sweep would be a silent no-op.
        $repoRoot = ($env:REPO_ROOT -replace "/", "\\")
        $keepStale = $env:KEEP_STALE -eq "1"
        try {
            $stale = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
                $_.CommandLine -and $_.CommandLine -like "*$repoRoot*" -and (
                    ($_.Name -eq "dotnet.exe" -and $_.CommandLine -like "*NovaTerminal.McpServer.dll*") -or
                    (-not $keepStale -and ($_.Name -eq "testhost.exe" -or $_.Name -like "*.Tests.exe"))
                )
            })
            foreach ($p in $stale) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
            if ($stale.Count -gt 0) {
                Write-Output ("build.sh: killed {0} stale process(es) locking this tree''s bin outputs: {1}." -f $stale.Count, (($stale | ForEach-Object { $_.Name }) -join ", "))
            }
        } catch {
            Write-Output "build.sh: stale-host sweep skipped ($($_.Exception.Message))"
        }
    ' 2>/dev/null || true
}

case "$verb" in
    build|test|publish|pack|msbuild|clean)
        sweep_stale_hosts
        exec dotnet "$verb" -nodeReuse:false "$@"
        ;;
    *)
        exec dotnet "$verb" "$@"
        ;;
esac
