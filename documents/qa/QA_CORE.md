# QA: Core VT/ANSI Correctness

**Objective**: Ensure the parser and buffer state accurately reflect the incoming byte stream without OS-specific behavioral drift.

## 1. Parser Invariants & Sequences
- **Action**: Run the `NovaTerminal.Tests` unit test suite.
- **Verification**: `AnsiParser` should correctly identify and dispatch ESC, CSI, OSC, and DEC sequences without corruption.
- **Mouse Reporting**: Verify `SGR` mouse tracking (CSI < ... m/M). Use a tool like `vttest` or `mouse-test`.
- **Primary/Secondary DA**: Verification of `CSI > c` and `CSI c` responses for terminal identification.

## 2. Color & Style Modes
- **16/256 Color**: Verify standard ANSI colors and extended 256-color palettes (`CSI 38;5;Nm`).
- **TrueColor (RGB)**: Verify 24-bit color support (`CSI 38;2;R;G;Bm`).
- **Style Stacking**: Ensure bold, italic, underline, and strikethrough can be applied simultaneously without clearing each other.

## 3. Alternate Screen Isolation
- **Action**: Open an interactive tool like `vim` or `htop`.
- **Expected**: The terminal switches to a clean alternate buffer.
- **Action**: Exit the tool.
- **Expected**: The main buffer is restored byte-for-byte, including cursor position and scrollback.
- **Check**: Ensure no leakage from the alt screen into the main scrollback buffer.

## 4. Security & Safety
- **Bracketed Paste**: Enable bracketed paste (`CSI ? 2004 h`), paste multiline text, and verify it is wrapped in start/end sequences.
- **Large Report Protection**: Verify that malicious or massive terminal reports (e.g., status reports) do not hang the parser.
