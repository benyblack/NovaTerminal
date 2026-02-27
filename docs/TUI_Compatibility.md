# TUI Compatibility & Text Reflow

This document details the specific heuristics and character handling logic implemented in NovaTerminal to ensure compatibility with complex Text User Interface (TUI) applications like Midnight Commander (MC), htop, and vim.

## 1. Text Reflow Heuristics

To support responsive resizing while preserving TUI layouts (borders, panels), NovaTerminal employs a "Smart Reflow" strategy.

### The Challenge
When a terminal is resized to be wider, hard-wrapped lines from the shell (like paragraphs of text) should reflow to fill the new width. However, TUI applications draw "pictures" using characters (box drawing, spaces with background colors) which must **NOT** be reflowed or merged, as this destroys the layout.

### The Solution: Conditional Line Protection
A line in the `TerminalBuffer` is considered "Protected" (will not merge with the next line) if it meets any of the following criteria at its **visual end**:

1.  **Box Drawing Characters**:
    -   Unicode Range: `\u2500` to `\u257F` (Box Drawing)
    -   ASCII Fallbacks: `+`, `-`, `|` (Only if `IsWrapped` was set, indicating TUI intent).

2.  **Block Elements**:
    -   Unicode Range: `\u2580` to `\u259F` (Full Block, Shades, etc.) often used for scrollbars and shadows.

3.  **Fullwidth Forms**:
    -   Unicode Range: `\uFF00` to `\uFFEF` (e.g., `｜` U+FF5C).
    -   Important for CJK and some WSL renderings.

4.  **Specific TUI Indicators**:
    -   **Scroll Indicators**: `>` (ASCII 62) at the end of a line (often used by MC to show content overflow).
    -   **Sort Indicators**: `^` (ASCII 94), `↑` (U+2191), `↓` (U+2193) used in headers.
        -   **CRITICAL**: These must be protected **only** if they have a non-default background color or are part of a known header structure, to avoid breaking regular text flow.

5.  **Background Color Fill**:
    -   If the line ends with **Space** characters (` `) that have a **non-default background color**, it is treated as a TUI panel background and protected. I.e. blue background spaces in MC.

### Null Cell Handling
When a buffer is widened (e.g., 80 -> 100 cols), new cells are initialized as `null` or `default`. The heuristic logic scans **backwards** from the end of the line, skipping these null/default cells to find the actual last content character.

---

## 2. Character Handling & Metrics

### "Mixed" Character Artifacts (The `IsCombining` Bug)
A visual artifact where characters like `[` and `^` appeared to overlap or mix was traced to incorrect `IsCombining` logic.

-   **Issue**: `^` (Cicrumflex Accent, U+005E) has Unicode Category `ModifierSymbol` (`Sk`).
-   **Bug**: The `IsCombining` method naively treated all `ModifierSymbol` characters as zero-width combining marks.
-   **Result**: The renderer drew `^` with Width 0, causing the next character `]` to be drawn at the same position.

**Fix**:
The `IsCombining` logic in `TerminalBuffer.cs` now explicitly **excludes** all ASCII characters (< 0x80).
-   `^`, `~`, ` `` ` are always treated as **Spacing Characters** (Width 1).
-   This ensures standard ASCII headers render correctly.

---

## 3. Vertical Resize Optimization

Changing the terminal height (Rows) **should not** trigger a Text Reflow. Reflows are computationally expensive and risky (potential data loss or prompt duplication).

-   **Optimization**: `TerminalBuffer.Resize` checks if `newCols == oldCols`. If only rows changed, it calls `Reshape` instead of `Reflow`.
-   **Reshape**: Simply adds/removes rows from the viewport or scrollback without re-wrapping text. This is critical for smooth window resizing.
