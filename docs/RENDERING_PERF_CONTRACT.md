# Rendering Perf Contract

## Purpose
This contract prevents GPU/render-path regressions while preserving deterministic replay behavior.

It enforces conservative ceilings on:
- per-frame allocations
- text draw-call volume

The contract is intentionally "good-enough + stable" rather than aggressively tight.

## Enabling Render Metrics
Set these environment variables before rendering:

- `NOVATERM_RENDER_METRICS=1`
- `NOVATERM_RENDER_METRICS_OUT=<absolute-or-relative-path-to-render_metrics.jsonl>`

When enabled, renderer frame metrics are appended as JSONL (`one JSON object per line`).

## Metrics Glossary
- `AllocBytesThisFrame`: GC allocation delta captured for the render thread in one frame.
- `DrawCallsText`: text draw calls (`DrawText`/`DrawShapedText` paths).
- `DrawCallsRects`: rectangle/line draw calls tracked by renderer counters.
- `DrawCallsTotal`: text + rects + other tracked calls (pictures/bitmaps/atlas flush calls).
- `DirtyRows`: dirty visual rows rendered this frame.
- `DirtySpansTotal`: total dirty spans reported by snapshot for this frame.
- `DirtySpanCount`: normalized span count used by renderer for this frame.
- `SpanRenderCount`: number of rows rendered via span-only update path.
- `RowRenderCount`: number of rows rendered as full-row fallback.
- `FlushCount`: glyph-atlas flush count.
- `AtlasAlphaGlyphs`: alpha atlas glyph count flushed this frame.
- `AtlasColorGlyphs`: color atlas glyph count flushed this frame.
- `ShapedTextRuns`: complex-shaping runs rendered this frame.
- `BufferReadLockTimeMs (ReadLockMs)`: snapshot capture lock time, recorded in renderer statistics (`RendererStatistics.RecordBufferReadLockTimeMs`).

## Running Perf Contract Tests
Run:

```powershell
dotnet test -c Release --filter Category=Performance
```

The contract tests added for this suite:
- `RenderPerf_Allocations_SteadyScroll_WithinConservativeCeilings`
- `RenderPerf_DrawCalls_SteadyScroll_WithinConservativeCeilings`

Both tests:
- run a deterministic steady-scroll workload
- ignore warmup frames
- assert average + p95 ceilings

## Interpreting Failures
Use this triage order:

1. Allocation spike (`Avg/P95 AllocBytesThisFrame`)
- Check for hot-path allocations:
  - `ToArray()`
  - `new StringBuilder`
  - `new SKPaint`
  - `new SKFont`
  - `new SKShaper`
- Verify per-frame caches are disposed at end-of-frame, not per-run.

2. Draw-call spike (`Avg/P95 DrawCallsText` or `Avg DrawCallsTotal`)
- Inspect span rendering/fallback behavior:
  - too many full-row fallbacks
  - row cache miss bursts
  - dirty-span explosion from invalidation logic

3. Frame-time spike (if monitored in CI artifacts)
- Check lock time and render batching:
  - snapshot read-lock inflation
  - row picture build bursts
  - atlas flush storms

## Updating Thresholds Safely
When intentional performance changes happen:

1. Capture fresh baseline on Windows and Linux.
2. Use the slower baseline host as reference.
3. Set new ceiling as:

`ceiling = baseline * 1.30`

4. Tighten gradually over follow-up changes, never in one large jump.

