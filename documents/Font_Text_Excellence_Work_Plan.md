# Phase 3 – Font & Text Excellence (Precise Work Plan)

**Purpose**
This document defines a precise, test-gated implementation plan for **Font & Text Excellence**
in NovaTerminal. It is written for automated coding agents and maintainers.

Scope:
- Rendering quality only
- No VT / ANSI semantic changes
- No buffer or reflow logic changes

---

## Phase 3.0 – Mandatory Guardrails (Pre-work)

### 3.0.1 Renderer Boundaries
All work in this phase must be limited to:
- TerminalView
- TerminalDrawOperation
- Font / metrics helpers

The following modules must NOT be modified:
- AnsiParser
- TerminalBuffer
- TerminalRow
- TerminalCell

Rule:
> Font work must never mutate buffer state or wrapping logic.

---

### 3.0.2 Text-Focused Replay Fixtures
Add replay fixtures stressing text layout:
- mixed ASCII + Unicode
- long wrapped lines
- resize during output
- prompts with powerline glyphs
- emoji mixed with text

These fixtures are reused unchanged throughout Phase 3.

---

## Phase 3.1 – Cell Metrics Authority (FOUNDATION)

### Goal
Define a single authoritative cell geometry used everywhere in rendering.

---

### 3.1.1 Introduce CellMetrics

Create a single source of truth:

```csharp
struct CellMetrics
{
    float CellWidth;
    float CellHeight;
    float Baseline;
    float Ascent;
    float Descent;
}
```

Rules:
- Computed once per (font family, size, DPI)
- Cached aggressively
- Never recomputed per frame

---

### 3.1.2 Centralized Font Measurement

- Measure using Skia font metrics
- Lock metrics at startup or font change
- Apply consistent rounding to avoid float drift

Invariant:
> Every cell occupies exactly the same rectangle at all times.

---

### 3.1.3 Enforce Renderer Usage

Audit rendering code:
- No ad-hoc glyph measurement in draw loop
- No per-glyph width decisions
- All placement uses (row, col) × CellMetrics

CI Rule:
- Fail build if renderer measures text during draw

---

### Tests
- Metric stability test (same font config → same metrics)
- Replay fixtures produce identical cell alignment

---

## Phase 3.2 – Deterministic Font Fallback Chain

### Goal
Ensure identical fallback behavior across all OSes.

---

### 3.2.1 Explicit Fallback Order

Define deterministic order:

Primary font  
→ User-configured fallback(s)  
→ Platform-neutral fallback  
→ Last-resort monospace

Do not rely on OS default fallback behavior.

---

### 3.2.2 Fallback Resolution Rules

- Resolution is deterministic
- Cached per codepoint range
- Renderer never probes fonts dynamically during draw

---

### 3.2.3 Cross-Platform Parity Tests

- Same replay fixtures on all OSes
- Assert identical cell placement and width
- No overflow or drift allowed

---

## Phase 3.3 – Ligatures (Opt-In, Visual Only)

### Goal
Support ligatures without altering terminal semantics.

---

### 3.3.1 Ligature Rules (Non-Negotiable)

- Ligatures collapse glyphs, NOT cells
- Cursor movement remains cell-based
- Selection remains cell-based
- Visual-only feature

Abort ligature work if any rule cannot be guaranteed.

---

### 3.3.2 Implementation Approach

- Shape text runs per row
- Map shaped glyph clusters back to cell ranges
- Render ligature glyphs across multiple cells
- Background and selection remain cell-based

---

### 3.3.3 Configuration

- Per-profile toggle:
  - Enable font ligatures
- Default: disabled

---

### Tests
- Replay fixtures with ligature-heavy prompts
- Assert identical buffer snapshots
- Assert cursor and selection invariants

---

## Phase 3.4 – Emoji & Wide Glyph Correctness

### Goal
Correct width handling without layout heuristics.

---

### 3.4.1 Width Classification

Explicit categories:
- width = 1 cell
- width = 2 cells (emoji, CJK)
- width = 0 (combining marks)

No heuristics in renderer.

---

### 3.4.2 Placement Rules

- Wide glyph occupies left cell
- Right cell marked as continuation
- Cursor advances correctly
- Background fills both cells

---

### 3.4.3 Fallback Handling

- Fallback glyphs must respect width
- No layout collapse if emoji font missing

---

### Tests
- Mixed emoji + ASCII
- Resize stress tests
- Selection across wide glyphs

---

## Phase 3.5 – DPI & Scaling Stability

### Goal
Prevent layout drift during DPI or scale changes.

---

### Deliverables
- CellMetrics recomputed only on DPI/font change
- Renderer cache invalidated cleanly
- One full redraw, then incremental rendering resumes

---

### Tests
- DPI change simulation
- Assert no accumulated drift
- Assert stable cell alignment

---

## Phase 3.6 – Performance Validation

### Goal
Ensure quality improvements do not regress performance.

---

### Metrics
- Glyph cache hit rate
- Dirty cell count per frame
- Frame time percentiles

---

### Acceptance Criteria
- No increase in steady-state full redraws
- No frame time regression
- Incremental rendering remains dominant

---

## Phase 3 Exit Criteria

Phase 3 is complete when:
- Text alignment is pixel-stable
- No glyph drift on resize
- Replay fixtures match across OSes
- Ligatures (if enabled) do not affect semantics
- Emoji widths are correct
- Renderer metrics stay within thresholds

---

## Explicitly Out of Scope

- Variable-width cells
- Proportional fonts
- Text shaping that affects layout
- Emoji styling/theming
- Inline images or graphics (later phase)

---

## Execution Order (Strict)

1. Cell metrics authority
2. Deterministic fallback
3. Ligatures (optional)
4. Emoji & wide glyphs
5. DPI stability
6. Performance validation

---

## Final Rule

> Text rendering must never change terminal semantics.
