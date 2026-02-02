using Avalonia.Media;

namespace NovaTerminal.Core
{
    public struct TerminalCell
    {
        public char Character;
        public string? Text; // For complex graphemes (emojis, etc.)
        public Color Foreground;
        public Color Background;
        public bool IsBold;
        public bool IsInverse;
        public bool IsDefaultForeground;
        public bool IsDefaultBackground;
        public bool IsDirty;
        public bool IsWide; // Takes 2 columns
        public bool IsWideContinuation; // Placeholder for the 2nd column of a wide char
        public bool IsHidden;

        public static TerminalCell Default => new TerminalCell
        {
            Character = ' ',
            Text = null,
            Foreground = Colors.LightGray,
            Background = Colors.Black,
            IsBold = false,
            IsInverse = false,
            IsDefaultForeground = true,
            IsDefaultBackground = true,
            IsDirty = true,
            IsWide = false,
            IsWideContinuation = false,
            IsHidden = false
        };

        public TerminalCell(char c, Color fg, Color bg, bool isInverse = false, bool isBold = false, bool isDefaultFg = false, bool isDefaultBg = false, bool isHidden = false)
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
            IsWide = false;
            IsWideContinuation = false;
            IsHidden = isHidden;
        }

        public TerminalCell(string text, Color fg, Color bg, bool isInverse = false, bool isBold = false, bool isDefaultFg = false, bool isDefaultBg = false, bool isHidden = false)
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
            IsWide = true; // Assume explicit string ctor is for complex/wide chars
            IsWideContinuation = false;
            IsHidden = isHidden;
        }
    }
}
