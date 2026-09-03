# NovaTerminal agent rules

Read `CLAUDE.md` first — it carries the build/test commands and the reason they
are not negotiable. `CONTRIBUTING.md` covers CI lanes and test categories;
`docs/MODULE_OWNERSHIP.md` is the authority on which assembly owns which
invariant.

## Build and test

- **Never call raw `dotnet build` or `dotnet test`.** Use `scripts/build.ps1`
  (Windows) or `scripts/build.sh` (Linux/macOS/Git Bash). The wrappers pass
  `-nodeReuse:false` and set `DOTNET_CLI_USE_MSBUILD_SERVER=0`; without them,
  MSBuild daemons inherit your captured stdout/stderr and the build hangs
  forever, usually looking stuck in `BuildCliShim`. If that happens, run
  `dotnet build-server shutdown`, kill stale `MSBuild.exe` / `dotnet.exe`, and
  retry through the wrapper.
- Run test projects individually. A whole-solution run takes tens of minutes
  because of the headless Avalonia suite.
- For `tests/NovaTerminal.App.Tests`, always pass `--blame-hang-timeout 5m`, run
  the `Lane!=PlatformBoot` and `Lane=PlatformBoot` filters separately (they must
  not share a process), and never run two invocations concurrently against the
  same results directory.

## Architecture

- Do not modify VT parsing/rendering unless explicitly required.
- Keep UI concerns out of terminal core logic. VT is a leaf: no Avalonia, no
  Skia, no native interop, no I/O.
- `CommandAssist`, `Backup`, `VtContract` and `AgentHost.Contracts` have zero
  project references and must keep it that way — that is what keeps
  `NovaTerminal.McpServer` free of a path into `App`, `VT`, `Pty` or
  `Rendering`. Adding a reference to any of them breaks an architecture test.
- Command Assist must stay a separate subsystem; the App owns only its views.

## UI

- Use Avalonia for all Command Assist UI.
- Do not render suggestions inside terminal grid content.
- Auto-hide assist UI in alternate-screen/fullscreen TUI mode.
- A new `TerminalSettings` field needs three other edits or it silently does
  nothing: the `TerminalPane.ApplySettings` effective-settings whitelist, and
  the `McpServer` `SettingsTools` schema plus its validators (two gating
  drift-guard tests cover the latter).

## Implementation

- Prefer additive changes over refactors.
- Keep interfaces small and explicit.
- Preserve current performance-sensitive paths.

## Testing

- Add tests for new domain/services code.
- Prefer deterministic tests.
- Avoid fragile UI snapshot dependencies unless already established.
- A flaky test "fixed" by raising a timeout deserves suspicion. If a failure
  burns the full ceiling rather than landing near it, the cause is an
  unreachable condition — a data race, a lost signal — not load.
