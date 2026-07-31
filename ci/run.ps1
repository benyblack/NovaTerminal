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

# Enforced as of #216: the 674 pre-existing whitespace violations that made this
# report-only have been swept, and `.gitattributes` now pins line endings so the check
# reaches the same verdict on Windows and Linux (it did not before - `.editorconfig`
# wants CRLF while git stores LF, so the answer depended on core.autocrlf).
#
# Scoped to `whitespace` deliberately. A bare `dotnet format` also runs the style and
# analyzer passes, whose diagnostics are #108's territory; mixing them in here would
# make this gate fail for reasons that have nothing to do with formatting.
Write-Host "=== FORMAT CHECK ==="
dotnet format whitespace --verify-no-changes
if ($LASTEXITCODE -ne 0) {
    Write-Host "FORMAT CHECK FAILED: run 'dotnet format whitespace' and commit the result."
    exit 1
}

Write-Host "CI SUCCESS"
