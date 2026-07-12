using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaTerminal.VT;
using NovaTerminal.Pty;

namespace NovaTerminal.Shell
{
    public class TerminalSettings
    {
        private static string SettingsPath => AppPaths.SettingsFilePath;

        public double FontSize { get; set; } = 14;
        public int MaxHistory { get; set; } = 10000;
        public string FontFamily { get; set; } = BundledFontCatalog.DefaultTerminalFontFamily;
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
        public bool EnableLinkDetection { get; set; } = true;
        // Scroll units forwarded per wheel notch. For own-buffer scrolling this is lines
        // per notch; for a mouse-reporting TUI it is wheel clicks per notch. Higher =
        // smoother/finer (more steps per notch), lower = coarser. Matters most for
        // high-resolution touchpads, which emit many sub-notch wheel events.
        public double WheelLinesPerNotch { get; set; } = 3.0;
        public string PaneClosePolicy { get; set; } = "Confirm";
        public System.Collections.Generic.Dictionary<string, string> Keybindings { get; set; } = new();
        public System.Collections.Generic.List<TabTemplateRule> TabTemplateRules { get; set; } = new();

        // Background Image Settings
        public string BackgroundImagePath { get; set; } = "";
        public double BackgroundImageOpacity { get; set; } = 0.5;
        public string BackgroundImageStretch { get; set; } = "UniformToFill"; // Options: "None", "Acrylic", "Mica"

        public bool QuakeModeEnabled { get; set; } = true;
        public string GlobalHotkey { get; set; } = "Alt+OemTilde";
        // Disabled by default as of 0.3: the Command Assist feature isn't production-ready
        // yet. This master flag gates the whole feature; users can opt in via Settings.
        public bool CommandAssistEnabled { get; set; } = false;
        public bool CommandAssistHistoryEnabled { get; set; } = true;
        public int CommandAssistMaxHistoryEntries { get; set; } = 5000;
        public bool CommandAssistAutoHideInAltScreen { get; set; } = true;
        public bool CommandAssistShellIntegrationEnabled { get; set; } = true;
        public bool CommandAssistPowerShellIntegrationEnabled { get; set; } = true;
        public bool ExperimentalNativeSshEnabled { get; set; } = false;
        // Agent-host observe surface (docs/agent-host/DIRECTION.md, milestone A1).
        // Off by default: when false, no local IPC endpoint exists at all and AI
        // agents cannot read any terminal session. Observe-only in v1 — there is
        // no acting capability behind this flag.
        public bool AgentAccessObserveEnabled { get; set; } = false;
        // A4 sub-gate on top of the observe toggle: allows agents to export a
        // session's recent output as a replay file (novaterminal.export_replay).
        // Exports contain output and resize events only — never typed input
        // (privacy decision in docs/plans/2026-07-07-agent-host-a4-replay-design.md).
        // Off by default; both toggles must be on for an export to succeed.
        public bool AgentReplayExportEnabled { get; set; } = false;
        // A3 act surface: separate default-off opt-in letting agents type into,
        // spawn, and close sessions (novaterminal.send_input / spawn_session /
        // close_session). On top of observe; SSH sessions additionally require
        // per-profile allowlisting. Every acting call is shown in the agent
        // activity journal. Off by default.
        public bool AgentAccessActEnabled { get; set; } = false;
        // In-app toast when a command that ran ≥30s finishes in an unfocused
        // pane (A2 PR4, absorbs ROADMAP §5.2). Off by default.
        public bool LongCommandNotificationsEnabled { get; set; } = false;

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
            return LoadFromPath(SettingsPath);
        }

        internal static TerminalSettings LoadFromPath(string settingsPath)
        {
            TerminalSettings settings;
            if (File.Exists(settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings) ?? new TerminalSettings();
                }
                catch (Exception ex)
                {
                    // Corrupt settings must not be silently replaced with defaults (#167):
                    // quarantine the evidence, then fall back to the .bak written by
                    // AtomicFile before resorting to defaults.
                    System.Diagnostics.Debug.WriteLine($"[Settings] '{settingsPath}' is unreadable ({ex.Message}); trying backup.");
                    try { File.Copy(settingsPath, settingsPath + ".corrupt", overwrite: true); }
                    catch { /* best effort */ }

                    var fromBackup = TryLoadOrNull(settingsPath + ".bak");
                    if (fromBackup != null)
                    {
                        settings = fromBackup;
                        // Repair the primary immediately so subsequent launches don't
                        // repeatedly quarantine + fall back (review feedback on #178).
                        try
                        {
                            AtomicFile.WriteAllText(settingsPath,
                                JsonSerializer.Serialize(fromBackup, AppJsonContext.Default.TerminalSettings));
                        }
                        catch (Exception repairEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Settings] Failed to repair '{settingsPath}' from backup: {repairEx.Message}");
                        }
                    }
                    else
                    {
                        // The reset to defaults must leave a diagnosable trace.
                        System.Diagnostics.Debug.WriteLine($"[Settings] Backup '{settingsPath}.bak' is also unreadable; falling back to defaults.");
                        settings = new TerminalSettings();
                    }
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

            if (settings.TabTemplateRules == null)
            {
                settings.TabTemplateRules = new System.Collections.Generic.List<TabTemplateRule>();
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

        private static TerminalSettings? TryLoadOrNull(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings);
            }
            catch
            {
                return null;
            }
        }

        public void Save()
        {
            try
            {
                AppPaths.EnsureInitialized();
                string json = JsonSerializer.Serialize(this, AppJsonContext.Default.TerminalSettings);
                // Atomic write with .bak (#167): a crash mid-write previously corrupted
                // settings.json, and the next start silently reset all configuration.
                AtomicFile.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Save failed: {ex.Message}");
            }
        }
    }
}
