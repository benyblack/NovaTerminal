# Command Assist PowerShell Integration

## Scope
Command Assist M3 integrates PowerShell first:
- `pwsh.exe`
- `powershell.exe`

Other shells still use the existing fallback path described below.

## Activation
PowerShell shell integration is enabled when all of the following are true:
- `CommandAssistEnabled` is `true`
- `CommandAssistShellIntegrationEnabled` is `true`
- `CommandAssistPowerShellIntegrationEnabled` is `true`
- the active shell profile resolves to a supported PowerShell command
- the PowerShell launch arguments do not already force a user-supplied `-File` script

When those conditions are met, NovaTerminal prepends a Command Assist bootstrap script through the existing App-layer launch-plan path.

## Marker Flow
The current PowerShell bootstrap emits these markers:

- `OSC 7`
  - working directory updates
- `OSC 133;A`
  - prompt ready
- `OSC 133;D;<exitCode>;<durationMs>`
  - command finished with exit code and duration after an accepted PowerShell submission reaches the idle transition

These markers are consumed by the existing shell-integration tracker and Command Assist controller. The terminal grid is not used as the source of truth for cwd or finish metadata.

## Prompt Behavior
The bootstrap wraps the existing PowerShell `prompt` function instead of replacing it with a Nova-owned prompt body.

That means:
- NovaTerminal emits cwd and prompt-ready markers before prompt rendering
- the original prompt implementation is still invoked
- custom prompt content remains the shell's responsibility

If NovaTerminal cannot capture the existing prompt implementation, it falls back to a simple prompt string so the shell session remains usable.

## Command Capture
For the current PowerShell integration path:
- command text still comes from the heuristic Enter-based capture path
- exit code and duration enrichment come from structured finish events
- shell integration continues to supply cwd and prompt-ready state

If shell integration is configured but no structured accepted-command marker is present, Command Assist keeps heuristic capture active for that pane.

## Fallback Behavior
Fallback remains in place for:
- unsupported shells
- sessions with shell integration disabled in settings
- PowerShell launches that already provide a user `-File` script
- partial or failed integration where structured markers never appear
- current PowerShell sessions for command text capture, because the bootstrap does not yet emit safe accepted-command markers

In those cases, Command Assist continues using the heuristic capture path from earlier milestones.

## Operational Notes
- This integration is implemented in the App-layer shell-integration subsystem.
- It does not render suggestions into the terminal grid.
- It does not require VT or renderer coupling beyond the existing OSC event path.
- It auto-hides the assist UI in alternate-screen scenarios as before.
