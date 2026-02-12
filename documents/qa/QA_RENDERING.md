# QA: Rendering Fidelity & Performance

**Objective**: Maintain "Physics-Perfect" sharpness and flicker-free updates.

## 1. Glyph Accuracy & High-DPI
- **Action**: Switch to high-DPI (fractional scaling like 125%, 150%).
- **Expected**: Text remains razor-sharp via the GPU-accelerated Dual-Atlas system.
- **Ligatures**: Verify that sequences like `=>`, `!=`, or `===` shape correctly.

## 2. Emoji & Advanced Shaping
- **ZWJ Sequences**: Verify complex emojis like 👩‍👩‍👧‍👦 (Family) or 🏳️‍🌈 (Pride Flag). They must render as a single glyph if the font supports it.
- **Grapheme Clusters**: Verify characters with multiple combining marks (e.g., Zalgo text or accented characters) render without overlap or "drifting" from the cell grid.

## 3. Font Fallback & Accuracy
- **Fallback Verification**: Input characters from multiple scripts (e.g., Greek, Cyrillic, Kanji) that require different fallback fonts.
- **Expected**: All characters should align perfectly to the cell grid. No "horizontal jitter" or mismatched baselines.

## 4. Atlas Stress & Visual Smoothing
- **Style/Color Stress**: Run a script that rapidly cycles colors and styles (e.g., `color-test.sh`).
- **Expected**: The glyph atlas handles the churn efficiently. No visual glitches or "wrong glyph" errors.
- **Incremental Rendering**: Verify 0-flicker performance during rapid window resizing or high-speed text output.
