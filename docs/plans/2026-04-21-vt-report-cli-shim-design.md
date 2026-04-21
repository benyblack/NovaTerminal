# VT Report CLI Shim Design

**Goal:** Make `NovaTerminal.exe --vt-report` and `NovaTerminal.exe --vt-report --json` produce visible console output from a shell without changing the GUI app's normal `WinExe` behavior.

## Problem

`NovaTerminal.App` is built as `WinExe`. The current `--vt-report` implementation runs before Avalonia startup, but when the GUI binary is launched from PowerShell or `dotnet run`, its `Console.Out`/`Console.Error` output is not reliably visible. The feature works logically, but the primary user-facing invocation path appears silent.

## Constraints

- Do not change VT, parser, buffer, renderer, or runtime probing behavior.
- Keep `NovaTerminal.App` as the GUI entry point for normal app startup.
- Preserve `NovaTerminal.exe --vt-report` as the public command.
- Keep the implementation deterministic and release-friendly.
- Avoid adding Windows-only console attach logic to the GUI app if a cleaner boundary is available.

## Recommended Approach

Add a small console-side shim, `NovaTerminal.Cli`, and have `NovaTerminal.App` forward CLI-only modes to it.

### Why this approach

- Preserves the existing user command surface.
- Keeps GUI startup behavior isolated from console behavior.
- Avoids `AttachConsole` / `AllocConsole` interop in the app process.
- Scales to future CLI-only commands if more lightweight diagnostics are added later.
- Keeps the VT report generation and output logic shared rather than duplicated.

## Architecture

### 1. Shared CLI command layer

Extract the VT report command logic into a small shared service that:

- detects whether the incoming arguments represent a supported CLI-only mode
- renders output to supplied `TextWriter` instances
- returns an exit code

This logic should be free of Avalonia dependencies and reusable from both executables.

### 2. New console shim project

Add `src/NovaTerminal.Cli` as a regular console app (`Exe`) that:

- references the shared CLI command layer
- runs the CLI command directly
- writes to normal stdout/stderr
- returns deterministic exit codes

### 3. GUI app forwarding

Update `src/NovaTerminal.App/Program.cs` so that:

- if args are not a supported CLI-only mode, startup proceeds exactly as today
- if args are a supported CLI-only mode, the app launches `NovaTerminal.Cli.exe` with the same args, waits for it to exit, and mirrors its exit code

The GUI app should not attempt to render report text itself in this path.

### 4. Build and packaging

Ensure the CLI shim is available beside the app binary for:

- local `dotnet build` / `dotnet run`
- publish output

The app-side launcher should resolve the sibling CLI executable from the current app directory.

## Command Flow

For `NovaTerminal.exe --vt-report`:

1. `NovaTerminal.App` starts.
2. `Program.Main` checks args.
3. The app recognizes a CLI-only mode.
4. The app resolves and launches `NovaTerminal.Cli.exe --vt-report`.
5. The CLI shim prints the summary to stdout.
6. The GUI app exits with the same code.

For `NovaTerminal.exe --vt-report --json`, the same flow applies, but the CLI shim prints the embedded JSON artifact.

## Error Handling

- If the CLI shim executable is missing, the app should print a short actionable error to stderr if possible and return a non-zero exit code.
- If the shared CLI handler rejects arguments, the CLI shim should print usage and return the existing error code behavior.
- If the embedded VT report resource is missing or invalid, the CLI shim should surface the same existing VT report load failure message.

## Testing Strategy

### Unit / service tests

- Detect CLI-only argument sets correctly.
- Reject unsupported argument sets cleanly.
- Verify launch command construction for the GUI-to-CLI handoff.

### CLI tests

- `NovaTerminal.Cli --vt-report` prints the human-readable summary.
- `NovaTerminal.Cli --vt-report --json` prints parseable JSON.
- Existing artifact parity test remains intact.

### App tests

- GUI app startup path does not forward when no CLI-only args are present.
- GUI app forwarding path resolves the sibling CLI shim and propagates the exit code.

## Alternatives Considered

### Windows console attach in `WinExe`

Pros:
- fewer files and projects
- direct fix for current PowerShell usage

Cons:
- Windows-specific
- adds low-level console lifecycle logic to the GUI app entry path
- more fragile around redirected output, IDE launches, and future CLI growth

### Change `NovaTerminal.App` to `Exe`

Pros:
- simplest for console output visibility

Cons:
- changes the subsystem and startup behavior of the main GUI app
- riskier and broader than needed

## Decision

Implement the console shim and GUI handoff. It is the cleanest small change that preserves the public command while keeping GUI and CLI concerns separate.
