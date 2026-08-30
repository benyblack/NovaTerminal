# .NET Terminal Logger Cursor-Line Design

## Problem

.NET 10's terminal logger redraws its progress area with `CSI Ps F` (Cursor Preceding Line), followed by carriage return and line feed. NovaTerminal currently does not handle CSI `F`, so the cursor remains on its current row and each refresh advances to another row. The elapsed-time text therefore forms a vertical staircase instead of updating in place.

## Design

Implement the standard CSI `E` (Cursor Next Line) and CSI `F` (Cursor Preceding Line) commands in `AnsiParser`. Both commands move vertically by `Ps`, defaulting zero or an omitted parameter to one, and set the cursor column to zero. Vertical movement follows the existing CSI `A`/`B` behavior: clamp to the active scrolling region when the cursor starts inside it, otherwise clamp to viewport bounds.

The change stays entirely in the VT parser. It does not alter PTY input, terminal-grid rendering, Command Assist, or application UI.

## Testing

Add deterministic parser tests that cover:

- CSI `E` and `F` default, explicit, and large distances.
- Column reset to zero.
- Scroll-region and viewport clamping.
- A reduced stream matching .NET 10's `CSI 1 F`, CR, LF refresh pattern, proving repeated updates overwrite one progress row instead of consuming new rows.

Run the focused regression tests, the complete VT test project, and a build before opening the pull request.
