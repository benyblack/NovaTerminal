using Avalonia.Media;

namespace NovaTerminal.Core
{
    public struct TerminalCell
    {
        public char Character;
        public Color Foreground;
        public Color Background;
        public bool IsDirty;

        public static TerminalCell Default => new TerminalCell 
        { 
            Character = ' ', 
            Foreground = Colors.LightGray, 
            Background = Colors.Black,
            IsDirty = true         
        };

        public TerminalCell(char c, Color fg, Color bg)
        {
            Character = c;
            Foreground = fg;
            Background = bg;
            IsDirty = true;
        }
    }
}
