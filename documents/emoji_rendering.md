# Emoji Rendering: Architecture and Limitations

This document outlines the implementation of emoji and grapheme cluster rendering in NovaTerminal, the identified limitations, and the proposed path to full Unicode excellence.

## Current Architecture

### 1. Buffer Synchronization
NovaTerminal treats multi-cell characters (like emojis and CJK) as a base cell + N continuation cells (`IsWideContinuation`). 
- **Attachment Logic**: When the PTY sends combining marks, ZWJ sequences, or skin-tone modifiers, the `TerminalBuffer` re-evaluates the width of the target grapheme. 
- **Expansion**: If an attachment causes a width-1 character to become width-2, the buffer automatically marks the adjacent cell as a continuation.
- **Renderer Sync**: The renderer respects these flags, skipping continuation cells to prevent "over-drawing" or double-rendering of wide glyphs.

### 2. Glyph Isolation (Clipping)
To prevent wide emojis from bleeding into adjacent characters, the renderer uses a strict horizontal clipping strategy:
- **`SaveLayer`**: Every wide-character drawing operation is wrapped in a Skia `SaveLayer` with a tight bounding rectangle. This ensures that even "overhangs" in the font glyph are strictly contained within the allocated terminal cells.

## Known Limitations

### 1. The "Yellow Emoji" Problem (Lack of Shaping)
While emojis are correctly positioned and clipped, complex sequences (like skin-tone variations or multi-person ZWJ clusters) often render as the base "yellow" version or as separate glyphs.
- **Root Cause**: The current `SKCanvas.DrawText` API provides basic glyph rendering but lacks a **Text Shaping Engine** (like HarfBuzz). 
- **Detail**: Shaping is required to combine multiple Unicode code points (e.g., `U+1F44D` Thumbs Up + `U+1F3FB` Light Skin Tone) into a single, specific glyph ID from the emoji font.

### 2. Font Fallback Complexity
The terminal currently forces a fallback to `Segoe UI Emoji` for known emoji ranges. This is an effective heuristic but does not replace a proper system-wide font fallback chain that can handle all Unicode planes.

## Solutions and Roadmap

### Phase 1: HarfBuzz Integration (Recommended)
To solve the shaping issue, NovaTerminal should integrate **SkiaSharp.HarfBuzz**.
- **Change**: Replace direct `DrawText` calls with `SKShaper`.
- **Benefit**: This will enable colored skin tones, complex flags, and all ZWJ-based emoji sequences.

### Phase 2: High-Level Text Layout
Transition from manual cell-by-cell rendering to Avalonia's `TextLayout` or a similar high-level API during the "Pass 3: Text" phase, but only for lines containing complex clusters. This allows for better shaping and bi-directional text support in the future.

### Phase 3: GPU-Accelerated Glyph Cache
Moving beyond simple Skia calls to a specialized glyph-cache texture would significantly improve performance for high-throughput emoji rendering, especially as the terminal grows in complexity.
