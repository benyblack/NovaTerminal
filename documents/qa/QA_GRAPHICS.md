# QA: Graphics Regression Test Plan

This document defines the manual steps required to verify that Nova Terminal's graphics implementation is robust across updates. It is a subset of the [Master QA Plan](file:///d:/projects/nova2/documents/qa/MASTER_QA_PLAN.md).

## 1. Protocol Verification

### 1.1 Kitty Graphics
*   **Action**: Emit a Kitty-encoded image sequence to the terminal.
*   **Expected**: The image displays clearly without artifacts.
*   **Regression Check**: Ensure subsequent text is correctly positioned below the image.

### 1.2 iTerm2 Graphics
*   **Action**: Emit an iTerm2 inline image sequence.
*   **Expected**: The image displays correctly.
*   **Regression Check**: Verify the image doesn't "stretch" or overlap inappropriately.

### 1.3 Tunneled Sixel (Windows/Recommended)
*   **Action**: Run `python test_sixel.py wrapped`.
*   **Expected**: A red block rendered in Sixel appears.
*   **Regression Check**: Check `debug.log` for `[ANSI_PARSER] Sixel decoded successfully`.

### 1.4 Raw Sixel (WSL/Linux only)
*   **Action**: Run `python test_sixel.py raw` in a WSL shell OR native Linux.
*   **Expected**: The Sixel image renders. (Note: Expected failure on native CMD/PWSH).

---

## 2. Interaction and Performance

### 2.1 Scrolling Behavior
*   **Action**: Render multiple images and scroll up/down past them.
*   **Expected**: Images remain locked to their cell coordinates and don't "drift" or disappear.
*   **Regression Check**: Verify that images in the scrollback buffer are still visible when scrolling back up.

### 2.2 Horizontal Resizing (Reflow)
*   **Action**: Render an image, then shrink/expand the window width.
*   **Expected**: Images should move with their associated lines.
*   **UI Check**: Ensure images don't cause prompt duplication or text jumbling during the reflow process.

### 2.3 Screen Clear (Alt Screen)
*   **Action**: Enter an interactive tool (like `icat` or a simple image previewer), clear screen, and exit.
*   **Expected**: Images inside the tool should be cleared.
*   **Regression Check**: Exiting back to the main buffer should NOT show "ghost" images from the alt screen.

---

## 3. Environment and Detection

### 3.1 `TERM` Propagation
*   **Action**: Run `echo $env:TERM` (Windows) or `echo $TERM` (Unix).
*   **Expected**: Output: `xterm-256color`.

### 3.2 DA1 Response
*   **Action**: Run `python test_sixel.py da` (Works only on Unix-like or with direct PTY).
*   **Expected**: Parser responds with Sixel capability.
*   **Check**: Verify `debug.log` shows `Handled Primary DA query`.

---

---

## 5. Text Rendering and High-DPI Fidelity

### 5.1 "Physics-Perfect" Sharpness
*   **Action**: Change OS scaling to fractional values (e.g., 125%, 150%).
*   **Expected**: Text remains razor-sharp with no blurriness or "halos."
*   **Verification**: Check that character edges are solid and match the physical pixel grid.

### 5.2 Theme-Neutral Weight
*   **Action**: Switch between Dark and Light themes.
*   **Expected**: Text weight should feel consistent (no "thin" or fuzzy text on light backgrounds).
*   **Verification**: Ensure no color swapping or "Inverse" rendering artifacts.

### 5.3 Complex Shaping and Ligatures
*   **Action**: Type complex emojis (e.g., 👩🏽‍💻) and ligature sequences (e.g., `=>`, `!=`).
*   **Expected**: Characters merge correctly into single glyphs via HarfBuzz.
*   **Verification**: Ensure background colors correctly cover the entire multi-cell region.
