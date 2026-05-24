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
# applies to the MSBuild driver, not as a project argument.
verb="$1"
shift
case "$verb" in
    build|test|publish|pack|restore|msbuild|clean|run)
        exec dotnet "$verb" -nodeReuse:false "$@"
        ;;
    *)
        exec dotnet "$verb" "$@"
        ;;
esac
