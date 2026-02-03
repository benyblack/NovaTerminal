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

        public System.Collections.Generic.List<TerminalProfile> Profiles { get; set; } = new();
        public Guid DefaultProfileId { get; set; }

        public static System.Collections.Generic.List<TerminalProfile> GetDefaultProfiles()
        {
            return new System.Collections.Generic.List<TerminalProfile>
            {
                new TerminalProfile { Name = "Command Prompt", Command = "cmd.exe" },
                new TerminalProfile { Name = "PowerShell", Command = "powershell.exe" },
                new TerminalProfile { Name = "WSL (Ubuntu)", Command = "wsl.exe" }
            };
        }

        public TerminalSettings()
        {
            Profiles = GetDefaultProfiles();
            DefaultProfileId = Profiles[0].Id;
        }

        public static TerminalSettings Load()
        {
            TerminalSettings settings;
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);
                    settings = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings) ?? new TerminalSettings();
                }
                catch
                {
                    settings = new TerminalSettings();
                }
            }
            else
            {
                settings = new TerminalSettings();
            }

            // Post-load validation
            if (settings.Profiles == null || settings.Profiles.Count == 0)
            {
                settings.Profiles = GetDefaultProfiles();
            }

            if (settings.DefaultProfileId == Guid.Empty || !settings.Profiles.Exists(p => p.Id == settings.DefaultProfileId))
            {
                settings.DefaultProfileId = settings.Profiles[0].Id;
            }

            return settings;
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
