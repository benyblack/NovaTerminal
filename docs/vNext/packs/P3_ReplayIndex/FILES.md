# Pack P3 — File Ownership Fence (ReplayIndex)

## Allowed
- `src/NovaTerminal.Replay/**` (index + seek)
- `src/NovaTerminal.Core/**` (event contracts only if strictly needed)
- `tests/**`
- `src/NovaTerminal.Cli/**` (optional: add a seek test command)

## Not Allowed
- `src/NovaTerminal.Rendering/**` (do not mix renderer work here)
- `src/NovaTerminal.Shell/**` (no command markers in this pack)
- Any remote/relay code

## Hot file rule
Avoid touching UI composition. Prefer CLI for validation if UI integration would cause conflicts.
