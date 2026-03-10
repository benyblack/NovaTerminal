# Command Assist M3 Shell Integration Design

## Summary

M3 moves Command Assist from heuristic command capture toward structured shell lifecycle ingestion, starting with PowerShell first.

This design keeps Command Assist as an App-layer subsystem and preserves the existing M1/M2 UI, storage, ranking, and fallback behavior.

Hard boundaries:

- do not couple Command Assist to terminal rendering or grid content
- do not change PTY transport semantics
- do not refactor the VT parser broadly
- keep non-integrated shells working through the current heuristic path
- implement PowerShell first, with a generic contract that can later support bash, zsh, and fish

## Current State

Current repo seams already provide part of the needed lifecycle:

- `AnsiParser` already emits:
  - `OnWorkingDirectoryChanged` from OSC 7
  - `OnCommandStarted` from OSC 133;B
  - `OnCommandFinished` from OSC 133;D[;exitCode]`
- `TerminalPane` already translates parser events into pane-local state:
  - `CurrentWorkingDirectory`
  - `LastExitCode`
  - `CommandStarted`
  - `CommandFinished`
- `CommandAssistController` already:
  - tracks pane/session context
  - persists command history
  - patches exit code after command completion
  - falls back to heuristic capture from local input
- `RustPtySession` currently contains ad hoc PowerShell startup injection logic

The main architectural gap is that shell integration is not modeled explicitly. Structured shell lifecycle data is partially present, but provider selection, launch planning, bootstrap management, and fallback policy are not.

## Goals

- prefer structured shell lifecycle events over local-input heuristics when available
- improve prompt readiness, cwd accuracy, exit-code accuracy, and multiline capture
- support optional command duration when available
- keep heuristic fallback for unsupported or failed integrations
- keep PowerShell-first M3 low-friction for users

## Non-Goals

- bash/zsh/fish implementation in M3
- AI, docs/help, or fix surfaces
- terminal-grid rendering changes
- broad VT parser redesign
- profile-installing user modules as a required setup step

## Recommended Architecture

Add a new App-side shell integration subsystem under:

- `src/NovaTerminal.App/CommandAssist/ShellIntegration/Contracts`
- `src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell`
- `src/NovaTerminal.App/CommandAssist/ShellIntegration/Runtime`
- `src/NovaTerminal.App/CommandAssist/ShellIntegration/Assets`

Recommended core types:

- `IShellIntegrationProvider`
- `ShellIntegrationLaunchPlan`
- `ShellIntegrationEvent`
- `ShellIntegrationEventType`
- `ShellLifecycleTracker`
- `ShellIntegrationRegistry`
- `PowerShellShellIntegrationProvider`
- `PowerShellBootstrapBuilder`

### Provider Contract

`IShellIntegrationProvider` should answer:

- whether a given shell/profile is supported
- what launch-time adjustments are needed
- what bootstrap asset or inline command should be used
- which shell marker dialect is expected

This keeps shell-specific logic out of `TerminalPane` and avoids adding more hardcoded PowerShell behavior into `RustPtySession`.

### Launch Planning

The provider should produce a `ShellIntegrationLaunchPlan` containing:

- adjusted executable and arguments if needed
- bootstrap asset path or generated temporary script path
- an `IsIntegrated` flag
- optional metadata about marker capabilities:
  - prompt ready
  - command accepted
  - command completed
  - cwd changed
  - duration

### Lifecycle Tracking

`TerminalPane` should remain the per-pane owner of runtime state, but it should use a `ShellLifecycleTracker` that consumes parser/session signals and emits a normalized App-side lifecycle:

- prompt ready
- command accepted
- command completed
- cwd changed
- exit code changed
- duration available

This normalized feed should become the input to `CommandAssistController` when integration is active.

## Parser Strategy

Use the existing parser surface wherever possible.

Current parser support already covers:

- OSC 7 cwd
- OSC 133 command started
- OSC 133 command finished with optional exit code

If PowerShell integration needs prompt-ready or command-accepted markers that are not currently parsed, only add the minimal additional OSC handling required for those markers.

Guideline:

- additive parser support is acceptable
- parser rewrites are not

## PowerShell Strategy

PowerShell should be the first integrated shell because the app already contains PowerShell-specific launch behavior and the M3 prompt pack explicitly prioritizes it.

Recommended M3 behavior:

- generate a temporary bootstrap script at launch time
- invoke it through provider-managed launch planning
- emit structured markers for:
  - cwd changes
  - command accepted
  - command completed
  - exit code
  - prompt ready
  - duration if easy to capture

The existing hardcoded startup injection in `RustPtySession` should be reduced or moved behind the provider-driven launch path.

## Command Assist Changes

`CommandAssistController` should gain a structured lifecycle mode.

When integration is active:

- command persistence should come from shell-lifecycle events instead of Enter heuristics
- multiline command text should be accepted
- history entries should be stored with `Source = ShellIntegration`
- exit code should be persisted directly when available
- duration should be persisted when available

When integration is unavailable or unhealthy:

- current heuristic behavior remains active

## Data Model Updates

Recommended additions:

- `CommandCaptureSource.ShellIntegration`
- `CommandHistoryEntry.DurationMs`

Optional but useful:

- integration-active flag in pane or controller session context

The ranking pipeline should prefer structured entries when duplicate heuristic and structured copies exist for the same command.

## Settings and Configuration

Add minimal M3 settings:

- `CommandAssistShellIntegrationEnabled`
- `CommandAssistPowerShellIntegrationEnabled`

Later shells can share the generic gate or add shell-specific toggles if needed.

No Settings UI is required for the first M3 slice if defaults are safe and tests cover persistence.

## Data Flow

1. `TerminalPane` initializes a session.
2. `ShellIntegrationRegistry` selects a provider based on shell kind and profile.
3. The provider returns a launch plan.
4. The session launches with integration bootstrap if supported.
5. Shell markers are emitted to the terminal stream.
6. `AnsiParser` raises existing or minimally-extended OSC lifecycle callbacks.
7. `TerminalPane` feeds those into `ShellLifecycleTracker`.
8. `ShellLifecycleTracker` emits normalized events.
9. `CommandAssistController` stores structured history entries and updates ranking context.
10. If integration is inactive or fails, the controller continues to use heuristic capture.

## Error Handling and Fallback

Failures must degrade gracefully.

Examples:

- provider unavailable: use heuristic mode
- bootstrap creation fails: use heuristic mode
- expected markers absent: remain in heuristic mode for that pane/session
- partial marker coverage: use structured cwd/exit code where available and keep heuristic capture for command text if needed

The terminal itself must continue functioning even if shell integration fails completely.

## Risks

- duplicating history entries if both heuristic Enter capture and structured command-complete capture remain active at once
- leaking more shell-specific logic into `RustPtySession`
- overreaching parser changes for shell-specific protocols
- requiring user setup steps that reduce adoption
- breaking non-PowerShell launch behavior

## Mitigations

- gate heuristic persistence off when structured integration is confirmed active
- keep provider logic in App-layer shell integration classes
- only extend parser for the exact markers M3 needs
- generate bootstrap scripts automatically instead of requiring user profile edits
- keep fallback mode explicit and tested

## Test Strategy

Add tests for:

- provider selection and launch-plan generation
- bootstrap script generation
- parser handling for any newly added shell markers
- pane lifecycle tracking from parser events
- structured command persistence
- exit code and cwd propagation
- multiline command capture
- fallback when integration is disabled or markers never arrive
- duplicate suppression between heuristic and structured capture

Re-run existing:

- `OscShellIntegrationTests`
- alternate-screen tests
- Command Assist controller/ranking tests

## Proposed Implementation Sequence

1. Add failing tests for provider selection, launch planning, and structured lifecycle ingestion.
2. Add shell integration contracts and runtime registry.
3. Add PowerShell provider and bootstrap generation.
4. Move PowerShell-specific startup behavior behind the provider-driven path.
5. Extend parser support only if required for prompt-ready or command-accepted markers.
6. Add `ShellLifecycleTracker` and wire it in `TerminalPane`.
7. Extend Command Assist models for `ShellIntegration` source and optional duration.
8. Update `CommandAssistController` to prefer structured lifecycle capture.
9. Add duplicate suppression and heuristic fallback behavior.
10. Run targeted verification and document PowerShell setup expectations plus known gaps for bash/zsh/fish.

## Recommendation

Implement M3 with a provider-driven PowerShell integration path, minimal additive parser support, and explicit heuristic fallback.

That approach fixes the biggest remaining correctness gap in Command Assist without disturbing VT correctness, render behavior, or the current M1/M2 UX surface.
