# Emoji and Text Rendering: GPU-Accelerated Architecture

This document outlines the high-performance implementation of text and emoji rendering in NovaTerminal, achieving "Physics-Perfect" clarity and extreme throughput.

## Current Architecture

### 1. Hybrid Rendering Pipeline
NovaTerminal uses a multi-layered approach to balance quality and performance:
- **Phase 1 (HarfBuzz)**: Complex clusters (emojis, ZWJ sequences, ligatures) are shaped as single units using `SKShaper`.
- **Phase 3 (GPU Cache)**: Standard glyphs are cached in a specialized **Dual-Atlas** system and rendered using hardware-accelerated batching.

### 2. The Dual-Atlas System
To maximize video memory efficiency and rendering speed:
- **Alpha8 Atlas**: Stores monochromatic glyphs as alpha masks. These are colorized on-the-fly using `SKBlendMode.Modulate`, enabling thousands of glyphs in different colors to share a single texture.
- **RGBA8888 Atlas**: Optimized for multi-color emojis and complex clusters that cannot be monochromatic.
- **Batching**: Thousands of glyphs are dispatched in single GPU calls via `SKCanvas.DrawAtlas`, significantly reducing CPU overhead.

### 3. "Physics-Perfect" Sharpness
To eliminate blurriness on High-DPI displays (125%, 150%, etc.):
- **Physical Resolution**: Glyphs are rendered into the atlas at their true **physical resolution** (logical size × scale).
- **1:1 Pixel Mapping**: Using compensatory scaling in the transformation matrix, we ensure a bit-perfect mapping from texture pixels to screen pixels.
- **Physical Snapping**: Every character position is rigorously snapped to the physical pixel grid using `Round(coord * scale) / scale`.

### 4. Buffer Synchronization
- **Wide Characters**: Multi-cell characters (CJK, Emojis) use `IsWideContinuation` flags in the buffer.
- **Clipping**: High-throughput rendering uses strict cell-based boundaries, ensuring zero bleed into adjacent cells.

## Unicode Excellence
- **ZWJ Support**: Full support for skin tones, multi-person emojis, and complex sequences.
- **Ligatures**: High-fidelity ligature support without breaking terminal cell semantics.
- **Font Fallback**: Deterministic fallback chain (e.g., Segoe UI Emoji) ensures consistent cross-platform parity.

## Performance Metrics
- **CPU**: Near-zero CPU cost for steady-state text output.
- **Memory**: Texture atlases are capped (e.g., 2048x2048) and managed via LRU logic.
- **Sharpness**: Visual parity with vector rendering at native speeds.
