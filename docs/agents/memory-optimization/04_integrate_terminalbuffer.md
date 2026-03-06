# Prompt 4 — Integrate Page Scrollback with TerminalBuffer

Goal:
Integrate ScrollbackPages into TerminalBuffer scrolling logic.

Current behavior:

ScrollUpInternal()
- moves top row to scrollback
- shifts viewport
- allocates new bottom row

New behavior:

1. Before shifting viewport:

copy the first row's cells into:

ScrollbackPages.AppendRow(rowSpan)

2. Continue existing viewport logic unchanged.

3. Remove old CircularBuffer<TerminalRow> scrollback storage.

4. Ensure alt-screen logic does NOT append to scrollback.

5. Add tests:

- Scrollback retains correct text
- Eviction works
- No regression in terminal behavior

Tests should be added to:
tests/NovaTerminal.Tests/Buffer/