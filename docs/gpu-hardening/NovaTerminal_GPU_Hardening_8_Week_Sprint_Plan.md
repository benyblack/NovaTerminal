# NovaTerminal GPU Hardening --- 8 Week Sprint Plan

Generated: 2026-02-26T13:28:47.837605 UTC

------------------------------------------------------------------------

## Sprint Objective

Deliver production-grade GPU rendering discipline without sacrificing
determinism.

------------------------------------------------------------------------

# Week 1--2: Instrumentation Phase

Deliver: - RenderPerfMetrics implementation - JSONL export -
FrameTimeMs + AllocBytes metrics

Goal: Establish baseline numbers.

------------------------------------------------------------------------

# Week 3--4: Allocation Elimination

Focus: - Remove float\[\] allocations - Remove ToArray() in
FlushBatches - Cache SKPaint, SKFont, SKShaper - Remove per-row
StringBuilder allocations

Deliver: - Allocation regression test - Verified reduced alloc/frame

------------------------------------------------------------------------

# Week 5--6: Snapshot Boundary + Pixel Grid

Tasks: - Enforce snapshot-only renderer contract - Implement PixelGrid -
Replace float grid math - Extend seam tests

Deliver: - DPI stable at 125%/150% - Replay parity intact

------------------------------------------------------------------------

# Week 7: Dirty Span Precision + Batching

Tasks: - Track per-row dirty spans - Merge adjacent spans - Batch glyph
runs by style bucket

Deliver: - Reduced draw calls - No visual regressions

------------------------------------------------------------------------

# Week 8: Hardening & Documentation

Tasks: - Add performance contract doc - Tune thresholds - Run CI stress
builds - Manual stress test with vim, lazygit, htop

Deliver: - Stable metrics across 3 OS - Ready for public benchmark
narrative

------------------------------------------------------------------------

# Success Criteria

-   No seams
-   Smooth scroll at 60Hz on typical workloads
-   Low steady-state allocations
-   Deterministic replay parity preserved

------------------------------------------------------------------------

End of sprint plan.
