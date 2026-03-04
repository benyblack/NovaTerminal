# NovaTerminal GPU Hardening --- Codex Task-by-Task Execution Prompt Pack

Generated: 2026-02-26T13:32:53.889279 UTC

This pack is designed to be copied task-by-task into Codex (or an IDE
agent).\
Each task has: **Goal**, **Files**, **Steps**, **Acceptance Criteria**,
and **Safety/Guardrails**.

Global Guardrails (apply to every task): - Do **not** change replay
semantics; all `ReplayTests/*` and parity snapshot comparisons must
remain green. - Do **not** introduce per-frame allocations in
steady-state scroll. - Keep Windows/macOS/Linux passing. - Keep
seam/white-line regressions fixed (BlockSeamRegressionTests and any DPI
tests).

------------------------------------------------------------------------

## TASK 00 --- Baseline & Safety Net (Read + Run)

**Goal:** Establish baseline and confirm current test suite + perf gates
before modifications.

**Files:** none

**Steps:** 1. Run: `dotnet test -c Release` 2. Run replay tests:
`dotnet test -c Release --filter Category=Replay` 3. Run perf tests:
`dotnet test -c Release --filter Category=Performance` 4. (Optional) Run
seam tests: `dotnet test -c Release --filter Category=Render` or
explicit test class. 5. Capture baseline results (save console output to
`artifacts/baseline_gpu_hardening.txt`).

**Acceptance Criteria:** - All tests pass (or you record known
failures + links to issues). - You have baseline outputs stored.

------------------------------------------------------------------------

## TASK 01 --- Add RenderPerfMetrics (Data Model + Writer)

**Goal:** Introduce a lightweight per-frame metrics struct and optional
JSONL writer behind env flags.

**Files to create/edit:** - Create:
`src/NovaTerminal.Rendering/RenderPerfMetrics.cs` (or nearest
appropriate project if Rendering project differs) - Create:
`src/NovaTerminal.Rendering/RenderPerfWriter.cs` (internal JSONL
writer) - Edit: `src/NovaTerminal.App/Core/TerminalDrawOperation.cs` to
populate metrics

**Env flags:** - `NOVATERM_RENDER_METRICS=1` -
`NOVATERM_RENDER_METRICS_OUT=<path>` (optional)

**Steps:** 1. Create `RenderPerfMetrics` struct with fields: -
FrameIndex, FrameTimeMs - DirtyRows, DirtySpansTotal - DrawCallsText,
DrawCallsRects, DrawCallsTotal - RowPictureCacheHits,
RowPictureCacheMisses, PictureBuilds - FlushCount, AtlasAlphaGlyphs,
AtlasColorGlyphs - DirectDrawTextCount, ShapedTextRuns -
AllocBytesThisFrame (best-effort) 2. Implement `RenderPerfWriter`: -
When enabled, append JSON per frame to the configured path. - Make IO
resilient (catch exceptions; disable writer on failure). 3. In
`TerminalDrawOperation`, add: - `Stopwatch` timing around the whole
frame render - `GC.GetAllocatedBytesForCurrentThread()` delta at
start/end (render thread only) - Counter increments at appropriate
draw/batch/cache decisions 4. Ensure default path is **off** with
near-zero overhead.

**Acceptance Criteria:** - With env flags disabled: behavior
unchanged. - With enabled: file contains valid JSONL entries (one per
frame). - No flaky file locking issues on Windows (use FileStream with
Append + ShareRead).

**Guardrails:** - Do not allocate large strings per frame when metrics
enabled; keep JSON compact. - Do not break AOT readiness.

------------------------------------------------------------------------

## TASK 02 --- Pool/Re-use Cell Edge Grids (Stop per-frame float\[\] allocations)

**Goal:** Eliminate per-frame allocations from `BuildCellEdgeGrid` usage
in `TerminalDrawOperation`.

**Files:** - `src/NovaTerminal.App/Core/TerminalDrawOperation.cs`

**Steps:** 1. Locate calls that create new `float[]` each frame
(e.g. `BuildCellEdgeGrid(cols)` / `BuildCellEdgeGrid(rows)`). 2. Replace
with reusable/pool-backed buffers: - Maintain fields: `_colEdges`,
`_rowEdges`, `_colEdgesLen`, `_rowEdgesLen` - Resize only when cols/rows
change (re-rent from `ArrayPool<float>.Shared`). 3. Ensure buffers are
returned on dispose (if draw op has lifecycle) or when resized.

**Acceptance Criteria:** - Allocation metrics show a drop in alloc/frame
for steady scroll. - No correctness changes (seam tests still pass).

------------------------------------------------------------------------

## TASK 03 --- Fix StringBuilder reuse in DrawRowTextFromSnapshot

**Goal:** Remove per-row `new StringBuilder(...)` allocations in hot
path.

**Files:** - `src/NovaTerminal.App/Core/TerminalDrawOperation.cs` -
(Optional) create helper:
`src/NovaTerminal.Rendering/StringBuilderCache.cs`

**Steps:** 1. Find `new StringBuilder(...)` inside row draw loop. 2.
Replace with reusable builder: - Option A: ThreadStatic builder cache -
Option B: `StringBuilderCache.Acquire(capacity)` / `Release(sb)` 3.
Ensure builder is cleared and capacity controlled (avoid unbounded
growth).

**Acceptance Criteria:** - No `new StringBuilder` per dirty row in
steady state. - Same rendered text.

------------------------------------------------------------------------

## TASK 04 --- Reuse SKPaint (Remove per-run SKPaint allocations)

**Goal:** Avoid creating/disposing `SKPaint` inside inner loops.

**Files:** - `src/NovaTerminal.App/Core/TerminalDrawOperation.cs`

**Steps:** 1. Identify all `new SKPaint` / `using var ... = new SKPaint`
inside per-run loops: - background fill - underline/strikethrough -
block fills 2. Introduce reused paints as fields or per-frame locals
created once: - `_bgPaint` (Fill, IsAntialias=false) - `_decoPaint`
(Stroke) - `_blockPaint` (Fill) 3. Only mutate `.Color`, `.StrokeWidth`,
`.Style` as needed per run. 4. Ensure paints are disposed correctly at
end-of-life (if fields, dispose in owning object).

**Acceptance Criteria:** - Reduced alloc/frame. - No changes to line
thickness or anti-aliasing artifacts.

------------------------------------------------------------------------

## TASK 05 --- Remove ToArray() in FlushBatches (No allocations per flush)

**Goal:** Stop allocations from `List<T>.ToArray()` used for `DrawAtlas`
batching.

**Files:** - `src/NovaTerminal.App/Core/TerminalDrawOperation.cs`

**Steps:** 1. Find `FlushBatches()` that calls `.ToArray()` for atlas
buffers. 2. Replace with: - Backing arrays stored as fields or rented
from `ArrayPool<T>` - Ensure capacity \>= count; copy list content into
arrays - Or switch to a custom growable array + count 3. Prefer a
compact custom struct array pattern if it helps (avoid per-item object
allocations). 4. Ensure calls pass arrays without slicing allocations.

**Acceptance Criteria:** - No `ToArray()` called in steady scroll. -
AllocBytes/frame drop significantly on heavy output workloads.

------------------------------------------------------------------------

## TASK 06 --- Per-frame cache for fallback SKFont (Reduce mixed-script overhead)

**Goal:** Avoid `new SKFont(...)` per rune for fallback fonts.

**Files:** - `src/NovaTerminal.App/Core/TerminalDrawOperation.cs`

**Steps:** 1. Identify code path allocating fallback fonts. 2. Add
per-frame cache: - `Dictionary<SKTypeface, SKFont> _fallbackFontCache` -
cleared at end of frame 3. Reuse cached `SKFont` for repeated glyphs
within frame. 4. Dispose cached fonts at end-of-frame when clearing.

**Acceptance Criteria:** - Fewer SKFont allocations in mixed Unicode
workloads. - No leaks (verify via MemoryLeakTest or add a targeted
test).

------------------------------------------------------------------------

## TASK 07 --- Cache SKShaper per typeface (Avoid per-run shaper allocations)

**Goal:** Reduce allocations and overhead from `new SKShaper(...)` in
complex shaping.

**Files:** - `src/NovaTerminal.App/Core/TerminalDrawOperation.cs`

**Steps:** 1. Identify complex shaping path using `new SKShaper(tf)`. 2.
Introduce per-frame cache: -
`Dictionary<SKTypeface, SKShaper> _shaperCache` - Clear/dispose at
end-of-frame 3. Ensure thread confinement (render thread only).

**Acceptance Criteria:** - Complex scripts draw with fewer
allocations. - No hangs/crashes from improper disposal.

------------------------------------------------------------------------

## TASK 08 --- PixelGrid: Convert hot geometry to integer pixel math

**Goal:** Eliminate float drift and reduce seam risk by using integer
pixel grid.

**Files to create/edit:** - Create:
`src/NovaTerminal.Rendering/PixelGrid.cs` - Edit:
`src/NovaTerminal.App/Core/TerminalDrawOperation.cs` to use PixelGrid

**Steps:** 1. Implement `PixelGrid`: - stores cell width/height in
pixels, origin px, baseline px, underline px - methods
`XForCol(int col)` and `YForRow(int row)` returning int pixels 2.
Compute PixelGrid once per layout/resize (consistent rounding). 3.
Replace `float x = col * CellWidth` etc with PixelGrid int math. 4.
Convert to float only at final Skia boundary as needed.

**Acceptance Criteria:** - Existing seam tests pass. - Add/extend a DPI
scaling seam test (125%, 150%) and keep it passing.

------------------------------------------------------------------------

## TASK 09 --- Snapshot-only rendering boundary enforcement

**Goal:** Ensure renderer never reads mutable buffer state during draw;
use immutable snapshots only.

**Files:** - `src/NovaTerminal.VT/RenderSnapshots.cs` -
`src/NovaTerminal.VT/TerminalBuffer.ThreadingAndInvalidation.cs` -
`src/NovaTerminal.App/Core/TerminalDrawOperation.cs`

**Steps:** 1. Formalize `TerminalRenderSnapshot` (or equivalent) to
contain everything draw needs. 2. Build snapshot under short lock scope,
then release locks before any draw. 3. Update `TerminalDrawOperation` to
accept snapshot and render from it only. 4. Add metrics: measure lock
time for snapshot creation (`ReadLockMs`).

**Acceptance Criteria:** - Replay parity tests pass unchanged. - No
deadlocks. - Reduced lock time reported.

------------------------------------------------------------------------

## TASK 10 --- Dirty spans per row (Precision invalidation)

**Goal:** Reduce work/draw calls by tracking dirty spans rather than
invalidating entire rows.

**Files:** - `src/NovaTerminal.VT/RenderSnapshots.cs` -
`src/NovaTerminal.VT/TerminalBuffer.ThreadingAndInvalidation.cs` -
`src/NovaTerminal.App/Core/TerminalDrawOperation.cs`

**Steps:** 1. Extend snapshot to include dirty spans per row: list of
(startCol, endCol). 2. Merge adjacent/overlapping spans. 3. Renderer
iterates only spans for that row. 4. Update row picture caching logic to
invalidate only when needed (or re-record row picture if simpler).

**Acceptance Criteria:** - Metrics show reduced DrawCallsText and/or
PictureBuilds on small edits. - Correctness unchanged (no missing
updates).

------------------------------------------------------------------------

## TASK 11 --- Add perf regression tests (alloc + draw calls) using metrics JSONL

**Goal:** Prevent future regressions; make "not dumb on GPU" provable.

**Files to add:** -
`tests/NovaTerminal.Tests/Performance/RenderPerf_Allocations_SteadyScroll.cs` -
`tests/NovaTerminal.Tests/Performance/RenderPerf_DrawCalls_SteadyScroll.cs`

**Steps:** 1. Create a deterministic workload: - Prefer replay fixture
if available; otherwise generate a VT stream that fills screen and
scrolls. 2. Enable env flags inside test: - set
`NOVATERM_RENDER_METRICS=1` - set out path to test temp directory 3. Run
for N frames (warm up first). 4. Parse JSONL and assert conservative
ceilings: - `AvgAllocBytesPerFrame <= X` -
`p95AllocBytesPerFrame <= Y` - `AvgDrawCallsText <= A` (after batching)
5. Keep thresholds initially generous; tighten later.

**Acceptance Criteria:** - Tests stable in CI (avoid flakiness). - Fail
meaningfully when regressions occur.

------------------------------------------------------------------------

## TASK 12 --- docs/RENDERING_PERF_CONTRACT.md

**Goal:** Document how to measure and enforce performance + how to debug
failures.

**Files:** - `docs/RENDERING_PERF_CONTRACT.md`

**Contents:** - env vars - where JSONL goes - how to run tests locally -
interpreting key metrics - typical causes of regressions (alloc spikes,
cache misses, fallback shaping storms)

**Acceptance Criteria:** - Clear, actionable doc for contributors and
future you.

------------------------------------------------------------------------

# Suggested execution order

1)  TASK 00
2)  TASK 01
3)  TASK 02--05 (biggest alloc wins)
4)  TASK 06--07 (unicode/shaping wins)
5)  TASK 08--10 (stability + batching)
6)  TASK 11--12 (lock it in)

------------------------------------------------------------------------

# Completion definition

You are done when: - Replay parity remains green across OS - Seam tests
pass under fractional DPI - Steady scroll has low allocations/frame -
Draw calls are within sane ceilings - Docs exist and CI catches
regressions

End of pack.
