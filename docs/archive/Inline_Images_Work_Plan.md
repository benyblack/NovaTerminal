# Phase 3 – Inline Images (iTerm2 / Kitty) – Precise Work Plan

Purpose:
This document defines a precise, test-gated implementation plan for Inline Images
in NovaTerminal. Primary target is the iTerm2 image protocol; Kitty graphics support
is optional and secondary.

Audience:
- Automated coding agents
- Maintainers
- Reviewers

Scope:
- Renderer extension only
- No VT / ANSI semantic changes
- No buffer, cursor, or reflow semantic changes

---

Core Principle:
Inline images must behave like text occupying cells, not floating UI elements.

---

Phase 3.0 – Mandatory Guardrails

Allowed modification areas:
- TerminalView
- TerminalDrawOperation
- Image decoding / cache helpers

Must NOT be modified:
- AnsiParser (except protocol tokenization)
- TerminalBuffer semantics
- TerminalRow wrapping logic
- Cursor movement logic

Rule:
Images must be represented as cell-backed entities in the renderer.

---

Phase 3.1 – Image Cell Model (FOUNDATION)

Introduce a renderer-level ImageCell:

- ImageId
- CellX
- CellY
- CellWidth (cells)
- CellHeight (cells)
- ZIndex (background layer only)

Rules:
- Cell-aligned only
- Background layer only
- Cursor advances as blank cells

---

Phase 3.2 – iTerm2 Image Protocol

Support OSC 1337;File=

Features:
- Base64 decoding
- Width/height in cells or pixels
- Scrollback persistence

Not supported:
- Remote file fetching
- HTTP URLs

---

Phase 3.3 – Rendering & Composition

Render order:
1. Background
2. Image
3. Text
4. Cursor
5. Selection

Images must never overlap text.

---

Phase 3.4 – Resize Safety

Rules:
- Reflow via cell grid only
- No pixel rescaling
- Clip if image does not fit

---

Phase 3.5 – Selection Semantics

Rules:
- Copy placeholder text for image-backed cells
- No clipboard image copy in v1

---

Phase 3.6 – Kitty Graphics (Optional)

Blocked until iTerm2 support is stable and replay-safe.

---

Phase 3.7 – Performance Guardrails

- Bounded image cache
- Max image dimensions
- No steady-state full redraws

---

Exit Criteria:
- Replay passes on all OSes
- No scrollback corruption
- Incremental rendering preserved

---

Final Rule:
Inline images must never compromise determinism or correctness.
