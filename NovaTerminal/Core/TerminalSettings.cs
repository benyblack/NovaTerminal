using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaTerminal.Core
{
    public class TerminalSettings
    {
        private static string SettingsPath => AppPaths.SettingsFilePath;

        public double FontSize { get; set; } = 14;
        public int MaxHistory { get; set; } = 10000;
        public string FontFamily { get; set; } = "Consolas";
        public string ThemeName { get; set; } = "Default";
        public double WindowOpacity { get; set; } = 1.0;
        public string BlurEffect { get; set; } = "Acrylic";
        public bool EnableLigatures { get; set; } = false;
        public bool EnableComplexShaping { get; set; } = true;
        public string CursorStyle { get; set; } = "Underline";
        public bool CursorBlink { get; set; } = true;
        public bool BellAudioEnabled { get; set; } = true;
        public bool BellVisualEnabled { get; set; } = true;
        public bool SmoothScrolling { get; set; } = true;
        public string PaneClosePolicy { get; set; } = "Confirm";
        public System.Collections.Generic.Dictionary<string, string> Keybindings { get; set; } = new();

        // Background Image Settings
        public string BackgroundImagePath { get; set; } = "";
        public double BackgroundImageOpacity { get; set; } = 0.5;
        public string BackgroundImageStretch { get; set; } = "UniformToFill"; // Options: "None", "Acrylic", "Mica"

        public bool QuakeModeEnabled { get; set; } = true;
        public string GlobalHotkey { get; set; } = "Alt+OemTilde";

        public System.Collections.Generic.List<TerminalProfile> Profiles { get; set; } = new();
        public Guid DefaultProfileId { get; set; }

        private TerminalTheme? _activeTheme;
        private ThemeManager? _themeManager;

        [JsonIgnore]
        public ThemeManager ThemeManager => _themeManager ??= new ThemeManager();

        [JsonIgnore]
        public TerminalTheme ActiveTheme
        {
            get
            {
                if (_activeTheme == null || _activeTheme.Name != ThemeName)
                {
                    ThemeManager.LoadThemes();
                    _activeTheme = ThemeManager.GetTheme(ThemeName);
                }
                return _activeTheme;
            }
            set => _activeTheme = value;
        }

        public void RefreshActiveTheme()
        {
            _activeTheme = null;
        }

        public static System.Collections.Generic.List<TerminalProfile> GetDefaultProfiles()
        {
            if (OperatingSystem.IsWindows())
            {
                return new System.Collections.Generic.List<TerminalProfile>
                {
                    new TerminalProfile { Name = "Command Prompt", Command = "cmd.exe" },
                    new TerminalProfile { Name = "PowerShell", Command = "pwsh.exe" },
                    new TerminalProfile { Name = "Windows PowerShell", Command = "powershell.exe" }
                };
            }
            else
            {
                return new System.Collections.Generic.List<TerminalProfile>
                {
                    new TerminalProfile { Name = "Bash", Command = "/bin/bash" },
                    new TerminalProfile { Name = "Zsh", Command = "/bin/zsh" },
                    new TerminalProfile { Name = "Shell", Command = "/bin/sh" }
                };
            }
        }

        public TerminalSettings()
        {
            Profiles = GetDefaultProfiles();
            DefaultProfileId = Profiles[0].Id;
        }

        public static TerminalSettings Load()
        {
            AppPaths.EnsureInitialized();
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
            else
            {
                // Cross-platform polish: If we don't have any profile that matches a known shell for this OS,
                // add the defaults for this OS so the user isn't stuck with invalid shells from another OS.
                bool nativeShellsFound = settings.Profiles.Exists(p =>
                    p.Type == ConnectionType.Local &&
                    (File.Exists(p.Command) || ShellHelper.InPath(p.Command)));

                if (!nativeShellsFound)
                {
                    foreach (var def in GetDefaultProfiles())
                    {
                        if (!settings.Profiles.Exists(p => p.Command == def.Command))
                        {
                            settings.Profiles.Add(def);
                        }
                    }
                }
            }

            // Ensure we have a valid default profile
            bool defaultValid = settings.Profiles.Exists(p => p.Id == settings.DefaultProfileId);
            if (settings.DefaultProfileId == Guid.Empty || !defaultValid)
            {
                settings.DefaultProfileId = settings.Profiles[0].Id;
            }
            else
            {
                // If the default profile is a local command that doesn't exist on this OS, 
                // try to pick a more appropriate default for the current platform.
                var currentDefault = settings.Profiles.Find(p => p.Id == settings.DefaultProfileId);
                if (currentDefault != null && currentDefault.Type == ConnectionType.Local)
                {
                    bool exists = File.Exists(currentDefault.Command) || ShellHelper.InPath(currentDefault.Command);
                    if (!exists)
                    {
                        var better = settings.Profiles.Find(p =>
                            p.Type == ConnectionType.Local &&
                            (File.Exists(p.Command) || ShellHelper.InPath(p.Command)));

                        if (better != null)
                        {
                            settings.DefaultProfileId = better.Id;
                        }
                    }
                }
            }

            return settings;
        }

        public void Save()
        {
            try
            {
                AppPaths.EnsureInitialized();
                string json = JsonSerializer.Serialize(this, AppJsonContext.Default.TerminalSettings);
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}
