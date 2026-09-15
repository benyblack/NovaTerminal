# NovaTerminal GPU Hardening --- Engineering Specification

Generated: 2026-02-26T13:28:47.837605 UTC

------------------------------------------------------------------------

## 1. Strategic Objective

Achieve "good-enough GPU speed" while preserving NovaTerminal's
strongest differentiator: deterministic replay correctness.

Constraints: - Keep Avalonia + Skia + .NET stack. - No replay format
regressions. - No visual artifacts (seams, DPI drift). - Near-zero
steady-state allocations during scroll.

------------------------------------------------------------------------

# 2. Architectural Targets

## 2.1 Renderer Contract

Renderer must: - Consume immutable `TerminalRenderSnapshot` - Never read
mutable `TerminalBuffer` during draw - Avoid runtime lock contention

### Files:

-   src/NovaTerminal.VT/RenderSnapshots.cs
-   src/NovaTerminal.VT/TerminalBuffer.ThreadingAndInvalidation.cs
-   src/NovaTerminal.App/Core/TerminalDrawOperation.cs

Tasks: - Add/Create `TerminalRenderSnapshot` DTO if not strict. -
Refactor draw path to remove live buffer reads. - Reduce lock scope to
snapshot creation only.

Acceptance: - Replay parity tests pass. - No deadlocks introduced.

------------------------------------------------------------------------

# 3. Rendering Pipeline Improvements

## 3.1 Integer Pixel Grid Enforcement

Create: - src/NovaTerminal.Rendering/PixelGrid.cs

Responsibilities: - Precompute CellWidthPx, CellHeightPx - Provide
ColToPx(), RowToPx() helpers - Handle DPI rounding once per resize

Refactor: - Replace float math in TerminalDrawOperation with PixelGrid
usage.

Acceptance: - Seam tests pass at 125% / 150% scaling.

------------------------------------------------------------------------

## 3.2 Allocation Elimination (Hot Path)

Target: No Gen0 churn during steady scroll.

### File: TerminalDrawOperation.cs

Replace: - Per-frame float\[\] edge arrays → pooled arrays - new
StringBuilder per row → cached builder - SKPaint inside loops → reused
paints - ToArray() in FlushBatches → preallocated arrays - Per-glyph
SKFont fallback → per-frame cache - Per-run SKShaper → cached per
typeface

Acceptance: - AllocBytesThisFrame under defined threshold after warmup.

------------------------------------------------------------------------

## 3.3 Dirty Span Precision

Enhance snapshot: - Track dirty spans per row instead of entire row
invalidation.

File changes: - RenderSnapshots.cs - TerminalDrawOperation.cs

Acceptance: - Reduced DrawCallsText metric. - No visual regressions.

------------------------------------------------------------------------

# 4. Metrics Infrastructure

Add: - src/NovaTerminal.Rendering/RenderPerfMetrics.cs

Environment Flags: - NOVATERM_RENDER_METRICS=1 -
NOVATERM_RENDER_METRICS_OUT=`<path>`{=html}

Metrics: - FrameTimeMs - DirtyRows - RowCacheHits/Misses -
DrawCallsText/Rects - AllocBytesThisFrame - TextShapingRuns

Acceptance: - JSONL export works. - No measurable overhead when
disabled.

------------------------------------------------------------------------

# 5. Performance Tests

Add under: tests/NovaTerminal.Tests/Performance/

New tests: - RenderPerf_Allocations_SteadyScroll.cs -
RenderPerf_DrawCalls_SteadyScroll.cs -
Render_DpiScaling_NoSeams_125_150.cs

Acceptance: - Conservative thresholds initially. - Stable CI runs.

------------------------------------------------------------------------

# 6. Documentation

Add: docs/RENDERING_PERF_CONTRACT.md

Contents: - Metrics explanation - Thresholds - How to run locally -
Debugging steps when failing

------------------------------------------------------------------------

# 7. Final Deliverables

-   Snapshot-only rendering enforced
-   Integer pixel grid
-   Allocation-free steady scroll
-   Dirty span batching
-   Metrics + tests
-   Updated documentation

------------------------------------------------------------------------

End of engineering specification.
