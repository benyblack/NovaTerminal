# Pack P1 — File Ownership Fence (Render HUD)

You may modify **ONLY** the following areas:

## Allowed
- `src/NovaTerminal.Rendering/**`
- `src/NovaTerminal.App/**` (only command binding & minimal UI wiring)
- `tests/**` (add new tests; minimal updates to existing tests)

## Allowed (New Files Preferred)
- `src/NovaTerminal.Rendering/Overlays/**`
- `src/NovaTerminal.App/Features/**` (registration only)

## Not Allowed
- `src/NovaTerminal.Core/**`
- `src/NovaTerminal.Replay/**`
- Any recording/index format code
- Any global refactor across many files

## “Hot file” rule
If there is a central app startup/DI file that many features touch:
- add a **single registration call** only
- do not restructure the file
