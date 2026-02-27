

namespace NovaTerminal.Core
{
    [System.Flags]
    public enum TerminalCellFlags : ushort
    {
        None = 0,
        Bold = 1 << 0,
        Inverse = 1 << 1,
        Italic = 1 << 2,
        Underline = 1 << 3,
        Strikethrough = 1 << 4,
        Blink = 1 << 5,
        Faint = 1 << 6,
        Hidden = 1 << 7,
        DefaultForeground = 1 << 8,
        DefaultBackground = 1 << 9,
        Dirty = 1 << 10,
        Wide = 1 << 11,
        WideContinuation = 1 << 12,
        HasExtendedText = 1 << 13,
        PaletteForeground = 1 << 14,
        PaletteBackground = 1 << 15
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct TerminalCell
    {
        internal const string TerminalCellLayoutId = "TerminalCell/v1";

        public char Character;
        public ushort Flags; // TerminalCellFlags bitmask
        public uint Fg; // RGB 0xRRGGBB or Palette Index
        public uint Bg; // RGB 0xRRGGBB or Palette Index

        public TerminalCell(char c, uint fg, uint bg, ushort flags)
        {
            Character = c;
            Fg = fg;
            Bg = bg;
            Flags = flags;
        }

        public TerminalCell(char c, TermColor fg, TermColor bg, bool isInverse = false, bool isBold = false, bool isDefaultFg = false, bool isDefaultBg = false, bool isHidden = false, short fgIdx = -1, short bgIdx = -1, bool isWide = false, bool isFaint = false, bool isItalic = false, bool isUnderline = false, bool isBlink = false, bool isStrikethrough = false)
        {
            Character = c;
            Flags = (ushort)TerminalCellFlags.Dirty;
            Fg = fg.ToUint();
            Bg = bg.ToUint();

            if (isInverse) Flags |= (ushort)TerminalCellFlags.Inverse;
            if (isBold) Flags |= (ushort)TerminalCellFlags.Bold;
            if (isDefaultFg) Flags |= (ushort)TerminalCellFlags.DefaultForeground;
            if (isDefaultBg) Flags |= (ushort)TerminalCellFlags.DefaultBackground;
            if (isHidden) Flags |= (ushort)TerminalCellFlags.Hidden;
            if (isWide) Flags |= (ushort)TerminalCellFlags.Wide;
            if (isFaint) Flags |= (ushort)TerminalCellFlags.Faint;
            if (isItalic) Flags |= (ushort)TerminalCellFlags.Italic;
            if (isUnderline) Flags |= (ushort)TerminalCellFlags.Underline;
            if (isBlink) Flags |= (ushort)TerminalCellFlags.Blink;
            if (isStrikethrough) Flags |= (ushort)TerminalCellFlags.Strikethrough;

            if (fgIdx >= 0) { Flags |= (ushort)TerminalCellFlags.PaletteForeground; Fg = (uint)fgIdx; }
            if (bgIdx >= 0) { Flags |= (ushort)TerminalCellFlags.PaletteBackground; Bg = (uint)bgIdx; }
        }

        // Helper properties to maintain source compatibility
        public bool IsBold { get => (Flags & (ushort)TerminalCellFlags.Bold) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.Bold; else Flags &= (ushort)~TerminalCellFlags.Bold; } }
        public bool IsInverse { get => (Flags & (ushort)TerminalCellFlags.Inverse) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.Inverse; else Flags &= (ushort)~TerminalCellFlags.Inverse; } }
        public bool IsItalic { get => (Flags & (ushort)TerminalCellFlags.Italic) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.Italic; else Flags &= (ushort)~TerminalCellFlags.Italic; } }
        public bool IsUnderline { get => (Flags & (ushort)TerminalCellFlags.Underline) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.Underline; else Flags &= (ushort)~TerminalCellFlags.Underline; } }
        public bool IsStrikethrough { get => (Flags & (ushort)TerminalCellFlags.Strikethrough) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.Strikethrough; else Flags &= (ushort)~TerminalCellFlags.Strikethrough; } }
        public bool IsBlink { get => (Flags & (ushort)TerminalCellFlags.Blink) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.Blink; else Flags &= (ushort)~TerminalCellFlags.Blink; } }
        public bool IsFaint { get => (Flags & (ushort)TerminalCellFlags.Faint) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.Faint; else Flags &= (ushort)~TerminalCellFlags.Faint; } }
        public bool IsHidden { get => (Flags & (ushort)TerminalCellFlags.Hidden) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.Hidden; else Flags &= (ushort)~TerminalCellFlags.Hidden; } }
        public bool IsDefaultForeground { get => (Flags & (ushort)TerminalCellFlags.DefaultForeground) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.DefaultForeground; else Flags &= (ushort)~TerminalCellFlags.DefaultForeground; } }
        public bool IsDefaultBackground { get => (Flags & (ushort)TerminalCellFlags.DefaultBackground) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.DefaultBackground; else Flags &= (ushort)~TerminalCellFlags.DefaultBackground; } }
        public bool IsDirty { get => (Flags & (ushort)TerminalCellFlags.Dirty) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.Dirty; else Flags &= (ushort)~TerminalCellFlags.Dirty; } }
        public bool IsWide { get => (Flags & (ushort)TerminalCellFlags.Wide) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.Wide; else Flags &= (ushort)~TerminalCellFlags.Wide; } }
        public bool IsWideContinuation { get => (Flags & (ushort)TerminalCellFlags.WideContinuation) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.WideContinuation; else Flags &= (ushort)~TerminalCellFlags.WideContinuation; } }
        public bool HasExtendedText { get => (Flags & (ushort)TerminalCellFlags.HasExtendedText) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.HasExtendedText; else Flags &= (ushort)~TerminalCellFlags.HasExtendedText; } }
        public bool IsPaletteForeground { get => (Flags & (ushort)TerminalCellFlags.PaletteForeground) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.PaletteForeground; else Flags &= (ushort)~TerminalCellFlags.PaletteForeground; } }
        public bool IsPaletteBackground { get => (Flags & (ushort)TerminalCellFlags.PaletteBackground) != 0; set { if (value) Flags |= (ushort)TerminalCellFlags.PaletteBackground; else Flags &= (ushort)~TerminalCellFlags.PaletteBackground; } }

        public short FgIndex { get => IsPaletteForeground ? (short)(Fg & 0xFFFF) : (short)-1; set { if (value >= 0) { IsPaletteForeground = true; Fg = (uint)value; } else { IsPaletteForeground = false; } } }
        public short BgIndex { get => IsPaletteBackground ? (short)(Bg & 0xFFFF) : (short)-1; set { if (value >= 0) { IsPaletteBackground = true; Bg = (uint)value; } else { IsPaletteBackground = false; } } }

        public TermColor Foreground { get => TermColor.FromUint(Fg); set { Fg = value.ToUint(); IsPaletteForeground = false; IsDefaultForeground = false; } }
        public TermColor Background { get => TermColor.FromUint(Bg); set { Bg = value.ToUint(); IsPaletteBackground = false; IsDefaultBackground = false; } }

        public static TerminalCell Default => new TerminalCell
        {
            Character = ' ',
            Flags = (ushort)(TerminalCellFlags.Dirty | TerminalCellFlags.DefaultForeground | TerminalCellFlags.DefaultBackground),
            Fg = 0,
            Bg = 0
        };

        // Dummy constructor for string compatibility during migration
        public TerminalCell(string text, TermColor fg, TermColor bg, bool isInverse = false, bool isBold = false, bool isDefaultFg = false, bool isDefaultBg = false, bool isHidden = false, short fgIdx = -1, short bgIdx = -1, bool isWide = false, bool isFaint = false, bool isItalic = false, bool isUnderline = false, bool isBlink = false, bool isStrikethrough = false)
            : this(text.Length > 0 ? text[0] : ' ', fg, bg, isInverse, isBold, isDefaultFg, isDefaultBg, isHidden, fgIdx, bgIdx, isWide, isFaint, isItalic, isUnderline, isBlink, isStrikethrough)
        {
            if (text.Length > 1 || (text.Length == 1 && char.IsSurrogate(text[0])))
            {
                Flags |= (ushort)TerminalCellFlags.HasExtendedText;
                // Note: The actual string storage happens in TerminalBuffer side table
            }
        }
    }
}
