# Prompt 1 — Analyze Current Memory Usage

You are working on the NovaTerminal codebase.

Goal:
Identify the main sources of memory usage and allocation churn in the terminal buffer and rendering pipeline.

Tasks:

1. Analyze these components:
   - TerminalBuffer
   - TerminalRow
   - TerminalCell
   - Scrollback buffer implementation
   - GlyphCache
   - RenderCellSnapshot
   - Any CircularBuffer<T> implementations

2. Produce a technical report including:
   - Object allocation hotspots
   - Long-lived memory structures
   - GC pressure sources
   - Estimated memory usage per terminal line

3. Specifically measure or estimate:
   - bytes per cell
   - bytes per row
   - memory consumed by 10,000 scrollback lines

4. Provide a list of the top 10 memory optimizations ranked by impact.

Output format:
Markdown document:
docs/performance/memory-analysis.md