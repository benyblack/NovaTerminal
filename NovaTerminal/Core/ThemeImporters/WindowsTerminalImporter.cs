using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;

namespace NovaTerminal.Core.ThemeImporters
{
    public class WindowsTerminalImporter : IThemeImporter
    {
        public string Name => "Windows Terminal";
        public string Extension => ".json";

        public IEnumerable<TerminalTheme> Import(string filePath)
        {
            var themes = new List<TerminalTheme>();
            try
            {
                string json = File.ReadAllText(filePath);
                var root = JsonNode.Parse(json);
                if (root == null) return themes;

                // WT can be a whole settings file with "schemes" array, or just an array of schemes
                var schemes = root["schemes"]?.AsArray() ?? root.AsArray();
                if (schemes == null) return themes;

                foreach (var scheme in schemes)
                {
                    if (scheme == null) continue;
                    var theme = MapToTerminalTheme(scheme);
                    if (theme != null) themes.Add(theme);
                }
            }
            catch { }
            return themes;
        }

        private TerminalTheme? MapToTerminalTheme(JsonNode node)
        {
            try
            {
                var theme = new TerminalTheme
                {
                    Name = node["name"]?.ToString() ?? "Imported WT Theme",
                    Foreground = ParseColor(node["foreground"]?.ToString(), TermColor.LightGray),
                    Background = ParseColor(node["background"]?.ToString(), TermColor.Black),
                    CursorColor = ParseColor(node["cursorColor"]?.ToString(), TermColor.White),

                    Black = ParseColor(node["black"]?.ToString(), TermColor.Black),
                    Red = ParseColor(node["red"]?.ToString(), TermColor.Red),
                    Green = ParseColor(node["green"]?.ToString(), TermColor.Green),
                    Yellow = ParseColor(node["yellow"]?.ToString(), TermColor.Yellow),
                    Blue = ParseColor(node["blue"]?.ToString(), TermColor.Blue),
                    Magenta = ParseColor(node["purple"]?.ToString(), TermColor.Magenta),
                    Cyan = ParseColor(node["cyan"]?.ToString(), TermColor.Cyan),
                    White = ParseColor(node["white"]?.ToString(), TermColor.White),

                    BrightBlack = ParseColor(node["brightBlack"]?.ToString(), TermColor.DarkGray),
                    BrightRed = ParseColor(node["brightRed"]?.ToString(), TermColor.Red),
                    BrightGreen = ParseColor(node["brightGreen"]?.ToString(), TermColor.Green),
                    BrightYellow = ParseColor(node["brightYellow"]?.ToString(), TermColor.Yellow),
                    BrightBlue = ParseColor(node["brightBlue"]?.ToString(), TermColor.Blue),
                    BrightMagenta = ParseColor(node["brightPurple"]?.ToString(), TermColor.Magenta),
                    BrightCyan = ParseColor(node["brightCyan"]?.ToString(), TermColor.Cyan),
                    BrightWhite = ParseColor(node["brightWhite"]?.ToString(), TermColor.White)
                };
                return theme;
            }
            catch { return null; }
        }

        private TermColor ParseColor(string? hex, TermColor fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            try
            {
                var avaloniaColor = Color.Parse(hex);
                return TermColor.FromAvaloniaColor(avaloniaColor);
            }
            catch { return fallback; }
        }
    }
}
