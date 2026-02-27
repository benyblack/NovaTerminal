using System;
using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public readonly struct RenderSnapshotRequest
    {
        public int ViewportRows { get; init; }
        public int ViewportCols { get; init; }
        public int ScrollOffset { get; init; }
        public SelectionState? Selection { get; init; }
        public IReadOnlyList<SearchMatch>? SearchMatches { get; init; }
        public int ActiveSearchIndex { get; init; }
    }

    public readonly struct RenderThemeSnapshot
    {
        public TermColor Foreground { get; init; }
        public TermColor Background { get; init; }
        public TermColor CursorColor { get; init; }
        public TermColor[] AnsiPalette { get; init; }
    }

    public readonly struct DirtySpan
    {
        // End-exclusive range: [ColStart, ColEnd)
        public int Row { get; init; }
        public int ColStart { get; init; }
        public int ColEnd { get; init; }
    }

    public readonly struct SelectionRowSnapshot
    {
        public int AbsRow { get; init; }
        public int ColStart { get; init; }
        public int ColEnd { get; init; }
    }

    public readonly struct SearchHighlightSnapshot
    {
        public int AbsRow { get; init; }
        public int StartCol { get; init; }
        public int EndCol { get; init; }
        public bool IsActive { get; init; }
    }

    public readonly struct TerminalRenderSnapshot
    {
        public int ViewportRows { get; init; }
        public int ViewportCols { get; init; }
        public int TotalLines { get; init; }
        public int AbsDisplayStart { get; init; }
        public int ScrollOffset { get; init; }
        public int CursorRow { get; init; }
        public int CursorCol { get; init; }
        public CursorStyle CursorStyle { get; init; }
        public RenderThemeSnapshot Theme { get; init; }
        public DirtySpan[] DirtySpans { get; init; }
        public RenderRowSnapshot[] RowsData { get; init; }
        public RenderImageSnapshot[] Images { get; init; }
        public SelectionRowSnapshot[] SelectionRows { get; init; }
        public SearchHighlightSnapshot[] SearchHighlights { get; init; }
    }

    public struct RenderCellSnapshot
    {
        public char Character;
        public string? Text;
        public TermColor Foreground;
        public TermColor Background;
        public bool IsInverse;
        public bool IsBold;
        public bool IsDefaultForeground;
        public bool IsDefaultBackground;
        public bool IsWide;
        public bool IsWideContinuation;
        public bool IsHidden;
        public bool IsFaint;
        public bool IsItalic;
        public bool IsUnderline;
        public bool IsBlink;
        public bool IsStrikethrough;
        public short FgIndex;
        public short BgIndex;
    }

    public struct RenderRowSnapshot
    {
        public int AbsRow;
        public uint Revision;
        public int Cols;
        public RenderCellSnapshot[] Cells;
        public long RowId;
    }

    public struct RenderImageSnapshot
    {
        public int CellX;
        public int CellY;
        public int CellWidth;
        public int CellHeight;
        public object ImageHandle;
        public bool IsSticky;
    }
}
