

namespace NovaTerminal.Core
{
    public struct TerminalCell
    {
        public char Character;
        public string? Text; // For complex graphemes (emojis, etc.)
        public TermColor Foreground;
        public TermColor Background;
        public bool IsBold;
        public bool IsInverse;
        public bool IsDefaultForeground;
        public bool IsDefaultBackground;
        public bool IsDirty;
        public bool IsWide; // Takes 2 columns
        public bool IsWideContinuation; // Placeholder for the 2nd column of a wide char
        public bool IsHidden;
        public bool IsFaint;
        public bool IsItalic;
        public bool IsUnderline;
        public bool IsBlink;
        public bool IsStrikethrough;

        public short FgIndex; // -1 = TrueColor/Default, 0-255 = Palette
        public short BgIndex; // -1 = TrueColor/Default, 0-255 = Palette

        public static TerminalCell Default => new TerminalCell
        {
            Character = ' ',
            Text = null,
            Foreground = TermColor.LightGray,
            Background = TermColor.Black,
            IsBold = false,
            IsInverse = false,
            IsDefaultForeground = true,
            IsDefaultBackground = true,
            IsDirty = true,
            IsWide = false,
            IsWideContinuation = false,
            IsHidden = false,
            IsFaint = false,
            IsItalic = false,
            IsUnderline = false,
            IsBlink = false,
            IsStrikethrough = false,
            FgIndex = -1,
            BgIndex = -1
        };

        public TerminalCell(char c, TermColor fg, TermColor bg, bool isInverse = false, bool isBold = false, bool isDefaultFg = false, bool isDefaultBg = false, bool isHidden = false, short fgIdx = -1, short bgIdx = -1, bool isWide = false, bool isFaint = false, bool isItalic = false, bool isUnderline = false, bool isBlink = false, bool isStrikethrough = false)
        {
            Character = c;
            Text = null;
            Foreground = fg;
            Background = bg;
            IsInverse = isInverse;
            IsBold = isBold;
            IsDefaultForeground = isDefaultFg;
            IsDefaultBackground = isDefaultBg;
            IsDirty = true;
            IsWide = isWide;
            IsWideContinuation = false;
            IsHidden = isHidden;
            FgIndex = fgIdx;
            BgIndex = bgIdx;
            IsFaint = isFaint;
            IsItalic = isItalic;
            IsUnderline = isUnderline;
            IsBlink = isBlink;
            IsStrikethrough = isStrikethrough;
        }

        public TerminalCell(string text, TermColor fg, TermColor bg, bool isInverse = false, bool isBold = false, bool isDefaultFg = false, bool isDefaultBg = false, bool isHidden = false, short fgIdx = -1, short bgIdx = -1, bool isWide = false, bool isFaint = false, bool isItalic = false, bool isUnderline = false, bool isBlink = false, bool isStrikethrough = false)
        {
            Character = text.Length > 0 ? text[0] : ' ';
            Text = text;
            Foreground = fg;
            Background = bg;
            IsInverse = isInverse;
            IsBold = isBold;
            IsDefaultForeground = isDefaultFg;
            IsDefaultBackground = isDefaultBg;
            IsDirty = true;
            IsWide = isWide;
            IsWideContinuation = false;
            IsHidden = isHidden;
            FgIndex = fgIdx;
            BgIndex = bgIdx;
            IsFaint = isFaint;
            IsItalic = isItalic;
            IsUnderline = isUnderline;
            IsBlink = isBlink;
            IsStrikethrough = isStrikethrough;
        }
    }
}
