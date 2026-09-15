# Pack P3 — File Ownership Fence (ReplayIndex)

## Allowed
- `src/Ntilde.Replay/**` (index + seek)
- `src/Ntilde.Core/**` (event contracts only if strictly needed)
- `tests/**`
- `src/Ntilde.Cli/**` (optional: add a seek test command)

## Not Allowed
- `src/Ntilde.Rendering/**` (do not mix renderer work here)
- `src/Ntilde.Shell/**` (no command markers in this pack)
- Any remote/relay code

## Hot file rule
Avoid touching UI composition. Prefer CLI for validation if UI integration would cause conflicts.
