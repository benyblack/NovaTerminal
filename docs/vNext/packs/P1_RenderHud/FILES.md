# Pack P1 — File Ownership Fence (Render HUD)

You may modify **ONLY** the following areas:

## Allowed
- `src/Ntilde.Rendering/**`
- `src/Ntilde.App/**` (only command binding & minimal UI wiring)
- `tests/**` (add new tests; minimal updates to existing tests)

## Allowed (New Files Preferred)
- `src/Ntilde.Rendering/Overlays/**`
- `src/Ntilde.App/Features/**` (registration only)

## Not Allowed
- `src/Ntilde.Core/**`
- `src/Ntilde.Replay/**`
- Any recording/index format code
- Any global refactor across many files

## “Hot file” rule
If there is a central app startup/DI file that many features touch:
- add a **single registration call** only
- do not restructure the file
