using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NovaTerminal.Core.Storage;

namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
        private sealed class ReflowEngineContext
        {
            private readonly SavedCursorStates _savedCursors;
            private TerminalRow[] _viewport;
            private ScrollbackPages? _scrollback;
            private readonly List<TerminalRow> _history;
            private readonly List<TerminalImage> _images;
            private readonly bool _isAltScreen;
            private readonly TerminalTheme Theme;
            private readonly long _maxScrollbackBytes;
            private int _cursorRow;
            private int _cursorCol;
            private readonly Func<string, int> _getGraphemeWidth;

            public ReflowEngineContext(TerminalBuffer source)
            {
                _savedCursors = source._savedCursors;
                _viewport = source._viewport;
                _scrollback = source._scrollback;
                _history = new List<TerminalRow>(_scrollback.Count + source.Rows);
                _maxScrollbackBytes = source.MaxScrollbackBytes;
                _images = source._images;
                _isAltScreen = source._isAltScreen;
                Theme = source.Theme;
                _cursorRow = source._cursorRow;
                _cursorCol = source._cursorCol;
                _getGraphemeWidth = source.GetGraphemeWidth;
            }

            public void Execute(int oldCols, int oldRows, int newCols, int newRows)
            {
                Reflow(oldCols, oldRows, newCols, newRows);
            }

            public void Apply(TerminalBuffer target)
            {
                target._viewport = _viewport;
                target._scrollback = _scrollback;
                target._cursorRow = _cursorRow;
                target._cursorCol = _cursorCol;
            }

            private int GetGraphemeWidth(string textElement)
            {
                return _getGraphemeWidth(textElement);
            }

            private void Reflow(int oldCols, int oldRows, int newCols, int newRows)
            {
                TerminalRow[]? allPhysicalRows = null;
                (TerminalCell Cell, string? ExtendedText)[]? logicalCells = null;

                try
                {
                    if (newCols <= 0 || newRows <= 0) return;

                    // 1. Capture Cursor Content Pre-Resize
                    int absCursorPhysicalIdx = _scrollback.Count + _cursorRow;
                    int cursorLogicalIdx = -1;
                    int cursorInLogicalOffset = -1;

                    int absMainSavedIdx = _scrollback.Count + _savedCursors.Main.Row;
                    int mainSavedLogicalIdx = -1;
                    int mainSavedInLogicalOffset = -1;

                    int absAltSavedIdx = _scrollback.Count + _savedCursors.Alt.Row;
                    int altSavedLogicalIdx = -1;
                    int altSavedInLogicalOffset = -1;

                    // 2. Physical Extraction with Padding Trim

                    // Calculate how many viewport rows have content
                    int lastActiveVpRow = -1;
                    int actualVpLen = _viewport.Length;
                    for (int i = 0; i < Math.Min(oldRows, actualVpLen); i++)
                    {
                        var row = _viewport[i];
                        bool isEmpty = true;
                        foreach (var cell in row.Cells)
                        {
                            if (cell.Character != ' ' && cell.Character != '\0' || !cell.IsDefaultBackground)
                            {
                                isEmpty = false;
                                break;
                            }
                        }
                        if (!isEmpty || i <= _cursorRow || i == _savedCursors.Main.Row || i == _savedCursors.Alt.Row) lastActiveVpRow = i;
                    }

                    int vpRowsToTake = lastActiveVpRow + 1;
                    int totalPhysRows = _scrollback.Count + vpRowsToTake;

                    // Rent array to avoid LOH/large allocation
                    allPhysicalRows = System.Buffers.ArrayPool<TerminalRow>.Shared.Rent(totalPhysRows);

                    // 3. Metadata-Aware Logical Reconstruction
                    var logicalCellsPool = System.Buffers.ArrayPool<(TerminalCell Cell, string? ExtendedText)>.Shared;
                    int maxLogicalCells = totalPhysRows * Math.Max(oldCols, newCols) + 1000;
                    logicalCells = logicalCellsPool.Rent(maxLogicalCells);
                    int logicalCellsCount = 0;

                    var logicalLines = new List<(int StartIdx, int Length, bool IsWrapped, int StartPhysIdx)>(totalPhysRows);

                    // Fill rented array
                    for (int i = 0; i < _scrollback.Count; i++)
                    {
                        var rowCells = _scrollback.GetRow(i);
                        var row = new TerminalRow(oldCols, Theme.Foreground, Theme.Background);
                        rowCells.CopyTo(row.Cells);
                        row.IsWrapped = _scrollback.IsRowWrapped(i);
                        // NOTE: Extended text is lost in scrollback until Step 5
                        allPhysicalRows[i] = row;
                    }
                    for (int i = 0; i < vpRowsToTake; i++)
                    {
                        if (i < actualVpLen) allPhysicalRows[_scrollback.Count + i] = _viewport[i];
                        else allPhysicalRows[_scrollback.Count + i] = new TerminalRow(oldCols, Theme.Foreground, Theme.Background);
                    }

                    int currentLogStart = -1;
                    int currentStartPhys = -1;

                    // Iterate using totalPhysRows count
                    for (int i = 0; i < totalPhysRows; i++)
                    {
                        var physRow = allPhysicalRows[i];

                        if (currentLogStart == -1)
                        {
                            currentLogStart = logicalCellsCount;
                            currentStartPhys = i;
                        }

                        // Cursor Tracking
                        if (i == absCursorPhysicalIdx)
                        {
                            cursorLogicalIdx = logicalLines.Count;
                            cursorInLogicalOffset = (logicalCellsCount - currentLogStart) + _cursorCol;
                        }

                        if (i == absMainSavedIdx)
                        {
                            mainSavedLogicalIdx = logicalLines.Count;
                            mainSavedInLogicalOffset = (logicalCellsCount - currentLogStart) + _savedCursors.Main.Col;
                        }

                        if (i == absAltSavedIdx)
                        {
                            altSavedLogicalIdx = logicalLines.Count;
                            altSavedInLogicalOffset = (logicalCellsCount - currentLogStart) + _savedCursors.Alt.Col;
                        }

                            int validLen = physRow.Cells.Length;

                            // HEURISTIC: TUI Border Protection
                            // If a line is marked as Wrapped, but it ends with a box-drawing character OR a colored background,
                            // it is likely a fixed-width TUI element that should NOT flow into the next line on resize.
                            bool ignoreWrap = false;
                            if (physRow.IsWrapped)
                            {
                                // 1. Check for TUI Background Fill (at the very edge)
                                // TUI apps often fill the background with a specific color (e.g. blue for MC).
                                // If the last cell has a non-default background AND is a SPACE, it likely hit the edge of a panel.
                                // We must NOT trigger this for regular text (e.g. "Hint" with black BG), or it won't reflow.
                                if (physRow.Cells.Length > 0)
                                {
                                    // Scan backwards for the last actual content (skipping newly allocated nulls from resize)
                                    TerminalCell lastCell = default;
                                    bool found = false;
                                    for (int k = physRow.Cells.Length - 1; k >= 0; k--)
                                    {
                                        if (physRow.Cells[k].Character != '\0')
                                        {
                                            lastCell = physRow.Cells[k];
                                            found = true;
                                            break;
                                        }
                                    }

                                    if (found && lastCell.Character == ' ' && !lastCell.IsDefaultBackground)
                                    {
                                        ignoreWrap = true;
                                    }
                                    else
                                    {
                                        // 2. Check for Border Characters (ignoring trailing spaces)
                                        for (int k = physRow.Cells.Length - 1; k >= 0; k--)
                                        {
                                            var c = physRow.Cells[k];
                                            char ch = c.Character;
                                            if (ch != ' ' && ch != '\0')
                                            {
                                                // Vertical bars, corners, etc.
                                                // U+2500 to U+257F are Box Drawing. 
                                                // U+2580 to U+259F are Block Elements (Full Block, Shades, etc.) used for scrollbars/shadows.
                                                // U+FF00 to U+FFEF are Halfwidth and Fullwidth Forms (includes Fullwidth Pipe U+FF5C).
                                                // '|' is standard vertical bar (U+007C).
                                                // '+' and '-' can be ASCII borders.
                                                // '>' is often used by MC to indicate horizontal scroll overflow.
                                                if (ch == '|' || ch == '+' || ch == '-' || ch == '>' ||
                                                   (ch >= '\u2500' && ch <= '\u257F') ||
                                                   (ch >= '\u2580' && ch <= '\u259F') ||
                                                   (ch >= '\uFF00' && ch <= '\uFFEF'))
                                                {
                                                    ignoreWrap = true;
                                                }
                                                // Special Case: MC Headers like ".[^]" often end with ']' or '^'.
                                                // These are text characters, so we can't protect them globally (would break text wrapping).
                                                // However, in TUI headers, they typically have a specific background color.
                                                // Also protect Arrows '↑' (U+2191) and '↓' (U+2193) which are sometimes used as sort indicators.
                                                else if ((ch == ']' || ch == '[' || ch == '^' || ch == '\u2191' || ch == '\u2193') && !c.IsDefaultBackground)
                                                {
                                                    ignoreWrap = true;
                                                }
                                                break;
                                            }
                                        }
                                    }
                                }
                            }

                            if (!physRow.IsWrapped || ignoreWrap)
                            {
                                // Smart trimming: Calculate last relevant content index
                                // Include cells that are non-space OR have non-default background
                                int lastContentIdx = -1;
                                for (int scan = 0; scan < physRow.Cells.Length; scan++)
                                {
                                    var cell = physRow.Cells[scan];
                                    if ((cell.Character != ' ' && cell.Character != '\0') || !cell.IsDefaultBackground || cell.HasExtendedText)
                                    {
                                        lastContentIdx = scan;
                                    }
                                }

                                // Determine valid length based on content
                                if (lastContentIdx >= 0)
                                {
                                    validLen = lastContentIdx + 1;
                                }
                                else
                                {
                                    validLen = 0;
                                }

                                // Special case: Preserve padding up to the cursor if it's on this row
                                if (i == absCursorPhysicalIdx && _cursorCol > validLen)
                                {
                                    validLen = _cursorCol;
                                }
                            }

                            // Improved sparse row detection: Find the LARGEST contiguous gap
                            // This preserves middle content (e.g. "Left ... Middle ... Right")
                            bool isSparseRowRepositioned = false;
                            if (!physRow.IsWrapped && i >= Math.Max(0, absCursorPhysicalIdx - 2) && i <= absCursorPhysicalIdx)
                            {
                                // Find largest gap strictly BETWEEN content
                                // We need to know if the gap is followed by content, otherwise it's just trailing space
                                // Scan logic:
                                // 1. Identify all gaps.
                                // 2. Identify the gap that is:
                                //    a) Large (> 10)
                                //    b) Followed by content (not end of line)
                                //    c) The largest such gap in the row

                                int bestGapStart = -1;
                                int bestGapLength = 0;

                                int currentScanStart = -1;
                                int currentScanLength = 0;

                                // First we need to find the "end of row content" to ignore trailing spaces
                                int lastContentIndex = -1;
                                for (int scan = physRow.Cells.Length - 1; scan >= 0; scan--)
                                {
                                    var cell = physRow.Cells[scan];
                                    if (cell.Character != ' ' && cell.Character != '\0')
                                    {
                                        lastContentIndex = scan;
                                        break;
                                    }
                                }

                                if (lastContentIndex > 0)
                                {
                                    // ONLY Scan up to lastContentIndex
                                    // This ensures any gap we find implies there is content AFTER it.
                                    for (int scan = 0; scan <= lastContentIndex; scan++)
                                    {
                                        var cell = physRow.Cells[scan];
                                        bool isSpace = (cell.Character == ' ' || cell.Character == '\0');

                                        if (isSpace)
                                        {
                                            if (currentScanStart == -1) currentScanStart = scan;
                                            currentScanLength++;
                                        }
                                        else
                                        {
                                            if (currentScanStart != -1)
                                            {
                                                if (currentScanLength > bestGapLength)
                                                {
                                                    bestGapLength = currentScanLength;
                                                    bestGapStart = currentScanStart;
                                                }
                                                currentScanStart = -1;
                                                currentScanLength = 0;
                                            }
                                        }
                                    }
                                    // Check gap if content resumes exactly at lastContentIndex? handled by loop
                                    // The loop stops AT lastContentIndex. If the character at lastContentIndex is content,
                                    // the else block triggers and we check the gap before it. Correct.
                                }

                                // Determine threshold for gap
                                // Standard: 10 spaces
                                // Special: 2 spaces IF the content touches the right edge (implies a shrunk right-prompt)
                                bool isRightPinned = lastContentIndex == physRow.Cells.Length - 1;
                                int gapThreshold = isRightPinned ? 2 : 10;

                                if (bestGapLength >= gapThreshold)
                                {
                                    // We found a split!
                                    // Left+Middle = 0 .. bestGapStart (exclusive)
                                    // Gap = bestGapStart .. bestGapStart + bestGapLength
                                    // Right = bestGapStart + bestGapLength .. lastContentIndex (inclusive)

                                    int gapStart = bestGapStart;
                                    int gapEnd = bestGapStart + bestGapLength;
                                    int rightStart = gapEnd;
                                    int rightEnd = lastContentIndex;

                                    // Extract Left+Middle
                                    for (int k = 0; k < gapStart; k++)
                                    {
                                        logicalCells[logicalCellsCount++] = (physRow.Cells[k], physRow.GetExtendedText(k));
                                    }

                                    // Calculate new position
                                    int rightBlockWidth = rightEnd - rightStart + 1;
                                    int newRightPos = newCols - rightBlockWidth;

                                    int currentPos = logicalCellsCount - currentLogStart; // This is effectively gapStart

                                    if (newRightPos > currentPos + 2 && (newRightPos + rightBlockWidth) <= newCols)
                                    {
                                        // Fill spaces
                                        var spaceFill = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                                        for (int s = currentPos; s < newRightPos; s++)
                                        {
                                            logicalCells[logicalCellsCount++] = (spaceFill, null);
                                        }
                                        // Add right content
                                        for (int k = rightStart; k <= rightEnd; k++)
                                        {
                                            logicalCells[logicalCellsCount++] = (physRow.Cells[k], physRow.GetExtendedText(k));
                                        }
                                    }
                                    else
                                    {
                                        // Truncate/Squish
                                        var spaceFill = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                                        logicalCells[logicalCellsCount++] = (spaceFill, null);
                                        logicalCells[logicalCellsCount++] = (spaceFill, null);

                                        int available = newCols - (logicalCellsCount - currentLogStart);
                                        if (available > 0)
                                        {
                                            int take = Math.Min(available, rightBlockWidth);
                                            int startOffset = rightBlockWidth - take;
                                            for (int k = rightStart + startOffset; k <= rightEnd; k++)
                                                logicalCells[logicalCellsCount++] = (physRow.Cells[k], physRow.GetExtendedText(k));
                                        }
                                    }
                                    isSparseRowRepositioned = true;
                                }

                            }

                            // Normal processing if not sparse row or repositioning failed
                            if (!isSparseRowRepositioned)
                            {
                                for (int k = 0; k < validLen; k++)
                                    logicalCells[logicalCellsCount++] = (physRow.Cells[k], physRow.GetExtendedText(k));
                            }

                            if (!physRow.IsWrapped || ignoreWrap)
                            {
                                logicalLines.Add((currentLogStart, logicalCellsCount - currentLogStart, false, currentStartPhys));
                                currentLogStart = -1;
                            }
                        }

                        if (currentLogStart != -1)
                        {
                            logicalLines.Add((currentLogStart, logicalCellsCount - currentLogStart, true, currentStartPhys));
                        }
                    // 5. Distribution logic
                    _scrollback.Clear();
                    _viewport = new TerminalRow[newRows];
                    // Pre-allocate for the typical case of 1.2x expansion due to wrapping
                    var allFlowedRows = new List<TerminalRow>((int)(logicalLines.Count * 1.2));

                    int newCursorPhysRow = -1;
                    int newCursorPhysCol = -1;
                    int newMainSavedPhysRow = -1;
                    int newMainSavedPhysCol = -1;
                    int newAltSavedPhysRow = -1;
                    int newAltSavedPhysCol = -1;
                    int historyRowCount = 0; // Tracks physical rows generated from original history
                    var newStartFlowIndices = new int[logicalLines.Count];

                    // 5b. Anchor Images to Logical Positions before Reflow
                    var imageAnchors = new List<(TerminalImage Image, int LogicalLineIdx, int OffsetInLogicalLine)>();
                    for (int imgIdx = 0; imgIdx < _images.Count; imgIdx++)
                    {
                        var img = _images[imgIdx];
                        if (img == null) continue;

                        // Find which logical line contains img.CellY
                        for (int idx = 0; idx < logicalLines.Count; idx++)
                        {
                            var start = logicalLines[idx].StartPhysIdx;
                            var end = (idx + 1 < logicalLines.Count) ? logicalLines[idx + 1].StartPhysIdx : allPhysicalRows.Length;
                            if (img.CellY >= start && img.CellY < end)
                            {
                                int rowOffset = img.CellY - start;
                                int offsetInLine = rowOffset * oldCols + img.CellX;
                                imageAnchors.Add((img, idx, offsetInLine));
                                break;
                            }
                        }
                    }

                    // Identify the logical line index that starts the viewport
                    // The first viewport row in 'allPhysicalRows' was at index 'oldScrollbackCount'
                    // We need to find the first logical line that includes 'oldScrollbackCount' or higher.
                    int firstViewportLogicalIdx = logicalLines.Count; // Default to end
                    int oldScrollbackCount = absCursorPhysicalIdx - _cursorRow; // Re-derive or pass in? 
                                                                                // Better to capture oldScrollbackCount at the start of Reflow.
                                                                                // But we can infer it: absCursorPhysicalIdx is _scrollback.Count + _cursorRow.
                                                                                // So _scrollback.Count = absCursorPhysicalIdx - _cursorRow.
                                                                                // Wait, absCursorPhysicalIdx is calculated using CURRENT _cursorRow and _scrollback.Count.
                                                                                // So yes, that works.
                    int splitPhysIndex = absCursorPhysicalIdx - _cursorRow;

                    // Find first logical line that starts at or after splitPhysIndex
                    for (int i = 0; i < logicalLines.Count; i++)
                    {
                        if (logicalLines[i].StartPhysIdx >= splitPhysIndex)
                        {
                            firstViewportLogicalIdx = i;
                            break;
                        }
                    }

                    for (int i = 0; i < logicalLines.Count; i++)
                    {
                        var lineInfo = logicalLines[i];
                        int lineStart = lineInfo.StartIdx;
                        int lineCount = lineInfo.Length;

                        // Track start of this logical line in flowed rows
                        int startFlowIndex = allFlowedRows.Count;

                        if (lineCount == 0)
                        {
                            // If this is the WIPED prompt, place cursor here
                            if (i == cursorLogicalIdx) { newCursorPhysRow = allFlowedRows.Count; newCursorPhysCol = 0; }
                            if (i == mainSavedLogicalIdx) { newMainSavedPhysRow = allFlowedRows.Count; newMainSavedPhysCol = 0; }
                            if (i == altSavedLogicalIdx) { newAltSavedPhysRow = allFlowedRows.Count; newAltSavedPhysCol = 0; }
                            allFlowedRows.Add(new TerminalRow(newCols, Theme.Foreground, Theme.Background));
                        }
                        else
                        {
                            int processed = 0;
                            while (processed < lineCount)
                            {
                                int remaining = lineCount - processed;
                                int take = Math.Min(remaining, newCols);

                                // Prevent splitting a wide character across lines
                                if (take < remaining && take > 0 && logicalCells[lineStart + processed + take - 1].Cell.IsWide)
                                {
                                    take--; // This row will end with a space, wide char moves to next row
                                }

                                // If take is 0 but we have remaining (newCols is 1 and we have a wide char),
                                // we're forced to just take it and let it be clipped, otherwise infinite loop.
                                if (take == 0 && remaining > 0) take = 1;

                                // Mapping
                                if (i == cursorLogicalIdx)
                                {
                                    if (cursorInLogicalOffset >= processed && cursorInLogicalOffset < processed + newCols)
                                    {
                                        newCursorPhysRow = allFlowedRows.Count;
                                        newCursorPhysCol = cursorInLogicalOffset - processed;
                                    }
                                    else if (cursorInLogicalOffset == processed + newCols && remaining == newCols)
                                    {
                                        newCursorPhysRow = allFlowedRows.Count;
                                        newCursorPhysCol = newCols;
                                    }
                                }

                                if (i == mainSavedLogicalIdx)
                                {
                                    if (mainSavedInLogicalOffset >= processed && mainSavedInLogicalOffset < processed + newCols)
                                    {
                                        newMainSavedPhysRow = allFlowedRows.Count;
                                        newMainSavedPhysCol = mainSavedInLogicalOffset - processed;
                                    }
                                }

                                if (i == altSavedLogicalIdx)
                                {
                                    if (altSavedInLogicalOffset >= processed && altSavedInLogicalOffset < processed + newCols)
                                    {
                                        newAltSavedPhysRow = allFlowedRows.Count;
                                        newAltSavedPhysCol = altSavedInLogicalOffset - processed;
                                    }
                                }

                                var row = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                                for (int c = 0; c < take; c++)
                                {
                                    var entry = logicalCells[lineStart + processed + c];
                                    row.Cells[c] = entry.Cell;
                                    row.SetExtendedText(c, entry.ExtendedText);
                                }

                                // Style-Aware Padding
                                if (take < newCols)
                                {
                                    // We use TRUE default style for padding, NOT the last character's style.
                                    // This prevents "Background Leakage" (e.g. blue/green bars) when resizing.
                                    var def = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                                    for (int c = take; c < newCols; c++) row.Cells[c] = def;
                                }

                                if (remaining > newCols) row.IsWrapped = true;
                                allFlowedRows.Add(row);
                                processed += take;
                            }
                        }

                        // If this line belongs to history (before viewport start), add its generated rows to count
                        if (i < firstViewportLogicalIdx)
                        {
                            historyRowCount += (allFlowedRows.Count - startFlowIndex);
                        }

                        newStartFlowIndices[i] = startFlowIndex;
                    }

                    // 6. Final Layout (Anchor-to-Top of Viewport)
                    // We want _scrollback to contain AT LEAST 'historyRowCount'.
                    // But if the remaining lines (viewport content) > newRows, we must push some of them to SB (Shrink).
                    int total = allFlowedRows.Count;

                    // Base split: Everything that was history stays history.
                    int sbCount = historyRowCount;

                    // Shrink Adjustment: If active content doesn't fit in new viewport, overflow goes to SB.
                    int activeContentSize = total - historyRowCount;
                    if (activeContentSize > newRows)
                    {
                        sbCount += (activeContentSize - newRows);
                    }

                    // Ensure safety
                    sbCount = Math.Clamp(sbCount, 0, total);
                    int vpCount = total - sbCount;

                    // Create new ScrollbackPages instance
                    var newScrollback = new ScrollbackPages(newCols, _sharedPagePool, _maxScrollbackBytes);
                    
                    long prevEvicted = 0;
                    for (int i = 0; i < sbCount; i++)
                    {
                        newScrollback.AppendRow(allFlowedRows[i].Cells, allFlowedRows[i].IsWrapped);
                    }
                    
                    int discardedRows = (int)newScrollback.TotalRowsEvicted;
                    _scrollback = newScrollback;

                    // Fill viewport
                    // If vpCount < newRows (Growth), we will have empty space at the bottom (Top Anchoring).
                    int vIdx = 0;
                    for (int i = 0; i < vpCount; i++) _viewport[vIdx++] = allFlowedRows[sbCount + i]; // Offset by updated sbCount

                    // Pad remaining viewport rows (at the BOTTOM now)
                    while (vIdx < newRows)
                    {
                        _viewport[vIdx++] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                    }

                    // 7. Restore Cursor
                    if (newCursorPhysRow != -1)
                    {
                        // newCursorPhysRow is absolute index in allFlowedRows
                        // We need to map it to viewport relative.
                        // It might be in scrollback now!
                        if (newCursorPhysRow < sbCount)
                        {
                            // Cursor pushed to scrollback?
                            // We must clamp it to 0? Or keep it?
                            // TerminalBuffer usually keeps cursor in Viewport.
                            // But if we shrank so much the cursor is gone... 
                            // We forcibly scroll? Or just clamp to top?
                            _cursorRow = 0;
                            // _scrollOffset adjustment would be needed here to keep it in view, but simplest is clamp.
                        }
                        else
                        {
                            _cursorRow = newCursorPhysRow - sbCount;
                        }
                        _cursorCol = Math.Clamp(newCursorPhysCol, 0, newCols);
                    }
                    else
                    {
                        _cursorRow = newRows - 1;
                        _cursorCol = 0;
                    }

                    // 8. Reposition Images
                    for (int i = _images.Count - 1; i >= 0; i--)
                    {
                        var img = _images[i];
                        if (img == null) continue;

                        // Find anchor
                        var anchor = imageAnchors.FirstOrDefault(a => a.Image == img);
                        if (anchor.Image != null && anchor.LogicalLineIdx >= 0 && anchor.LogicalLineIdx < newStartFlowIndices.Length)
                        {
                            int newY = newStartFlowIndices[anchor.LogicalLineIdx] + (anchor.OffsetInLogicalLine / newCols);
                            img.CellY = newY - discardedRows;
                            img.CellX = anchor.OffsetInLogicalLine % newCols;

                            // Prune if shifted out of history bounds
                            if (img.CellY + img.CellHeight < 0)
                            {
                                _images.RemoveAt(i);
                            }
                        }
                    }

                    // 7b. Restore Saved Cursors
                    if (newMainSavedPhysRow != -1)
                    {
                        if (newMainSavedPhysRow < sbCount) _savedCursors.Main.Row = 0;
                        else _savedCursors.Main.Row = newMainSavedPhysRow - sbCount;
                        _savedCursors.Main.Col = Math.Clamp(newMainSavedPhysCol, 0, newCols);
                    }
                    if (newAltSavedPhysRow != -1)
                    {
                        if (newAltSavedPhysRow < sbCount) _savedCursors.Alt.Row = 0;
                        else _savedCursors.Alt.Row = newAltSavedPhysRow - sbCount;
                        _savedCursors.Alt.Col = Math.Clamp(newAltSavedPhysCol, 0, newCols);
                    }

                    // 8. Conditional Cursor Row Clearing (REFINED)
                    // Clear ONLY truly empty padding rows on horizontal resize, not actual wrapped content.
                    // This prevents duplication in CMD while preserving oh-my-posh sparse prompts in PowerShell.
                    // 
                    // Rationale:
                    // - Horizontal resize: Width changes cause line rewrapping. Some shells may duplicate prompts.
                    // - We only clear rows that are confirmed empty, not rows with actual content.
                    // - This preserves oh-my-posh right-aligned content that wraps to the next row.
                    if (newCols != oldCols)
                    {
                        if (_cursorRow >= 0 && _cursorRow < newRows && _cursorRow + 1 < newRows)
                        {
                            var nextRow = _viewport[_cursorRow + 1];

                            // Only clear if:
                            // 1. Row is NOT wrapped (not a continuation of a wrapped line)
                            // 2. Row is completely empty (no non-space content)
                            if (!nextRow.IsWrapped)
                            {
                                // Check if row has any actual content
                                bool hasContent = false;
                                for (int c = 0; c < nextRow.Cells.Length; c++)
                                {
                                    if (nextRow.Cells[c].Character != ' ' && nextRow.Cells[c].Character != '\0')
                                    {
                                        hasContent = true;
                                        break;
                                    }
                                }

                                // Only clear if truly empty (no content)
                                if (!hasContent)
                                {
                                    _viewport[_cursorRow + 1] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                                }
                            }
                        }
                    }

                    // Explicitly check for cursor column overflow (Reflow safety)
                    _cursorCol = Math.Clamp(_cursorCol, 0, newCols);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText("error.log", "\n--- Reflow Exception at " + DateTime.Now + " ---\n" + ex.ToString() + "\n"); } catch { }
                    // Failsafe: if reflow crashes, we just reset the buffer to a clean state to avoid permanent hang
                    _scrollback = new ScrollbackPages(newCols, _sharedPagePool, _maxScrollbackBytes);
                    _viewport = new TerminalRow[newRows];
                    for (int i = 0; i < newRows; i++) _viewport[i] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                    _cursorRow = 0;
                    _cursorCol = 0;
                }
            }
        }
    }
}
