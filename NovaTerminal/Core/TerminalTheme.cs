using Avalonia.Media;
using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class TerminalTheme
    {
        public string Name { get; set; } = "Default";
        public Color Foreground { get; set; } = Colors.LightGray;
        public Color Background { get; set; } = Colors.Black;
        public Color CursorColor { get; set; } = Colors.White;

        // ANSI 0-15
        public Color Black { get; set; } = Color.FromRgb(0, 0, 0);
        public Color Red { get; set; } = Color.FromRgb(205, 0, 0);
        public Color Green { get; set; } = Color.FromRgb(0, 205, 0);
        public Color Yellow { get; set; } = Color.FromRgb(205, 205, 0);
        public Color Blue { get; set; } = Color.FromRgb(0, 0, 238);
        public Color Magenta { get; set; } = Color.FromRgb(205, 0, 205);
        public Color Cyan { get; set; } = Color.FromRgb(0, 205, 205);
        public Color White { get; set; } = Color.FromRgb(229, 229, 229);

        public Color BrightBlack { get; set; } = Color.FromRgb(127, 127, 127);
        public Color BrightRed { get; set; } = Color.FromRgb(255, 0, 0);
        public Color BrightGreen { get; set; } = Color.FromRgb(0, 255, 0);
        public Color BrightYellow { get; set; } = Color.FromRgb(255, 255, 0);
        public Color BrightBlue { get; set; } = Color.FromRgb(92, 92, 255);
        public Color BrightMagenta { get; set; } = Color.FromRgb(255, 0, 255);
        public Color BrightCyan { get; set; } = Color.FromRgb(0, 255, 255);
        public Color BrightWhite { get; set; } = Color.FromRgb(255, 255, 255);

        public static TerminalTheme Dark => new TerminalTheme();

        public static TerminalTheme SolarizedDark => new TerminalTheme
        {
            Name = "Solarized Dark",
            Foreground = Color.Parse("#839496"),
            Background = Color.Parse("#002b36"),
            Black = Color.Parse("#073642"),
            Red = Color.Parse("#dc322f"),
            Green = Color.Parse("#859900"),
            Yellow = Color.Parse("#b58900"),
            Blue = Color.Parse("#268bd2"),
            Magenta = Color.Parse("#d33682"),
            Cyan = Color.Parse("#2aa198"),
            White = Color.Parse("#eee8d5"),
            BrightBlack = Color.Parse("#002b36"),
            BrightRed = Color.Parse("#cb4b16"),
            BrightGreen = Color.Parse("#586e75"),
            BrightYellow = Color.Parse("#657b83"),
            BrightBlue = Color.Parse("#839496"),
            BrightMagenta = Color.Parse("#6c71c4"),
            BrightCyan = Color.Parse("#93a1a1"),
            BrightWhite = Color.Parse("#fdf6e3")
        };

        public Color GetAnsiColor(int index, bool bright)
        {
            if (bright)
            {
                return index switch
                {
                    0 => BrightBlack, 1 => BrightRed, 2 => BrightGreen, 3 => BrightYellow,
                    4 => BrightBlue, 5 => BrightMagenta, 6 => BrightCyan, 7 => BrightWhite,
                    _ => BrightWhite
                };
            }
            return index switch
            {
                0 => Black, 1 => Red, 2 => Green, 3 => Yellow,
                4 => Blue, 5 => Magenta, 6 => Cyan, 7 => White,
                _ => White
            };
        }
    }
}
