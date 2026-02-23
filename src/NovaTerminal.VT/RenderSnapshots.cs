namespace NovaTerminal.Core
{
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
