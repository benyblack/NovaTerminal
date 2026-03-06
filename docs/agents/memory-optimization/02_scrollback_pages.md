# Prompt 2 — Introduce Scrollback Page Architecture

Goal:
Replace the current scrollback storage model (TerminalRow objects) with a page-based architecture.

Design requirements:

1. Introduce a new structure:

TerminalPage
- int RowsInPage
- int Cols
- TerminalCell[] Cells (RowsInPage * Cols)
- int UsedRows

2. Implement helper methods:

Span<TerminalCell> GetRowSpan(int rowIndex)
void ClearRow(int rowIndex)

3. Introduce a new component:

ScrollbackPages
- Circular buffer of TerminalPage
- Byte budget limit

Public API:

AppendRow(ReadOnlySpan<TerminalCell> row)
TryEvictUntilWithinBudget()
Clear()

4. Add configuration:

MaxScrollbackBytes

Default:
128 MB

5. Eviction behavior:

When memory exceeds the budget:
- Evict oldest pages
- Return them to a page pool

6. Do NOT modify viewport logic yet.
Viewport rows should still use TerminalRow.

Output:
New files in Core/Buffer/
ScrollbackPages.cs
TerminalPage.cs