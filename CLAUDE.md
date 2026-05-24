# CLAUDE.md

## Build & test commands

**Always use the wrapper scripts, not raw `dotnet`:**

- Windows / PowerShell: `scripts/build.ps1 <args...>`
- Linux / macOS / Git Bash: `scripts/build.sh <args...>`

Examples:
- `scripts/build.ps1 build src/NovaTerminal.App`
- `scripts/build.ps1 test`
- `scripts/build.sh build -c Release src/NovaTerminal.App`

The wrappers pass `-nodeReuse:false` and set `DOTNET_CLI_USE_MSBUILD_SERVER=0`, which is required when stdout/stderr is captured by a parent (Claude Code's Bash tool, CI runners, test harnesses).

### Why this matters

A raw `dotnet build` invocation spawns long-lived daemons — the MSBuild worker node pool (24h `nodeReuse` default), the dotnet MSBuild build server, and `VBCSCompiler`. These daemons inherit the caller's stdout/stderr handles, so a parent process reading via pipes never sees EOF and hangs indefinitely. The hang typically appears stuck in `BuildCliShim` because that's the last target to emit output before silence.

`<UseSharedCompilation>false</UseSharedCompilation>` in `Directory.Build.props` already kills VBCSCompiler globally, and the `BuildCliShim` / `PublishCliShim` targets in `src/NovaTerminal.App/NovaTerminal.App.csproj` pass `-nodeReuse:false` to their nested `dotnet build`. But the **outer** invocation must also pass `-nodeReuse:false`; the project-level settings cannot influence the driver's node-pool startup. The wrapper scripts encode this.

If a build appears stuck in BuildCliShim and you reached for raw `dotnet build`: stop, kill the orphan MSBuild/dotnet daemons (`dotnet build-server shutdown` + Stop-Process on any stale `MSBuild.exe` / `dotnet.exe`), and re-run via the wrapper.
