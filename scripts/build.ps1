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
}

& dotnet @dotnetArgs
exit $LASTEXITCODE
