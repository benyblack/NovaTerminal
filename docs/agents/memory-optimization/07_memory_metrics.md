# Prompt 7 — Add Memory Metrics + Debug View

Goal:
Add memory diagnostics for terminal buffers.

Implement:

TerminalMemoryMetrics

Include:

ScrollbackBytes
ScrollbackPages
ViewportCells
GlyphCacheSize

Expose through:

TerminalBuffer.GetMemoryMetrics()

Add debug log every 5 seconds:

[TerminalMemory]
ScrollbackMB=XX
Pages=XX
GlyphCache=XX