#!/usr/bin/env pwsh
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
# Usage: scripts/build.ps1 [args...]   # passed to `dotnet`, e.g. `build src/...` or `test`
#
# Defaults: `dotnet build` if no args given.

$ErrorActionPreference = 'Stop'

$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

$dotnetArgs = @($args)
if ($dotnetArgs.Count -eq 0) {
    $dotnetArgs = @('build')
}

# Insert -nodeReuse:false immediately after the verb (build/test/publish/etc.) so it
# applies to the MSBuild driver, not as a project argument. `restore` and `run` are
# deliberately omitted: restore does no compilation so the flag is unnecessary, and
# `dotnet run`'s argument parser splits options across the run/build/app boundaries
# in ways that make a generic insert here unsafe.
$verbs = @('build','test','publish','pack','msbuild','clean')
if ($verbs -contains $dotnetArgs[0]) {
    $rest = @($dotnetArgs | Select-Object -Skip 1)
    $dotnetArgs = @($dotnetArgs[0], '-nodeReuse:false') + $rest

    # Kill stale NovaTerminal.McpServer processes before compiling. MCP clients
    # (Claude Desktop, Cowork, etc.) launch the server from this repo's bin output
    # and often leave it running, which locks the DLLs and fails the build with
    # "file is in use". Killing is always safe: clients respawn the server on the
    # next tool call. Scoped to servers launched from THIS repo tree only.
    # Note: $env:OS is not reliable here (some hosts spawn children with a stripped
    # environment), so use the runtime's own platform check.
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
        try {
            # Two families of leftover process lock this tree's build output:
            #   * MCP servers, which clients respawn on the next tool call, and
            #   * test hosts from an interrupted `test` run (testhost.exe, and xunit v3's
            #     own <Project>.Tests.exe), which nothing respawns and which make the next
            #     run fail with "file is in use" or sit there looking hung (#317).
            # Scoped to this tree, so a run in one worktree never touches another's.
            # Caveat worth knowing: a genuinely concurrent `test` run from THIS tree would
            # be killed too. That is the accepted trade - the silent-lock failure mode cost
            # hours of debugging, and NOVA_KEEP_STALE_HOSTS=1 opts out.
            $keepStale = $env:NOVA_KEEP_STALE_HOSTS -eq '1'
            $stale = @(Get-CimInstance Win32_Process -ErrorAction Stop |
                Where-Object {
                    $_.CommandLine -and
                    $_.CommandLine -like "*$repoRoot*" -and
                    (
                        ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*NovaTerminal.McpServer.dll*') -or
                        (-not $keepStale -and ($_.Name -eq 'testhost.exe' -or $_.Name -like '*.Tests.exe'))
                    )
                })
            foreach ($p in $stale) {
                Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            }
            if ($stale.Count -gt 0) {
                Write-Output "build.ps1: killed $($stale.Count) stale process(es) locking this tree's bin outputs: $(($stale | ForEach-Object { $_.Name }) -join ', ')."
            }
        } catch {
            Write-Output "build.ps1: stale-server sweep skipped ($($_.Exception.Message))"
        }
    }
}

& dotnet @dotnetArgs
exit $LASTEXITCODE
