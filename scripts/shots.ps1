#!/usr/bin/env pwsh
# Builds and runs the screenshot harness. Uses scripts/build.ps1 rather than raw `dotnet`
# for the reason documented in CLAUDE.md: raw dotnet build leaves MSBuild daemons holding
# the caller's stdout and hangs anything reading via pipes.

$ErrorActionPreference = 'Stop'

& "$PSScriptRoot/build.ps1" build -c Release tools/NovaTerminal.Shots
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $PSScriptRoot '../tools/NovaTerminal.Shots/bin/Release/net10.0/NovaTerminal.Shots.dll'
& dotnet $dll @args
exit $LASTEXITCODE
