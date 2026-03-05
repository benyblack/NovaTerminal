# Prompt 3 — Implement Page Pooling

Goal:
Add pooling for scrollback pages and cell arrays.

Implement:

TerminalPagePool
- Rent(cols)
- Return(page)

Use:
ArrayPool<TerminalCell>

Requirements:

1. Pages must reuse cell arrays.
2. Page pool must support preheating.

Preheat:
4 pages per terminal instance.

3. When ScrollbackPages evicts a page:
Return it to the pool.

4. Add metrics:

ActivePages
PooledPages
BytesUsed

Expose metrics via:
ScrollbackPages.GetMetrics()

Output:
Core/Buffer/TerminalPagePool.cs