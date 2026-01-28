using System;
using System.IO;
using System.Text.Json;

namespace NovaTerminal.Core
{
    public class TerminalSettings
    {
        private const string SettingsFile = "settings.json";
        private static string SettingsPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFile);

        public double FontSize { get; set; } = 14;
        public int MaxHistory { get; set; } = 10000;
        public string FontFamily { get; set; } = "Consolas";
        public string ThemeName { get; set; } = "Default";
        public double WindowOpacity { get; set; } = 1.0;
        public string BlurEffect { get; set; } = "Acrylic";

        // Background Image Settings
        public string BackgroundImagePath { get; set; } = "";
        public double BackgroundImageOpacity { get; set; } = 0.5;
        public string BackgroundImageStretch { get; set; } = "UniformToFill"; // Options: "None", "Acrylic", "Mica"

        public static TerminalSettings Load()
        {
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings) ?? new TerminalSettings();
                }
                catch { }
            }
            return new TerminalSettings();
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(this, AppJsonContext.Default.TerminalSettings);
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}
