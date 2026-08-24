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
        // Kill switch for the kitty keyboard protocol's disambiguate-escape-codes tier
        // (issue #266 / PR #277 review, Blocker 2). Default on. When false: TerminalView
        // never calls TryEncodeKittyKey (so every key falls through to the legacy encoding
        // path unconditionally, even if a TUI pushed flag 1), and AnsiParser answers the
        // CSI ? u query with flags 0 regardless of the actual pushed stack state, so a TUI
        // that queries capabilities does not believe the protocol is active. This is the
        // user's escape hatch if a keyboard layout hits the AltGr carve-out's accepted
        // trade-off or any other unforeseen encoding issue - flip this off instead of having
        // to fight the TUI that turned the protocol on.
        public bool EnableKittyKeyboardProtocol { get; set; } = true;
        // Settings gate for OSC 52 clipboard-write support (issue #268). Default on for
        // local sessions: AnsiParser.OnClipboardWrite always fires (VT stays policy-free),
        // and this flag is what TerminalPane checks before actually touching the system
        // clipboard. Read (answering an OSC 52 query with real clipboard contents) is never
        // implemented regardless of this setting - it only ever gates the write path.
        // Single global setting for now, no per-profile override: an SSH session forwarding
        // OSC 52 from a remote TUI is indistinguishable from a local one at this layer, so a
        // profile-scoped opt-in (e.g. disabling for untrusted SSH hosts specifically) is
        // future work rather than a v1 requirement.
        public bool AllowOsc52ClipboardWrite { get; set; } = true;
        // Scroll units forwarded per wheel notch. For own-buffer scrolling this is lines
        // per notch; for a mouse-reporting TUI it is wheel clicks per notch. Higher =
        // smoother/finer (more steps per notch), lower = coarser. Matters most for
        // high-resolution touchpads, which emit many sub-notch wheel events.
        public double WheelLinesPerNotch { get; set; } = 3.0;
        public string PaneClosePolicy { get; set; } = "Confirm";
        // What happens to a pane when its shell exits (#311). Three values: "Graceful" (the default)
        // closes it on a clean exit (code 0) and keeps it with the exit banner otherwise; "Never" always
        // keeps the pane and shows the banner; "Always" closes it whatever the code. The default shipped
        // as "Never" while a local PTY still reported 0 for every exit — a policy cannot tell a clean
        // exit from a crash when every exit looks clean. #313 (Windows) and #323 (Unix) made the code the
        // child's real status, so "Graceful" now means what it says. SSH panes ignore this and always keep
        // their reconnect banner. Unrecognised values behave as "Never" — a typo must not be more
        // destructive than the default.
        public string ShellExitPolicy { get; set; } = "Graceful";
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

        // Gates command capture and history-sourced suggestions only. It is not a second master
        // switch: with this off the bubble, popup, path suggestions, Help and Fix all still work
        // (V2 Phase 3b task 3 - it used to take the whole feature down).
        public bool CommandAssistHistoryEnabled { get; set; } = true;
        public int CommandAssistMaxHistoryEntries { get; set; } = 5000;

        // The passive typing bubble (V2 Phase 3b task 1): after two typed characters the bubble shows
        // the top-ranked history/path suggestion without being asked. On by default when the master
        // flag is on, because a feature nobody can see is the problem this phase exists to fix; off
        // restores the M4.3 quiet behavior, where a passive bubble only ever offers path completions.
        //
        // CommandAssistAutoHideInAltScreen used to sit here. It was never read: hiding the overlay
        // when a full-screen TUI takes the alternate screen is unconditional, and a setting that
        // could switch it off would only ever let the assist paint over vim. Deleted in Phase 3b
        // rather than wired up; unknown keys in settings.json are ignored on load, so an existing
        // file that still carries it loads fine.
        public bool CommandAssistPassiveBubbleEnabled { get; set; } = true;
        public bool CommandAssistShellIntegrationEnabled { get; set; } = true;
        public bool CommandAssistPowerShellIntegrationEnabled { get; set; } = true;
        // On by default since the native backend reached parity (agent auth, jump chains, all
        // three forward kinds, SFTP) and every gap warns instead of degrading silently. Users
        // whose settings.json already stores an explicit false keep it — this default only
        // reaches fresh installs and settings files predating the field.
        public bool ExperimentalNativeSshEnabled { get; set; } = true;
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
        // A5 sub-gate on top of the observe toggle: allows agents to render a
        // session to a PNG (novaterminal.capture_screen). Its own decision rather
        // than riding the observe toggle because an image discloses more than the
        // text readScreen returns — inline images, the theme, everything drawn on
        // the grid. Off by default; both toggles must be on for a capture to
        // succeed, and every capture is shown in the agent activity journal.
        public bool AgentScreenshotEnabled { get; set; } = false;
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
