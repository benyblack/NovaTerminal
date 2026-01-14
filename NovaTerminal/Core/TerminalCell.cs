using Avalonia.Media;

namespace NovaTerminal.Core
{
    public struct TerminalCell
    {
        public char Character;
        public Color Foreground;
        public Color Background;
        public bool IsBold;
        public bool IsInverse;
        public bool IsDefaultForeground;
        public bool IsDefaultBackground;
        public bool IsDirty;

        public static TerminalCell Default => new TerminalCell 
        { 
            Character = ' ', 
            Foreground = Colors.LightGray, 
            Background = Colors.Black,
            IsBold = false,
            IsInverse = false,
            IsDefaultForeground = true,
            IsDefaultBackground = true,
            IsDirty = true         
        };

        public TerminalCell(char c, Color fg, Color bg, bool isInverse = false, bool isBold = false, bool isDefaultFg = false, bool isDefaultBg = false)
        {
            Character = c;
            Foreground = fg;
            Background = bg;
            IsInverse = isInverse;
            IsBold = isBold;
            IsDefaultForeground = isDefaultFg;
            IsDefaultBackground = isDefaultBg;
            IsDirty = true;
        }
    }
}
