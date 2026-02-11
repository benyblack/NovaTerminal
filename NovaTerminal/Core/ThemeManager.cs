using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NovaTerminal.Core
{
    public class ThemeManager
    {
        private readonly string _themesDirectory;
        private readonly Dictionary<string, TerminalTheme> _loadedThemes = new();
        private readonly List<ThemeImporters.IThemeImporter> _importers = new()
        {
            new ThemeImporters.WindowsTerminalImporter(),
            new ThemeImporters.ITerm2Importer()
        };

        public ThemeManager(string? themesDirectory = null)
        {
            _themesDirectory = themesDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "themes");
            if (!Directory.Exists(_themesDirectory))
            {
                Directory.CreateDirectory(_themesDirectory);
            }
        }

        private bool _isLoaded = false;

        public void ReloadThemes()
        {
            _isLoaded = false;
            LoadThemes();
        }

        public void LoadThemes()
        {
            if (_isLoaded) return;
            _loadedThemes.Clear();

            // Ensure Default theme is always available
            var defaultTheme = new TerminalTheme { Name = "Default" };
            _loadedThemes[defaultTheme.Name] = defaultTheme;

            // First load built-in defaults if directory is empty (extraction step)
            ExtractBuiltInThemes();

            var files = Directory.GetFiles(_themesDirectory, "*.json");
            foreach (var file in files)
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme);
                    if (theme != null)
                    {
                        _loadedThemes[theme.Name] = theme;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ThemeManager] Error loading theme from {file}: {ex.Message}");
                }
            }
            _isLoaded = true;
        }

        public string ImportTheme(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLower();
            var importer = _importers.FirstOrDefault(i => i.Extension.ToLower() == ext);

            if (importer == null && ext == ".json")
            {
                // Simple JSON theme? Try to just copy it
                try
                {
                    string json = File.ReadAllText(filePath);
                    var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme);
                    if (theme != null)
                    {
                        string targetPath = Path.Combine(_themesDirectory, Path.GetFileName(filePath));
                        File.WriteAllText(targetPath, json);
                        _loadedThemes[theme.Name] = theme;
                        return theme.Name;
                    }
                }
                catch { }
            }

            if (importer != null)
            {
                var imported = importer.Import(filePath);
                string lastThemeName = "";
                foreach (var theme in imported)
                {
                    string fileName = theme.Name.Replace(" ", "") + ".json";
                    string targetPath = Path.Combine(_themesDirectory, fileName);
                    string json = JsonSerializer.Serialize(theme, AppJsonContext.Default.TerminalTheme);
                    File.WriteAllText(targetPath, json);
                    _loadedThemes[theme.Name] = theme;
                    lastThemeName = theme.Name;
                }
                return lastThemeName;
            }

            return "";
        }

        public IEnumerable<string> GetAvailableThemes()
        {
            LoadThemes();
            return _loadedThemes.Keys;
        }

        public TerminalTheme GetTheme(string name)
        {
            LoadThemes(); // Ensure loaded

            if (name == "Default (Dark)") name = "Default";

            if (_loadedThemes.TryGetValue(name, out var theme))
            {
                return theme;
            }
            return _loadedThemes["Default"];
        }

        public void SaveTheme(TerminalTheme theme)
        {
            string fileName = theme.Name.Replace(" ", "").Replace("/", "_").Replace("\\", "_") + ".json";
            string targetPath = Path.Combine(_themesDirectory, fileName);

            try
            {
                string json = JsonSerializer.Serialize(theme, AppJsonContext.Default.TerminalTheme);
                File.WriteAllText(targetPath, json);

                // Update cache
                _loadedThemes[theme.Name] = theme;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ThemeManager] Error saving theme {theme.Name}: {ex.Message}");
            }
        }

        public void DeleteTheme(string name)
        {
            if (name == "Default") return; // Cannot delete default

            if (_loadedThemes.TryGetValue(name, out var theme))
            {
                string fileName = theme.Name.Replace(" ", "").Replace("/", "_").Replace("\\", "_") + ".json";
                string targetPath = Path.Combine(_themesDirectory, fileName);

                try
                {
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                    _loadedThemes.Remove(name);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ThemeManager] Error deleting theme {name}: {ex.Message}");
                }
            }
        }

        private void ExtractBuiltInThemes()
        {
            // Built-in themes are now provided as JSON files in the themes directory.
        }
    }
}
