# Pack P2 — File Ownership Fence (Snapshot Export)

## Allowed
- `src/Ntilde.Core/**` (snapshot model + serialization only)
- `src/Ntilde.Rendering/**` (PNG render/export path only)
- `src/Ntilde.Cli/**` OR `src/Ntilde.App/**` (pick ONE minimal trigger surface)
- `tests/**`

## Not Allowed
- `src/Ntilde.Replay/**` (no replay/index changes in this pack)
- Any shell integration work
- Any remote/relay code

## Hot file rule
Avoid changing large app composition files.
Prefer adding a new command and registering it in a single registry location if available.
