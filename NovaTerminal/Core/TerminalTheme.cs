using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class TerminalTheme
    {
        public string Name { get; set; } = "Default";
        public TermColor Foreground { get; set; } = TermColor.LightGray;
        public TermColor Background { get; set; } = TermColor.Black;
        public TermColor CursorColor { get; set; } = TermColor.White;

        // ANSI 0-15
        public TermColor Black { get; set; } = TermColor.FromRgb(0, 0, 0);
        public TermColor Red { get; set; } = TermColor.FromRgb(205, 0, 0);
        public TermColor Green { get; set; } = TermColor.FromRgb(0, 205, 0);
        public TermColor Yellow { get; set; } = TermColor.FromRgb(205, 205, 0);
        public TermColor Blue { get; set; } = TermColor.FromRgb(0, 0, 238);
        public TermColor Magenta { get; set; } = TermColor.FromRgb(205, 0, 205);
        public TermColor Cyan { get; set; } = TermColor.FromRgb(0, 205, 205);
        public TermColor White { get; set; } = TermColor.FromRgb(229, 229, 229);

        public TermColor BrightBlack { get; set; } = TermColor.FromRgb(127, 127, 127);
        public TermColor BrightRed { get; set; } = TermColor.FromRgb(255, 0, 0);
        public TermColor BrightGreen { get; set; } = TermColor.FromRgb(0, 255, 0);
        public TermColor BrightYellow { get; set; } = TermColor.FromRgb(255, 255, 0);
        public TermColor BrightBlue { get; set; } = TermColor.FromRgb(92, 92, 255);
        public TermColor BrightMagenta { get; set; } = TermColor.FromRgb(255, 0, 255);
        public TermColor BrightCyan { get; set; } = TermColor.FromRgb(0, 255, 255);
        public TermColor BrightWhite { get; set; } = TermColor.FromRgb(255, 255, 255);


        public TermColor GetAnsiColor(int index, bool bright)
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

        public void SetAnsiColor(int index, bool bright, TermColor color)
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

        public TermColor GetContrastForeground()
        {
            // Formula: 0.299*R + 0.587*G + 0.114*B
            double luminance = (0.299 * Background.R + 0.587 * Background.G + 0.114 * Background.B) / 255.0;
            return luminance > 0.5 ? TermColor.Black : TermColor.White;
        }
    }
}
