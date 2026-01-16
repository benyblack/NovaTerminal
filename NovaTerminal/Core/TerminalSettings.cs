using System;
using System.IO;
using Newtonsoft.Json;

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
        public string BlurEffect { get; set; } = "Acrylic"; // Options: "None", "Acrylic", "Mica"

        public static TerminalSettings Load()
        {
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonConvert.DeserializeObject<TerminalSettings>(json) ?? new TerminalSettings();
                }
                catch { }
            }
            return new TerminalSettings();
        }

        public void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}
