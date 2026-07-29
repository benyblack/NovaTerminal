# Local full-CI rehearsal. Mirrors the GitHub workflow closely enough to catch
# breakage before pushing, without needing a runner.
#
# Every dotnet invocation goes through scripts/build.ps1 rather than calling `dotnet`
# directly. Raw `dotnet build` spawns MSBuild worker nodes and a build server that
# inherit this script's stdout/stderr; when a parent captures those pipes the handles
# outlive the build and the reader never sees EOF, so the whole thing hangs. The wrapper
# encodes -nodeReuse:false and DOTNET_CLI_USE_MSBUILD_SERVER=0. See CLAUDE.md and
# Directory.Build.props. (#174)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$build = Join-Path $PSScriptRoot "..\scripts\build.ps1"

Write-Host "=== TOOLING ==="
# `restore`/`--info` do no compilation, so the wrapper deliberately leaves them alone;
# calling dotnet directly here is safe and keeps the output honest about the real SDK.
dotnet --info
rustc --version
cargo --version

Write-Host "=== CLEAN ==="
& $build clean

Write-Host "=== RESTORE ==="
& $build restore

# NOTE: -warnaserror is deliberately absent. It was here while GitHub CI did not pass it
# (ci.yml builds without it), so this script enforced a stricter contract than CI and
# could fail on warnings that CI accepted. #108 owns re-enabling warnings-as-errors
# repo-wide, once the ~350 existing diagnostics are addressed; until then both paths
# build with the same flags.
Write-Host "=== BUILD RELEASE ==="
& $build build -c Release

Write-Host "=== TEST ==="
& $build test -c Release --no-build

Write-Host "=== REPLAY TESTS ==="
& $build test -c Release --filter Category=Replay

Write-Host "=== AOT PUBLISH ==="
& $build publish src/NovaTerminal.App/NovaTerminal.App.csproj `
    -c Release `
    -r win-x64 `
    -p:PublishAot=true `
    -p:StripSymbols=true

# Reported, not enforced. `dotnet format --verify-no-changes` currently fails on main
# with 649 pre-existing whitespace violations across 79 files (~480 of them in
# TerminalDrawOperation.cs and TerminalBuffer.ReflowEngine.cs alone), so gating on it
# would make this script permanently red and mask real failures after it. The sweep is
# tracked in #216; flip this back to a hard failure once it lands.
Write-Host "=== FORMAT CHECK (report only) ==="
dotnet format --verify-no-changes
if ($LASTEXITCODE -ne 0) {
    Write-Host "FORMAT CHECK: differences found (not failing the run - see comment above)."
}

Write-Host "CI SUCCESS"
