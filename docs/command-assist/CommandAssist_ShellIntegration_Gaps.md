# Command Assist Shell Integration Gaps

## Implemented In M3
- generic shell integration contract
- App-layer launch-plan selection
- PowerShell-first bootstrap integration
- structured prompt-ready, command-finished, and cwd events for PowerShell
- structured exit-code and duration enrichment for command history
- heuristic fallback when structured integration is unavailable or not yet confirmed

## Not Yet Implemented
- bash integration
- zsh integration
- fish integration

Those shells still rely on the heuristic Command Assist path.

## Current PowerShell Limitations
- integration currently depends on the bootstrap script being injected through the launch plan
- if a user already launches PowerShell with a custom `-File` script, NovaTerminal does not claim structured integration for that session
- prompt preservation is best-effort and depends on wrapping the existing `prompt` command successfully
- command text capture still falls back to the heuristic Enter-based path
- the bootstrap does not currently emit accepted-command markers because earlier attempts were causing visible prompt/input artifacts
- command-finished timing is based on PowerShell idle transitions after accepted commands, not a shell-native explicit completion event

## Deferred Follow-Up Areas
- first-class bash/zsh/fish providers
- richer shell-specific prompt contracts beyond the current wrapper approach
- deeper multiline editing semantics for shells without structured accepted-command events
- additional setup UX in settings or profile surfaces

## Non-Goals Of M3
- AI assistance
- help/fix/documentation surfaces from later milestones
- terminal-grid inline suggestion rendering
- VT/render-core refactors
