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

        public static TerminalTheme Dracula => new TerminalTheme
        {
            Name = "Dracula",
            Foreground = Color.Parse("#f8f8f2"),
            Background = Color.Parse("#282a36"),
            CursorColor = Color.Parse("#f8f8f2"),
            Black = Color.Parse("#21222c"),
            Red = Color.Parse("#ff5555"),
            Green = Color.Parse("#50fa7b"),
            Yellow = Color.Parse("#f1fa8c"),
            Blue = Color.Parse("#bd93f9"),
            Magenta = Color.Parse("#ff79c6"),
            Cyan = Color.Parse("#8be9fd"),
            White = Color.Parse("#f8f8f2"),
            BrightBlack = Color.Parse("#6272a4"),
            BrightRed = Color.Parse("#ff6e6e"),
            BrightGreen = Color.Parse("#69ff94"),
            BrightYellow = Color.Parse("#ffffa5"),
            BrightBlue = Color.Parse("#d6acff"),
            BrightMagenta = Color.Parse("#ff92df"),
            BrightCyan = Color.Parse("#a4ffff"),
            BrightWhite = Color.Parse("#ffffff")
        };

        public static TerminalTheme Monokai => new TerminalTheme
        {
            Name = "Monokai",
            Foreground = Color.Parse("#f8f8f2"),
            Background = Color.Parse("#272822"),
            CursorColor = Color.Parse("#f8f8f2"),
            Black = Color.Parse("#272822"),
            Red = Color.Parse("#f92672"),
            Green = Color.Parse("#a6e22e"),
            Yellow = Color.Parse("#f4bf75"),
            Blue = Color.Parse("#66d9ef"),
            Magenta = Color.Parse("#ae81ff"),
            Cyan = Color.Parse("#a1efe4"),
            White = Color.Parse("#f8f8f2"),
            BrightBlack = Color.Parse("#75715e"),
            BrightRed = Color.Parse("#f92672"),
            BrightGreen = Color.Parse("#a6e22e"),
            BrightYellow = Color.Parse("#f4bf75"),
            BrightBlue = Color.Parse("#66d9ef"),
            BrightMagenta = Color.Parse("#ae81ff"),
            BrightCyan = Color.Parse("#a1efe4"),
            BrightWhite = Color.Parse("#f9f8f5")
        };

        public static TerminalTheme OneHalfDark => new TerminalTheme
        {
            Name = "One Half Dark",
            Foreground = Color.Parse("#dcdfe4"),
            Background = Color.Parse("#282c34"),
            CursorColor = Color.Parse("#dcdfe4"),
            Black = Color.Parse("#282c34"),
            Red = Color.Parse("#e06c75"),
            Green = Color.Parse("#98c379"),
            Yellow = Color.Parse("#e5c07b"),
            Blue = Color.Parse("#61afef"),
            Magenta = Color.Parse("#c678dd"),
            Cyan = Color.Parse("#56b6c2"),
            White = Color.Parse("#dcdfe4"),
            BrightBlack = Color.Parse("#5c6370"),
            BrightRed = Color.Parse("#e06c75"),
            BrightGreen = Color.Parse("#98c379"),
            BrightYellow = Color.Parse("#e5c07b"),
            BrightBlue = Color.Parse("#61afef"),
            BrightMagenta = Color.Parse("#c678dd"),
            BrightCyan = Color.Parse("#56b6c2"),
            BrightWhite = Color.Parse("#dcdfe4")
        };

        public static TerminalTheme GitHubDark => new TerminalTheme
        {
            Name = "GitHub Dark",
            Foreground = Color.Parse("#c9d1d9"),
            Background = Color.Parse("#0d1117"),
            CursorColor = Color.Parse("#c9d1d9"),
            Black = Color.Parse("#484f58"),
            Red = Color.Parse("#ff7b72"),
            Green = Color.Parse("#3fb950"),
            Yellow = Color.Parse("#d29922"),
            Blue = Color.Parse("#58a6ff"),
            Magenta = Color.Parse("#bc8cff"),
            Cyan = Color.Parse("#39c5cf"),
            White = Color.Parse("#b1bac4"),
            BrightBlack = Color.Parse("#6e7681"),
            BrightRed = Color.Parse("#ffa198"),
            BrightGreen = Color.Parse("#56d364"),
            BrightYellow = Color.Parse("#e3b341"),
            BrightBlue = Color.Parse("#79c0ff"),
            BrightMagenta = Color.Parse("#d2a8ff"),
            BrightCyan = Color.Parse("#56d4dd"),
            BrightWhite = Color.Parse("#f0f6fc")
        };

        public Color GetAnsiColor(int index, bool bright)
        {
            if (bright)
            {
                return index switch
                {
                    0 => BrightBlack,
                    1 => BrightRed,
                    2 => BrightGreen,
                    3 => BrightYellow,
                    4 => BrightBlue,
                    5 => BrightMagenta,
                    6 => BrightCyan,
                    7 => BrightWhite,
                    _ => BrightWhite
                };
            }
            return index switch
            {
                0 => Black,
                1 => Red,
                2 => Green,
                3 => Yellow,
                4 => Blue,
                5 => Magenta,
                6 => Cyan,
                7 => White,
                _ => White
            };
        }
    }
}
