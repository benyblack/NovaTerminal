# NovaTerminal agent rules

Architecture
- Do not modify VT parsing/rendering unless explicitly required.
- Command Assist must be a separate subsystem.
- Keep UI concerns out of terminal core logic.

UI
- Use Avalonia for all Command Assist UI.
- Do not render suggestions inside terminal grid content.
- Auto-hide assist UI in alternate-screen/fullscreen TUI mode.

Implementation
- Prefer additive changes over refactors.
- Keep interfaces small and explicit.
- Preserve current performance-sensitive paths.

Testing
- Add tests for new domain/services code.
- Prefer deterministic tests.
- Avoid fragile UI snapshot dependencies unless already established.