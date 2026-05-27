# Command Assist PowerShell Integration

## Scope
Command Assist M3 integrates PowerShell:
- `pwsh.exe`
- `powershell.exe`

Bash, Zsh, and Fish providers exist alongside this one; see
[`CommandAssist_ShellIntegration_Gaps.md`](CommandAssist_ShellIntegration_Gaps.md).

## Activation
PowerShell shell integration is enabled when all of the following are true:
- `CommandAssistEnabled` is `true`
- `CommandAssistShellIntegrationEnabled` is `true`
- `CommandAssistPowerShellIntegrationEnabled` is `true`
- the active shell profile resolves to a supported PowerShell command
- the PowerShell launch arguments do not already force a user-supplied `-File` script

When those conditions are met, NovaTerminal injects a Command Assist bootstrap
script through the existing App-layer launch-plan path (`-File <bootstrap>`).

## Marker Flow
The PowerShell bootstrap emits the full structured lifecycle:

- `OSC 7`
  - working directory updates
- `OSC 133;A`
  - prompt ready (emitted from the wrapped `prompt` function)
- `OSC 133;C;<base64-utf8>`
  - command accepted; the user-entered buffer is base64-encoded so multiline
    submissions survive transit through the VT byte stream
- `OSC 133;D;<exitCode>;<durationMs>`
  - command finished; emitted from the next `prompt` invocation rather than
    a `PowerShell.OnIdle` engine event, so it fires exactly once per accepted
    command and reads a snapshot of `$?` / `$LASTEXITCODE` for that command

These markers feed the existing shell-integration tracker and Command Assist
controller; the terminal grid is not used as a source of truth for cwd or
command lifecycle metadata.

## Prompt Behavior
The bootstrap wraps the existing PowerShell `prompt` function instead of
replacing it.

That means:
- NovaTerminal emits cwd, completion-D, and prompt-ready markers before the
  user's prompt rendering
- the original prompt implementation is still invoked
- custom prompt content remains the shell's responsibility

If NovaTerminal cannot capture the existing prompt implementation, it falls
back to a simple prompt string so the shell session remains usable.

## Command Capture
PowerShell command text is captured at the shell boundary by wrapping
PSReadLine's `Enter` chord:

- the buffer is read via `[Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState`
- non-empty buffers are base64-encoded and emitted as `OSC 133;C;<payload>`
- `AcceptLine` is then called so the original Enter behavior runs

Exit code and duration enrichment come from the prompt-driven `OSC 133;D`
marker on the *next* prompt cycle. The Command Assist controller persists
the accepted-command entry on `C`, then patches the same entry's exit code
and duration on `D`.

## Fallback Behavior
Fallback to the heuristic capture path remains in place for:
- unsupported shells (no provider registered for the detected shell kind)
- sessions with shell integration disabled in settings
- PowerShell launches that already provide a user `-File` script
- partial or failed integration where structured markers never appear

## Operational Notes
- This integration is implemented in the App-layer shell-integration subsystem.
- It does not render suggestions into the terminal grid.
- It does not require VT or renderer coupling beyond the existing OSC event path.
- It auto-hides the assist UI in alternate-screen scenarios as before.
