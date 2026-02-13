using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public struct RenderCellSnapshot
    {
        public char Character;
        public string? Text;
        public Color Foreground;
        public Color Background;
        public bool IsInverse;
        public bool IsBold;
        public bool IsDefaultForeground;
        public bool IsDefaultBackground;
        public bool IsWide;
        public bool IsWideContinuation;
        public bool IsHidden;
        public short FgIndex;
        public short BgIndex;
    }

    public struct RenderRowSnapshot
    {
        public int AbsRow;
        public uint Revision;
        public int Cols;
        public RenderCellSnapshot[] Cells;
    }

    public struct RenderImageSnapshot
    {
        public int CellX;
        public int CellY;
        public int CellWidth;
        public int CellHeight;
        public SKImage Image;
        public bool IsSticky;
    }
}
