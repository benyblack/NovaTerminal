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

        public void SetAnsiColor(int index, bool bright, Color color)
        {
            if (bright)
            {
                switch (index)
                {
                    case 0: BrightBlack = color; break;
                    case 1: BrightRed = color; break;
                    case 2: BrightGreen = color; break;
                    case 3: BrightYellow = color; break;
                    case 4: BrightBlue = color; break;
                    case 5: BrightMagenta = color; break;
                    case 6: BrightCyan = color; break;
                    case 7: BrightWhite = color; break;
                }
            }
            else
            {
                switch (index)
                {
                    case 0: Black = color; break;
                    case 1: Red = color; break;
                    case 2: Green = color; break;
                    case 3: Yellow = color; break;
                    case 4: Blue = color; break;
                    case 5: Magenta = color; break;
                    case 6: Cyan = color; break;
                    case 7: White = color; break;
                }
            }
        }

        public TerminalTheme Clone()
        {
            return new TerminalTheme
            {
                Name = this.Name,
                Foreground = this.Foreground,
                Background = this.Background,
                CursorColor = this.CursorColor,
                Black = this.Black,
                Red = this.Red,
                Green = this.Green,
                Yellow = this.Yellow,
                Blue = this.Blue,
                Magenta = this.Magenta,
                Cyan = this.Cyan,
                White = this.White,
                BrightBlack = this.BrightBlack,
                BrightRed = this.BrightRed,
                BrightGreen = this.BrightGreen,
                BrightYellow = this.BrightYellow,
                BrightBlue = this.BrightBlue,
                BrightMagenta = this.BrightMagenta,
                BrightCyan = this.BrightCyan,
                BrightWhite = this.BrightWhite
            };
        }

        public Color GetContrastForeground()
        {
            // Formula: 0.299*R + 0.587*G + 0.114*B
            double luminance = (0.299 * Background.R + 0.587 * Background.G + 0.114 * Background.B) / 255.0;
            return luminance > 0.5 ? Colors.Black : Colors.White;
        }
    }
}
