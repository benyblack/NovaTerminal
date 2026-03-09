# Command Assist M3.1 Closeout Design

## Goal
Close the remaining M3 gaps so PowerShell shell integration can be considered complete by the Command Assist spec: preserve the user's prompt behavior, make structured command boundaries trustworthy, add explicit multiline coverage, and commit the required integration docs.

## Scope
In scope:
- PowerShell prompt-preservation ergonomics
- trustworthy command-finished emission
- explicit multiline shell-integration coverage
- committed PowerShell setup notes
- committed known-gaps documentation for bash, zsh, and fish

Out of scope:
- bash, zsh, or fish implementation
- new UI modes
- VT/render/refactor work
- AI/help/fix surfaces

## Constraints
- Keep Command Assist as a separate subsystem.
- Keep UI concerns out of terminal core logic.
- Do not broaden VT/parser changes unless strictly required.
- Preserve current fallback behavior for unsupported or partially integrated shells.

## Current Gaps
1. The PowerShell bootstrap replaces `Global:prompt` with a hardcoded prompt string instead of preserving the user's current prompt behavior.
2. The `PowerShell.OnIdle` completion path can emit finish markers too broadly, which weakens the "trustworthy command boundaries" exit criterion.
3. Multiline handling is required by the spec, but there is no committed explicit shell-integration multiline test coverage.
4. The repo does not yet contain committed M3 setup notes and documented shell gaps.

## Recommended Approach
Preserve the current architecture and harden it.

The PowerShell bootstrap should wrap the user's prompt instead of replacing it with Nova-owned rendering. Structured markers should remain additive: emit cwd and prompt-ready markers, then invoke the original prompt logic and return its rendered value unchanged.

Command completion must be tied to an active accepted command. If no command has been accepted, the bootstrap should not emit a finish marker on idle. That keeps `CommandAccepted` and `CommandFinished` paired and prevents synthetic completion events from idle transitions.

The multiline requirement should be closed with explicit parser/controller/store coverage for multiline accepted command text. The M3.1 pass should prove that structured command text with embedded newlines survives decoding, ingestion, persistence, and later history use as a single entry.

The required documentation should be committed under `docs/command-assist/`, not left as local planning notes.

## Design

### 1. Prompt Preservation
In `PowerShellBootstrapBuilder`, capture the current prompt implementation before redefining `prompt`.

Recommended wrapper shape:
- save the original prompt scriptblock or command implementation
- redefine `prompt` to:
  - emit cwd marker
  - emit prompt-ready marker
  - invoke the saved original prompt
  - return exactly what the original prompt produced

Expected behavior:
- custom prompts continue to render as they do today
- right-aligned prompt ecosystems are not replaced by a hardcoded `PS ... >`
- Command Assist still receives prompt-ready and cwd markers before each prompt render

Failure handling:
- if prompt capture fails, return the existing safe fallback prompt only as a last resort
- do not crash the shell session because integration bootstrap logic fails

### 2. Trustworthy Command Completion
The bootstrap already sets command-start state when PSReadLine history accepts a line. M3.1 should make completion emission depend on that accepted-command state.

Required rules:
- `CommandFinished` can only be emitted if a command was previously accepted
- idle ticks without an active accepted command do nothing
- after emitting finish, clear the active command state
- a prompt-ready event alone must not manufacture a command completion

This keeps the structured lifecycle usable as the source of truth for integrated sessions and aligns with the M3 exit criterion that command boundaries are trustworthy.

### 3. Multiline Handling
Multiline command handling should remain based on the current OSC `133;C;<base64>` accepted-command path.

Requirements:
- multiline text must survive base64 decode exactly
- the controller must persist that accepted command as a single history entry
- finish enrichment must still update the same entry with exit code and duration
- fallback mode should remain unchanged for non-integrated shells

No new parser protocol is needed if the current `133;C` payload already carries the full multiline command.

### 4. Documentation Deliverables
Add committed docs:
- `docs/command-assist/CommandAssist_PowerShell_Integration.md`
- `docs/command-assist/CommandAssist_ShellIntegration_Gaps.md`

The setup note should explain:
- which shells are integrated in M3
- how PowerShell integration is activated
- what markers are emitted
- what fallback behavior looks like if integration is disabled or unavailable

The known-gaps note should explicitly state:
- bash/zsh/fish are not yet implemented
- current limitations of PowerShell integration
- areas deferred to later milestones

## Files Expected To Change

Code:
- `src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs`

Likely tests:
- `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellBootstrapBuilderTests.cs`
- `tests/NovaTerminal.Tests/OscShellIntegrationTests.cs`
- `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`
- `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellLifecycleTrackerTests.cs`

Docs:
- `docs/command-assist/CommandAssist_PowerShell_Integration.md`
- `docs/command-assist/CommandAssist_ShellIntegration_Gaps.md`

## Testing Strategy
- Add failing tests first for prompt preservation behavior in the bootstrap output.
- Add failing tests first for finish emission requiring an active accepted command.
- Add failing tests first for multiline structured command persistence.
- Rerun existing shell-integration tests.
- Rerun relevant PowerShell prompt/cursor preservation tests to make sure integration hardening does not conflict with terminal behavior expectations.

## Risks
- PowerShell prompt wrapping can be fragile if prompt capture is implemented too narrowly.
- Prompt ecosystems that depend on complex prompt functions may require a scriptblock-based wrapper rather than string-based replacement.
- Over-hardening completion logic could accidentally suppress valid finish events if command state is not tracked carefully.

## Success Criteria
- Integrated PowerShell sessions preserve their effective prompt rendering.
- Structured finish events only occur for real accepted commands.
- Multiline shell-integrated command capture is explicitly covered by tests.
- Required M3 PowerShell setup and shell-gap docs are committed.
- Non-integrated shells still use the current fallback behavior.
