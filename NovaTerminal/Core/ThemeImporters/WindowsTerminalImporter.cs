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
                    Foreground = ParseColor(node["foreground"]?.ToString(), Colors.LightGray),
                    Background = ParseColor(node["background"]?.ToString(), Colors.Black),
                    CursorColor = ParseColor(node["cursorColor"]?.ToString(), Colors.White),

                    Black = ParseColor(node["black"]?.ToString(), Colors.Black),
                    Red = ParseColor(node["red"]?.ToString(), Colors.Red),
                    Green = ParseColor(node["green"]?.ToString(), Colors.Green),
                    Yellow = ParseColor(node["yellow"]?.ToString(), Colors.Yellow),
                    Blue = ParseColor(node["blue"]?.ToString(), Colors.Blue),
                    Magenta = ParseColor(node["purple"]?.ToString(), Colors.Magenta),
                    Cyan = ParseColor(node["cyan"]?.ToString(), Colors.Cyan),
                    White = ParseColor(node["white"]?.ToString(), Colors.White),

                    BrightBlack = ParseColor(node["brightBlack"]?.ToString(), Colors.Gray),
                    BrightRed = ParseColor(node["brightRed"]?.ToString(), Colors.Red),
                    BrightGreen = ParseColor(node["brightGreen"]?.ToString(), Colors.Green),
                    BrightYellow = ParseColor(node["brightYellow"]?.ToString(), Colors.Yellow),
                    BrightBlue = ParseColor(node["brightBlue"]?.ToString(), Colors.Blue),
                    BrightMagenta = ParseColor(node["brightPurple"]?.ToString(), Colors.Magenta),
                    BrightCyan = ParseColor(node["brightCyan"]?.ToString(), Colors.Cyan),
                    BrightWhite = ParseColor(node["brightWhite"]?.ToString(), Colors.White)
                };
                return theme;
            }
            catch { return null; }
        }

        private Color ParseColor(string? hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            try { return Color.Parse(hex); } catch { return fallback; }
        }
    }
}
