using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;

namespace NovaTerminal.Core.ThemeImporters
{
    public class AlacrittyImporter : IThemeImporter
    {
        public string Name => "Alacritty";
        public string Extension => ".toml";

        public IEnumerable<TerminalTheme> Import(string filePath)
        {
            var themes = new List<TerminalTheme>();
            try
            {
                var theme = new TerminalTheme
                {
                    Name = Path.GetFileNameWithoutExtension(filePath) + " (Alacritty)"
                };

                string section = string.Empty;
                foreach (var raw in File.ReadLines(filePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                    int commentIndex = line.IndexOf('#');
                    if (commentIndex > 0)
                    {
                        line = line.Substring(0, commentIndex).Trim();
                    }

                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    {
                        section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim().Trim('"', '\'');
                    if (!TryParseColor(val, out var color)) continue;

                    switch ($"{section}.{key}")
                    {
                        case "colors.primary.foreground": theme.Foreground = color; break;
                        case "colors.primary.background": theme.Background = color; break;
                        case "colors.cursor.cursor": theme.CursorColor = color; break;

                        case "colors.normal.black": theme.Black = color; break;
                        case "colors.normal.red": theme.Red = color; break;
                        case "colors.normal.green": theme.Green = color; break;
                        case "colors.normal.yellow": theme.Yellow = color; break;
                        case "colors.normal.blue": theme.Blue = color; break;
                        case "colors.normal.magenta": theme.Magenta = color; break;
                        case "colors.normal.cyan": theme.Cyan = color; break;
                        case "colors.normal.white": theme.White = color; break;

                        case "colors.bright.black": theme.BrightBlack = color; break;
                        case "colors.bright.red": theme.BrightRed = color; break;
                        case "colors.bright.green": theme.BrightGreen = color; break;
                        case "colors.bright.yellow": theme.BrightYellow = color; break;
                        case "colors.bright.blue": theme.BrightBlue = color; break;
                        case "colors.bright.magenta": theme.BrightMagenta = color; break;
                        case "colors.bright.cyan": theme.BrightCyan = color; break;
                        case "colors.bright.white": theme.BrightWhite = color; break;
                    }
                }

                themes.Add(theme);
            }
            catch
            {
                // Import errors are non-fatal and handled by caller.
            }

            return themes;
        }

        private static bool TryParseColor(string value, out TermColor color)
        {
            color = TermColor.Black;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string normalized = value.Trim();
            if (!normalized.StartsWith("#", StringComparison.Ordinal) &&
                normalized.Length == 6)
            {
                normalized = "#" + normalized;
            }

            try
            {
                var parsed = Color.Parse(normalized);
                color = TermColorHelper.FromAvaloniaColor(parsed);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
