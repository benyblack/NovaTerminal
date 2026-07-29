#!/usr/bin/env bash
# Local full-CI rehearsal. Mirrors the GitHub workflow closely enough to catch breakage
# before pushing, without needing a runner.
#
# Every dotnet invocation goes through scripts/build.sh rather than calling `dotnet`
# directly. Raw `dotnet build` spawns MSBuild worker nodes and a build server that
# inherit this script's stdout/stderr; when a parent captures those pipes the handles
# outlive the build and the reader never sees EOF, so the whole thing hangs. The wrapper
# encodes -nodeReuse:false and DOTNET_CLI_USE_MSBUILD_SERVER=0. See CLAUDE.md and
# Directory.Build.props. (#174)
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD="$SCRIPT_DIR/../scripts/build.sh"

echo "=== TOOLING ==="
# `restore`/`--info` do no compilation, so the wrapper deliberately leaves them alone;
# calling dotnet directly here is safe and keeps the output honest about the real SDK.
dotnet --info
rustc --version
cargo --version

echo "=== CLEAN ==="
"$BUILD" clean

echo "=== RESTORE ==="
"$BUILD" restore

# NOTE: -warnaserror is deliberately absent. It was here while GitHub CI did not pass it
# (ci.yml builds without it), so this script enforced a stricter contract than CI and
# could fail on warnings that CI accepted. #108 owns re-enabling warnings-as-errors
# repo-wide, once the ~350 existing diagnostics are addressed; until then both paths
# build with the same flags.
echo "=== BUILD RELEASE ==="
"$BUILD" build -c Release

echo "=== TEST ==="
"$BUILD" test -c Release --no-build

echo "=== REPLAY TESTS ==="
"$BUILD" test -c Release --filter Category=Replay

# Reported, not enforced. `dotnet format --verify-no-changes` currently fails on main
# with 649 pre-existing whitespace violations across 79 files (~480 of them in
# TerminalDrawOperation.cs and TerminalBuffer.ReflowEngine.cs alone), so gating on it
# would make this script permanently red and mask real failures after it. The sweep is
# tracked in #216; flip this back to a hard failure once it lands.
echo "=== FORMAT CHECK (report only) ==="
if ! dotnet format --verify-no-changes; then
    echo "FORMAT CHECK: differences found (not failing the run - see comment above)."
fi

echo "CI SUCCESS"
