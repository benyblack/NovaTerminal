using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using NovaTerminal.Shell;
using NovaTerminal.Platform;
using NovaTerminal.Pty;
using NovaTerminal.VT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ShellIntegration.Remote;
using NovaTerminal.Backup;
using NovaTerminal.Services.Ssh;
using NovaTerminal.Shell.Shortcuts;
using NovaTerminal.Shell.TitleBar;

namespace NovaTerminal
{
    public partial class SettingsWindow : Window
    {
        private TerminalSettings _settings;
        public TerminalSettings Settings => _settings; // Expose for main window to grab without reloading disk

        /// <summary>
        /// F1: set once a successful Import or Restore has replaced configuration on disk out from
        /// under this window (see <see cref="ReloadSettingsAfterExternalChangeAsync"/>). The owning
        /// <c>MainWindow.OpenSettings</c> must adopt the reloaded <see cref="Settings"/> regardless
        /// of how this dialog eventually closes - Save, Cancel, or the window's X - because closing
        /// any other way than Save previously left <c>MainWindow._settings</c> pointing at the
        /// stale PRE-import object. Any later ordinary <c>_settings.Save()</c> from MainWindow (e.g.
        /// the "Font: Increase" palette command) would then silently overwrite the just-imported
        /// configuration on disk.
        /// </summary>
        internal bool ConfigurationReplacedExternally { get; private set; }

        private TerminalProfile? _selectedProfile;
        private System.Collections.Generic.List<TerminalProfile> _profilesList = new();
        private Dictionary<string, string> _shortcutDraftBindings = new(StringComparer.OrdinalIgnoreCase);
        private readonly TitleBarDraftState _titleBarDraft = new();

        // Shared style-class name (see SettingsWindow.axaml's "TextBlock.RowDesc" selector) used
        // across several row-building methods below; a const avoids the literal drifting out of
        // sync with the selector in one of its several call sites.
        private const string RowDescStyleClass = "RowDesc";

        public event Action<double>? OnOpacityChanged;
        public event Action<string>? OnBlurChanged;
        public event Action<string, double, string>? OnBgImageChanged;
        public event Action<string>? OnFontChanged;
        public event Action<double>? OnFontSizeChanged;
        public event Action<string>? OnThemeChanged;

        private DispatcherTimer? _statusTimer;
        private TerminalTheme? _editingTheme;

        /// <summary>
        /// The live Command Assist history store, injected by <c>MainWindow.OpenSettings</c> so the
        /// "Clear history" button acts on the same instance the panes append to.
        /// </summary>
        /// <remarks>
        /// Null for a window opened outside that path (a test, the parameterless constructor XAML
        /// tooling uses), in which case the row says so rather than building a second store over the
        /// same file.
        /// </remarks>
        internal IHistoryStore? CommandAssistHistoryStore { get; set; }

        /// <summary>
        /// Raised after command history has actually been cleared, so the host can refresh anything that
        /// was showing the rows that just went away.
        /// </summary>
        /// <remarks>
        /// An event rather than a direct call because this window cannot see panes, and it fires only on
        /// the success path - a failed clear leaves the surfaces alone, since they are still accurate.
        /// </remarks>
        internal event Action? OnCommandAssistHistoryCleared;

        /// <summary>Whether the next "Clear history" click is the confirming one.</summary>
        private bool _isClearCommandAssistHistoryArmed;

        /// <summary>
        /// The live Command Assist snippet store, injected by <c>MainWindow.OpenSettings</c> for the
        /// same reason <see cref="CommandAssistHistoryStore"/> is (V2 Phase 4b).
        /// </summary>
        /// <remarks>
        /// Null for a window opened outside that path, in which case the snippet section says so
        /// rather than building a second store over the same file.
        /// </remarks>
        internal ISnippetStore? CommandAssistSnippetStore { get; set; }

        /// <summary>
        /// Raised after a snippet was added, edited or deleted, so the host can refresh any assist
        /// surface still showing the old rows.
        /// </summary>
        internal event Action? OnCommandAssistSnippetsChanged;

        /// <summary>The rules behind the snippet rows. Built on first use from the injected store.</summary>
        private SnippetEditor? _snippetEditor;

        /// <summary>The Appearance tab's index in <c>MainTabs</c> - where every <see cref="SettingsSection"/> currently lives.</summary>
        private const int AppearanceTabIndex = 0;

        /// <summary>
        /// The section this window was asked to bring into view once opened (PR #342 Codex round 6),
        /// in addition to whatever tab it selects. Recorded even when it is <see cref="SettingsSection.None"/>
        /// so a test (or a future second caller) can confirm what was actually requested.
        /// </summary>
        private readonly SettingsSection _targetSection;

        public SettingsWindow() : this(0, null) { }

        public SettingsWindow(int initialTab = 0, Guid? initialProfileId = null, SettingsSection section = SettingsSection.None)
        {
            InitializeComponent();
            _settings = TerminalSettings.Load();
            var sshMigration = new SshLegacyProfileMigrationService();
            if (sshMigration.MigrateLegacyProfiles(_settings))
            {
                _settings.Save();
            }
            ApplyTheme();

            _targetSection = section;

            var tabs = this.FindControl<TabControl>("MainTabs");
            // Every SettingsSection currently lives on Appearance, so a section target overrides
            // whatever tab index the caller passed - a caller asking for the TITLE BAR section
            // with the wrong tab index is a bug, not something this window should surface as "the
            // section silently didn't scroll".
            if (tabs != null) tabs.SelectedIndex = section == SettingsSection.None ? initialTab : AppearanceTabIndex;

            if (section == SettingsSection.TitleBar)
            {
                // BringIntoView is a no-op before layout has measured/arranged the target and the
                // ScrollViewer above it - which has not happened yet at construction time (the
                // window is not even shown). DispatcherPriority.Loaded runs after the initial
                // layout/render pass completes, which is the same "wait for real layout" idiom
                // already used elsewhere in this window (see WireCommandAssistSnippetsRow's Opened
                // handler) and in MainWindow.AddTab's LayoutUpdated-based deferral. Hooking this on
                // Opened rather than firing immediately from the constructor also keeps it out of
                // the way of every other constructor caller (tests included) that never shows the
                // window at all.
                Opened += (_, _) => Dispatcher.UIThread.Post(ScrollToTitleBarSection, DispatcherPriority.Loaded);
            }

            // Keep the sidebar list boxes in sync with the tab control. The previous single
            // list box drove selection via a direct SelectedIndex binding; that breaks once the
            // sidebar is split (InterfaceNav holds tabs 0-2, AssistantNav holds tabs 3-4,
            // ConnectionNav holds tab 5, DataNav holds tab 6), so route everything through this
            // small dispatcher instead. The tab header strip is not the navigation — these lists
            // are — so a new tab MUST get a sidebar item and a mapping here, or it is
            // unreachable. That is not hypothetical: the SSH tab initially shipped without one,
            // which silently remapped the "Agent Access" item onto SSH and stranded the real
            // Agent Access tab (Codex review finding on #332). New tabs go at the END of the
            // TabControl so the existing offsets stay true.
            var interfaceNav = this.FindControl<ListBox>("InterfaceNav");
            var assistantNav = this.FindControl<ListBox>("AssistantNav");
            var connectionNav = this.FindControl<ListBox>("ConnectionNav");
            var dataNav = this.FindControl<ListBox>("DataNav");
            if (tabs != null && interfaceNav != null && assistantNav != null && connectionNav != null && dataNav != null)
            {
                tabs.SelectionChanged += (_, _) => SyncSidebarFromTabs(tabs, interfaceNav, assistantNav, connectionNav, dataNav);
                interfaceNav.SelectionChanged += (_, _) =>
                {
                    if (interfaceNav.SelectedIndex < 0) return;
                    tabs.SelectedIndex = interfaceNav.SelectedIndex;
                };
                assistantNav.SelectionChanged += (_, _) =>
                {
                    if (assistantNav.SelectedIndex < 0) return;
                    tabs.SelectedIndex = assistantNav.SelectedIndex + 3;
                };
                connectionNav.SelectionChanged += (_, _) =>
                {
                    if (connectionNav.SelectedIndex < 0) return;
                    tabs.SelectedIndex = connectionNav.SelectedIndex + 5;
                };
                dataNav.SelectionChanged += (_, _) =>
                {
                    if (dataNav.SelectedIndex < 0) return;
                    tabs.SelectedIndex = dataNav.SelectedIndex + 6;
                };
                SyncSidebarFromTabs(tabs, interfaceNav, assistantNav, connectionNav, dataNav);
            }

            // Settings editor is local-profiles only; SSH connections are managed in Connection Manager.
            _profilesList = BuildLocalProfilesForEditor(_settings.Profiles);
            _settings.DefaultProfileId = ResolveDefaultLocalProfileId(_settings.DefaultProfileId, _profilesList);
            _shortcutDraftBindings = new Dictionary<string, string>(_settings.Keybindings, StringComparer.OrdinalIgnoreCase);

            PopulateFonts();
            PopulateThemes();

            var themeList = this.FindControl<ComboBox>("ThemeList");
            var overrideThemeList = this.FindControl<ComboBox>("OverrideThemeList");
            var fontList = this.FindControl<ComboBox>("FontList");
            var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");
            var opacitySlider = this.FindControl<Slider>("WindowOpacitySlider");
            var opacityDisplay = this.FindControl<TextBlock>("OpacityValueDisplay");
            var blurList = this.FindControl<ComboBox>("BlurList");
            var profilesListBox = this.FindControl<ListBox>("ProfilesListBox");
            var btnAddProfile = this.FindControl<Button>("BtnAddProfile");
            var btnDeleteProfile = this.FindControl<Button>("BtnDeleteProfile");
            var btnSetDefault = this.FindControl<Button>("BtnSetDefault");
            var btnSave = this.FindControl<Button>("BtnSave");
            var btnCancel = this.FindControl<Button>("BtnCancel");
            var nameInput = this.FindControl<TextBox>("ProfileNameInput");
            var commandInput = this.FindControl<TextBox>("ProfileCommandInput");
            var argsInput = this.FindControl<TextBox>("ProfileArgsInput");
            var cwdInput = this.FindControl<TextBox>("ProfileCwdInput");
            var groupInput = this.FindControl<TextBox>("ProfileGroupInput");
            var tagsInput = this.FindControl<TextBox>("ProfileTagsInput");
            var typeList = this.FindControl<ComboBox>("ProfileTypeList");
            var sshPanel = this.FindControl<StackPanel>("SshSettingsPanel");
            var sshHostInput = this.FindControl<TextBox>("SshHostInput");
            var sshPortInput = this.FindControl<NumericUpDown>("SshPortInput");
            var sshUserInput = this.FindControl<TextBox>("SshUserInput");
            var sshKeyPathInput = this.FindControl<TextBox>("SshKeyPathInput");
            var btnBrowseSshKey = this.FindControl<Button>("BtnBrowseSshKey");
            var jumpList = this.FindControl<ComboBox>("JumpHostList");
            var radioAgent = this.FindControl<RadioButton>("RadioAuthAgent");
            var radioKey = this.FindControl<RadioButton>("RadioAuthKey");
            var checkFont = this.FindControl<CheckBox>("CheckOverrideFont");
            var checkSize = this.FindControl<CheckBox>("CheckOverrideSize");
            var checkTheme = this.FindControl<CheckBox>("CheckOverrideTheme");
            var overrideFontList = this.FindControl<ComboBox>("OverrideFontList");
            var overrideFontSize = this.FindControl<NumericUpDown>("OverrideFontSizeInput");
            var ligatureToggle = this.FindControl<CheckBox>("LigatureToggle");
            var checkLigatures = this.FindControl<CheckBox>("CheckOverrideLigatures");
            var overrideLigatureToggle = this.FindControl<CheckBox>("OverrideLigatureToggle");
            var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
            var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
            var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");
            var bgOpacityDisplay = this.FindControl<TextBlock>("BgImageOpacityDisplay");
            var importStatus = this.FindControl<TextBlock>("ImportStatusText");
            var btnImportWT = this.FindControl<Button>("BtnImportWT");
            var btnAddRule = this.FindControl<Button>("BtnAddRule");

            // Theme Editor Controls
            var btnEditTheme = this.FindControl<Button>("BtnEditTheme");
            var btnNewTheme = this.FindControl<Button>("BtnNewTheme");
            var btnCloseEditor = this.FindControl<Button>("BtnCloseEditor");
            var btnSaveTheme = this.FindControl<Button>("BtnSaveTheme");
            var btnDeleteTheme = this.FindControl<Button>("BtnDeleteTheme");
            var btnImportTheme = this.FindControl<Button>("BtnImportTheme");
            var themeEditorPanel = this.FindControl<Border>("ThemeEditorPanel");
            var editThemeNameInput = this.FindControl<TextBox>("EditThemeNameInput");
            var editThemeFgInput = this.FindControl<TextBox>("EditThemeFgInput");
            var editThemeBgInput = this.FindControl<TextBox>("EditThemeBgInput");
            var editThemeCursorInput = this.FindControl<TextBox>("EditThemeCursorInput");
            var themeEditorStatus = this.FindControl<TextBlock>("ThemeEditorStatus");

            if (btnImportTheme != null)
            {
                btnImportTheme.Click += async (s, e) =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel != null)
                    {
                        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                        {
                            Title = "Import Theme",
                            AllowMultiple = false,
                            FileTypeFilter = new[]
                            {
                                new Avalonia.Platform.Storage.FilePickerFileType("Theme Files") { Patterns = new[] { "*.json", "*.itermcolors" } },
                                new Avalonia.Platform.Storage.FilePickerFileType("Alacritty Theme") { Patterns = new[] { "*.toml" } }
                            }
                        });

                        if (files.Count > 0)
                        {
                            string path = files[0].Path.LocalPath;
                            string importedThemeName = _settings.ThemeManager.ImportTheme(path);
                            if (!string.IsNullOrEmpty(importedThemeName))
                            {
                                PopulateThemes();
                                // Select the imported theme
                                if (themeList != null)
                                {
                                    foreach (ComboBoxItem it in themeList.Items.Cast<ComboBoxItem>())
                                    {
                                        if (it.Content?.ToString() == importedThemeName)
                                        {
                                            themeList.SelectedItem = it;
                                            OnThemeChanged?.Invoke(importedThemeName);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                };
            }

            if (btnEditTheme != null)
            {
                btnEditTheme.Click += (s, e) =>
                {
                    if (themeList?.SelectedItem is ComboBoxItem item)
                    {
                        var themeName = item.Content?.ToString() ?? "Default";
                        var theme = _settings.ThemeManager.GetTheme(themeName);
                        OpenThemeEditor(theme.Clone());
                    }
                };
            }

            if (btnNewTheme != null)
            {
                btnNewTheme.Click += (s, e) =>
                {
                    OpenThemeEditor(new TerminalTheme { Name = "New Theme" });
                };
            }

            if (btnCloseEditor != null)
            {
                btnCloseEditor.Click += (s, e) =>
                {
                    if (themeEditorPanel != null) themeEditorPanel.IsVisible = false;
                };
            }

            if (btnSaveTheme != null)
            {
                btnSaveTheme.Click += (s, e) =>
                {
                    if (_editingTheme != null)
                    {
                        if (string.IsNullOrWhiteSpace(editThemeNameInput?.Text))
                        {
                            if (themeEditorStatus != null) themeEditorStatus.Text = "Name required";
                            return;
                        }

                        _editingTheme.Name = editThemeNameInput.Text;
                        _settings.ThemeManager.SaveTheme(_editingTheme);
                        PopulateThemes();

                        // Select the saved theme in the list
                        if (themeList != null)
                        {
                            foreach (ComboBoxItem it in themeList.Items.Cast<ComboBoxItem>())
                            {
                                if (it.Content?.ToString() == _editingTheme.Name)
                                {
                                    themeList.SelectedItem = it;
                                    _settings.RefreshActiveTheme();
                                    OnThemeChanged?.Invoke(_editingTheme.Name);
                                    break;
                                }
                            }
                        }

                        // OnThemeChanged refreshes the host window, but nothing above re-applies
                        // the palette to THIS window when the saved theme was already the selected
                        // one (editing colors of the active theme) - the selection change is a
                        // no-op, so SelectionChanged never fires and the editor's new background,
                        // sidebar, and card colors only showed up after closing and reopening
                        // Settings (or restarting). Re-apply here so the edit lands live.
                        ApplyTheme();

                        if (themeEditorStatus != null) themeEditorStatus.Text = "Saved!";
                        DispatcherTimer.RunOnce(() => { if (themeEditorStatus != null) themeEditorStatus.Text = ""; }, TimeSpan.FromSeconds(2));
                    }
                };
            }

            if (btnDeleteTheme != null)
            {
                btnDeleteTheme.Click += (s, e) =>
                {
                    if (_editingTheme != null && _editingTheme.Name != "Default")
                    {
                        _settings.ThemeManager.DeleteTheme(_editingTheme.Name);
                        PopulateThemes();
                        if (themeList != null) themeList.SelectedIndex = 0;
                        if (themeEditorPanel != null) themeEditorPanel.IsVisible = false;
                    }
                };
            }

            // Wire up ANSI swatch inputs
            for (int i = 0; i < 16; i++)
            {
                int index = i;
                var swatchBtn = this.FindControl<Button>($"EditSwatch{index}");
                if (swatchBtn != null)
                {
                    swatchBtn.Click += (s, e) => OpenSwatchFlyout(swatchBtn, index);
                }
            }

            // Real-time updates for global color inputs
            if (editThemeFgInput != null) editThemeFgInput.TextChanged += (s, e) =>
            {
                if (Color.TryParse(editThemeFgInput.Text, out var color) && _editingTheme != null)
                {
                    _editingTheme.Foreground = TermColorHelper.FromAvaloniaColor(color);
                    UpdateThemePreview(_editingTheme, "Editor");
                }
            };
            if (editThemeBgInput != null) editThemeBgInput.TextChanged += (s, e) =>
            {
                if (Color.TryParse(editThemeBgInput.Text, out var color) && _editingTheme != null)
                {
                    _editingTheme.Background = TermColorHelper.FromAvaloniaColor(color);
                    UpdateThemePreview(_editingTheme, "Editor");
                }
            };
            if (editThemeCursorInput != null) editThemeCursorInput.TextChanged += (s, e) =>
            {
                if (Color.TryParse(editThemeCursorInput.Text, out var color) && _editingTheme != null)
                {
                    _editingTheme.CursorColor = TermColorHelper.FromAvaloniaColor(color);
                    UpdateThemePreview(_editingTheme, "Editor");
                }
            };

            if (themeList != null)
            {
                themeList.SelectionChanged += (s, e) =>
                {
                    if (themeList.SelectedItem is ComboBoxItem item)
                    {
                        var theme = _settings.ThemeManager.GetTheme(item.Content?.ToString() ?? "Default");
                        UpdateThemePreview(theme, "Main");
                    }
                };
            }

            if (overrideThemeList != null)
            {
                overrideThemeList.SelectionChanged += (s, e) =>
                {
                    if (overrideThemeList.SelectedItem is ComboBoxItem item)
                    {
                        var theme = _settings.ThemeManager.GetTheme(item.Content?.ToString() ?? "Default");
                        UpdateThemePreview(theme, "Override");
                    }
                };
            }

            LoadCurrentSettings();
            PopulateProfilesList();
            InitializeShortcutEditor();
            LoadTitleBarDraft();
            RebuildTitleBarRows();
            ApplyTheme();

            // LoadCurrentSettings' theme selection usually initializes the preview via
            // SelectionChanged, but only when an item actually matches _settings.ThemeName - a
            // saved name the manager normalizes (legacy "Default (Dark)") or a since-deleted
            // theme leaves the combo empty and the preview stuck on its hardcoded dark XAML
            // background. Paint it from the active theme directly so it is always current.
            UpdateThemePreview(_settings.ActiveTheme, "Main");

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _statusTimer.Tick += (s, e) => RefreshForwardsList();
            _statusTimer.Start();

            this.Closed += (s, e) =>
            {
                _statusTimer?.Stop();
                _statusTimer = null;
            };

            // Profile Controls
            if (profilesListBox != null)
            {
                profilesListBox.SelectionChanged += (s, e) =>
                {
                    if (profilesListBox.SelectedItem is ListBoxItem item && item.Tag is TerminalProfile profile)
                    {
                        SwitchSelectedProfile(profile);
                    }
                };
            }

            if (btnAddProfile != null)
            {
                btnAddProfile.Click += (s, e) =>
                {
                    // Seeded with this platform's shell, not cmd.exe: on Linux and macOS the
                    // latter gave every freshly added profile a command that cannot spawn.
                    var newProfile = new TerminalProfile { Name = "New Profile", Command = ShellHelper.GetDefaultShell(), Type = ConnectionType.Local };
                    _profilesList.Add(newProfile);
                    PopulateProfilesList();
                    if (profilesListBox != null) profilesListBox.SelectedIndex = _profilesList.Count - 1;
                };
            }

            if (btnDeleteProfile != null)
            {
                btnDeleteProfile.Click += (s, e) =>
                {
                    if (_selectedProfile != null && _profilesList.Count > 1)
                    {
                        var index = _profilesList.IndexOf(_selectedProfile);
                        _profilesList.Remove(_selectedProfile);
                        PopulateProfilesList();
                        if (profilesListBox != null) profilesListBox.SelectedIndex = Math.Clamp(index, 0, _profilesList.Count - 1);
                    }
                };
            }

            if (btnImportWT != null)
            {
                btnImportWT.Click += (s, e) =>
                {
                    var imported = ProfileImporter.ImportWindowsTerminalProfiles();
                    int added = 0;
                    foreach (var p in imported)
                    {
                        if (p.Type != ConnectionType.Local)
                        {
                            continue;
                        }

                        if (!_profilesList.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            _profilesList.Add(p);
                            added++;
                        }
                    }
                    if (added > 0)
                    {
                        PopulateProfilesList();
                        if (profilesListBox != null) profilesListBox.SelectedIndex = _profilesList.Count - 1;
                        if (importStatus != null) importStatus.Text = $"Imported {added} profiles.";
                    }
                    else
                    {
                        if (importStatus != null) importStatus.Text = "No new profiles found.";
                    }
                };
            }

            WireRemoteShellIntegrationRow();
            WireClearCommandAssistHistoryRow();
            WireCommandAssistSnippetsRow();

            if (btnSetDefault != null)
            {
                btnSetDefault.Click += (s, e) =>
                {
                    if (_selectedProfile != null)
                    {
                        _settings.DefaultProfileId = _selectedProfile.Id;
                        PopulateProfilesList(); // Refresh labels
                    }
                };
            }

            // Profile Editor Inputs
            if (nameInput != null) nameInput.KeyUp += (s, e) => { if (_selectedProfile != null) { _selectedProfile.Name = nameInput.Text ?? ""; RefreshProfileListItem(_selectedProfile); } };
            if (commandInput != null) commandInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.Command = commandInput.Text ?? ""; };
            if (argsInput != null) argsInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.Arguments = argsInput.Text ?? ""; };
            if (cwdInput != null) cwdInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.StartingDirectory = cwdInput.Text ?? ""; };
            if (groupInput != null) groupInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.Group = groupInput.Text ?? "General"; };
            if (tagsInput != null) tagsInput.KeyUp += (s, e) =>
            {
                if (_selectedProfile != null)
                    _selectedProfile.Tags = (tagsInput.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            };

            // Connection Type and SSH Inputs
            if (typeList != null)
            {
                typeList.SelectedIndex = 0;
                typeList.SelectionChanged += (s, e) =>
                {
                    if (_selectedProfile != null)
                    {
                        _selectedProfile.Type = ConnectionType.Local;
                        if (sshPanel != null) sshPanel.IsVisible = false;
                    }
                };
            }

            if (sshHostInput != null) sshHostInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.SshHost = sshHostInput.Text ?? ""; };
            if (sshPortInput != null) sshPortInput.ValueChanged += (s, e) => { if (_selectedProfile != null && sshPortInput.Value.HasValue) _selectedProfile.SshPort = (int)sshPortInput.Value.Value; };
            if (sshUserInput != null) sshUserInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.SshUser = sshUserInput.Text ?? ""; };

            // Advanced SSH Controls
            if (jumpList != null)
            {
                jumpList.SelectionChanged += (s, e) =>
                {
                    if (_selectedProfile != null && jumpList.SelectedItem is ComboBoxItem item)
                    {
                        if (item.Tag is Guid gid) _selectedProfile.JumpHostProfileId = gid;
                        else _selectedProfile.JumpHostProfileId = null;
                    }
                };
            }

            if (radioAgent != null)
            {
                radioAgent.IsCheckedChanged += (s, e) =>
                {
                    if (_selectedProfile == null) return;
                    _selectedProfile.UseSshAgent = (radioAgent.IsChecked == true);

                    if (sshKeyPathInput != null) sshKeyPathInput.IsEnabled = !_selectedProfile.UseSshAgent;
                    if (btnBrowseSshKey != null) btnBrowseSshKey.IsEnabled = !_selectedProfile.UseSshAgent;
                };
            }

            if (btnBrowseSshKey != null)
            {
                btnBrowseSshKey.Click += async (s, e) =>
                {
                    var files = await this.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = "Select Private Key",
                        AllowMultiple = false
                    });

                    if (files.Count > 0 && sshKeyPathInput != null)
                    {
                        var path = files[0].Path.LocalPath;
                        sshKeyPathInput.Text = path;
                        if (_selectedProfile != null)
                        {
                            _selectedProfile.IdentityFilePath = path;
                            _selectedProfile.SshKeyPath = path; // Backward compat sync
                        }
                    }
                };
            }
            if (sshKeyPathInput != null) sshKeyPathInput.KeyUp += (s, e) =>
            {
                if (_selectedProfile != null)
                {
                    _selectedProfile.IdentityFilePath = sshKeyPathInput.Text ?? "";
                    _selectedProfile.SshKeyPath = sshKeyPathInput.Text ?? ""; // Sync
                }
            };

            // Overrides Logic
            if (checkFont != null)
            {
                checkFont.IsCheckedChanged += (s, e) =>
                {
                    if (_selectedProfile != null)
                    {
                        if (checkFont.IsChecked == true)
                        {
                            if (_selectedProfile.FontFamily == null) _selectedProfile.FontFamily = _settings.FontFamily;
                            if (overrideFontList != null)
                                foreach (var item in overrideFontList.Items.OfType<ComboBoxItem>())
                                    if (item.Content?.ToString() == _selectedProfile.FontFamily) overrideFontList.SelectedItem = item;
                        }
                        else
                        {
                            _selectedProfile.FontFamily = null;
                        }
                    }
                };
            }

            if (overrideFontList != null)
            {
                overrideFontList.SelectionChanged += (s, e) =>
                {
                    if (_selectedProfile != null && checkFont?.IsChecked == true && overrideFontList.SelectedItem is ComboBoxItem item)
                    {
                        _selectedProfile.FontFamily = item.Content?.ToString();
                    }
                };
            }

            if (checkSize != null)
            {
                checkSize.IsCheckedChanged += (s, e) =>
                {
                    if (_selectedProfile != null)
                    {
                        if (checkSize.IsChecked == true)
                        {
                            if (_selectedProfile.FontSize == null) _selectedProfile.FontSize = _settings.FontSize;
                            if (overrideFontSize != null) overrideFontSize.Value = (decimal)(_selectedProfile.FontSize ?? 14);
                        }
                        else
                        {
                            _selectedProfile.FontSize = null;
                        }
                    }
                };
            }

            if (overrideFontSize != null)
            {
                overrideFontSize.ValueChanged += (s, e) =>
                {
                    if (_selectedProfile != null && checkSize?.IsChecked == true && overrideFontSize.Value.HasValue)
                    {
                        _selectedProfile.FontSize = (double)overrideFontSize.Value.Value;
                    }
                };
            }

            if (checkTheme != null)
            {
                checkTheme.IsCheckedChanged += (s, e) =>
                {
                    if (_selectedProfile != null)
                    {
                        if (checkTheme.IsChecked == true)
                        {
                            if (_selectedProfile.ThemeName == null) _selectedProfile.ThemeName = _settings.ThemeName;
                            if (overrideThemeList != null)
                                foreach (var item in overrideThemeList.Items.OfType<ComboBoxItem>())
                                    if (item.Content?.ToString() == _selectedProfile.ThemeName) overrideThemeList.SelectedItem = item;
                        }
                        else
                        {
                            _selectedProfile.ThemeName = null;
                        }
                    }
                };
            }

            if (overrideThemeList != null)
            {
                overrideThemeList.SelectionChanged += (s, e) =>
                {
                    if (_selectedProfile != null && checkTheme?.IsChecked == true && overrideThemeList.SelectedItem is ComboBoxItem item)
                    {
                        _selectedProfile.ThemeName = item.Content?.ToString();
                    }
                };
            }

            // Ligatures
            if (ligatureToggle != null)
            {
                ligatureToggle.IsCheckedChanged += (s, e) =>
                {
                };
            }

            if (checkLigatures != null)
            {
                checkLigatures.IsCheckedChanged += (s, e) =>
                {
                    if (_selectedProfile != null)
                    {
                        if (checkLigatures.IsChecked == true)
                        {
                            if (_selectedProfile.EnableLigatures == null) _selectedProfile.EnableLigatures = _settings.EnableLigatures;
                            if (overrideLigatureToggle != null) overrideLigatureToggle.IsChecked = _selectedProfile.EnableLigatures;
                        }
                        else
                        {
                            _selectedProfile.EnableLigatures = null;
                        }
                    }
                };
            }

            if (overrideLigatureToggle != null)
            {
                overrideLigatureToggle.IsCheckedChanged += (s, e) =>
                {
                    if (_selectedProfile != null && checkLigatures?.IsChecked == true)
                    {
                        _selectedProfile.EnableLigatures = overrideLigatureToggle.IsChecked;
                    }
                };
            }

            // Core Settings
            if (fontList != null)
            {
                fontList.SelectionChanged += (s, e) =>
                {
                    if (fontList.SelectedItem is ComboBoxItem item && item.Content != null)
                    {
                        OnFontChanged?.Invoke(item.Content.ToString() ?? BundledFontCatalog.DefaultTerminalFontFamily);
                    }
                };
            }

            if (fontSizeInput != null)
            {
                fontSizeInput.ValueChanged += (s, e) =>
                {
                    if (fontSizeInput.Value.HasValue)
                    {
                        OnFontSizeChanged?.Invoke((double)fontSizeInput.Value.Value);
                    }
                };
            }

            if (themeList != null)
            {
                themeList.SelectionChanged += (s, e) =>
                {
                    if (themeList.SelectedItem is ComboBoxItem item && item.Content != null)
                    {
                        var themeName = item.Content.ToString() ?? "Default";
                        OnThemeChanged?.Invoke(themeName);
                        var theme = _settings.ThemeManager.GetTheme(themeName);
                        ApplyTheme(theme);
                    }
                };
            }

            // Bg Image
            void TriggerBgUpdate()
            {
                var path = bgPathInput?.Text ?? "";
                var opacity = bgOpacitySlider?.Value ?? 0.5;
                var stretch = (bgStretchList?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UniformToFill";
                OnBgImageChanged?.Invoke(path, opacity, stretch);
            }

            if (bgPathInput != null)
            {
                bgPathInput.PropertyChanged += (s, e) =>
                {
                    if (e.Property == TextBox.TextProperty) TriggerBgUpdate();
                };
            }

            if (bgOpacitySlider != null && bgOpacityDisplay != null)
            {
                bgOpacityDisplay.Text = $"{(int)(bgOpacitySlider.Value * 100)}%";
                bgOpacitySlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                    {
                        bgOpacityDisplay.Text = $"{(int)(bgOpacitySlider.Value * 100)}%";
                        TriggerBgUpdate();
                    }
                };
            }

            if (bgStretchList != null)
            {
                bgStretchList.SelectionChanged += (s, e) => TriggerBgUpdate();
            }

            if (opacitySlider != null && opacityDisplay != null)
            {
                opacityDisplay.Text = $"{(int)(opacitySlider.Value * 100)}%";
                opacitySlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                    {
                        opacityDisplay.Text = $"{(int)(opacitySlider.Value * 100)}%";
                        OnOpacityChanged?.Invoke(opacitySlider.Value);
                    }
                };
            }

            if (blurList != null)
            {
                blurList.SelectionChanged += (s, e) =>
                {
                    if (blurList.SelectedItem is ComboBoxItem item && item.Content != null)
                    {
                        OnBlurChanged?.Invoke(item.Content.ToString() ?? "Acrylic");
                    }
                };
            }

            var btnBrowse = this.FindControl<Button>("BtnBrowseImage");
            if (btnBrowse != null)
            {
                btnBrowse.Click += async (s, e) =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel != null)
                    {
                        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                        {
                            Title = "Select Background Image",
                            AllowMultiple = false,
                            FileTypeFilter = new[]
                            {
                                new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } }
                            }
                        });

                        if (files.Count > 0)
                        {
                            if (bgPathInput != null)
                            {
                                bgPathInput.Text = files[0].Path.LocalPath;
                                TriggerBgUpdate();
                            }
                        }
                    }
                };
            }

            var btnClearBg = this.FindControl<Button>("BtnClearImage");
            if (btnClearBg != null)
            {
                btnClearBg.Click += (s, e) =>
                {
                    if (bgPathInput != null)
                    {
                        bgPathInput.Text = "";
                        TriggerBgUpdate();
                    }
                };
            }

            if (btnAddRule != null) btnAddRule.Click += BtnAddForward_Click;
            if (btnSave != null) btnSave.Click += (s, e) => SaveAndClose();
            if (btnCancel != null) btnCancel.Click += (s, e) => Close();

            WireBackupSection();

            // Auto-select profile if requested
            if (initialProfileId.HasValue && profilesListBox != null)
            {
                foreach (var item in profilesListBox.Items.OfType<ListBoxItem>())
                {
                    if (item.Tag is TerminalProfile p && p.Id == initialProfileId.Value)
                    {
                        profilesListBox.SelectedItem = item;
                        profilesListBox.ScrollIntoView(item);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Mirror the current tab control selection into the sidebar list boxes.
        /// InterfaceNav owns tabs 0-2 (Appearance / Profiles / Shortcuts), AssistantNav owns
        /// tabs 3-4 (Command Assist / Agent Access), ConnectionNav owns tab 5 (SSH), DataNav
        /// owns tab 6 (Backup & Restore). The other list boxes are cleared so only one item
        /// ever reads as selected.
        /// </summary>
        private static void SyncSidebarFromTabs(
            TabControl tabs,
            ListBox interfaceNav,
            ListBox assistantNav,
            ListBox connectionNav,
            ListBox dataNav)
        {
            var idx = tabs.SelectedIndex;

            interfaceNav.SelectedIndex = -1;
            assistantNav.SelectedIndex = -1;
            connectionNav.SelectedIndex = -1;
            dataNav.SelectedIndex = -1;

            if (idx < 0) return;

            if (idx < 3) interfaceNav.SelectedIndex = idx;
            else if (idx < 5) assistantNav.SelectedIndex = idx - 3;
            else if (idx < 6) connectionNav.SelectedIndex = idx - 5;
            else dataNav.SelectedIndex = idx - 6;
        }

        /// <summary>
        /// Selects the Backup &amp; Restore tab. Used by the command palette's three backup
        /// entries (see <c>MainWindow.OpenSettingsToBackupPage</c>).
        ///
        /// Looks the tab up by its <c>Header</c> ("Backup") rather than by numeric position, so
        /// this is immune to Backup's index changing in either direction - a tab removed or
        /// reordered ahead of it, or a new tab appended after it (Backup is the last tab today,
        /// per the constructor's remarks on sidebar offsets, but nothing requires it to stay
        /// that way). A position-based index - hardcoded or derived from <c>Items.Count</c> -
        /// would get the "appended after" direction wrong. If the tab is ever renamed or removed,
        /// this is a no-op rather than selecting the wrong page, matching every other
        /// <c>FindControl</c> guard in this file.
        /// </summary>
        public void SelectBackupPage()
        {
            var tabs = this.FindControl<TabControl>("MainTabs");
            if (tabs is null) return;

            var backupTab = tabs.Items.OfType<TabItem>().FirstOrDefault(t => (string?)t.Header == "Backup");
            if (backupTab is null) return;

            tabs.SelectedIndex = tabs.Items.IndexOf(backupTab);
        }

        /// <summary>
        /// Test seam for the "confirm before restoring" gate. Null (the default, and always the
        /// case in production) uses the real <see cref="ConfirmRestoreAsync"/>, which shows an
        /// actual modal <see cref="Window.ShowDialog"/> - not drivable headlessly without risking
        /// a hang (see <see cref="ConfirmRestoreAsync"/>'s remarks). Tests substitute a synchronous
        /// fake here instead, so both the "confirmed" and "declined" branches of the Restore click
        /// handler are covered by something that actually runs in CI, without ever touching
        /// ShowDialog. Internal rather than public: this exists solely so
        /// NovaTerminal.App.Tests (an InternalsVisibleTo friend) can reach it - it is a test seam,
        /// not new public API.
        /// </summary>
        internal Func<SnapshotRow, System.Threading.Tasks.Task<bool>>? RestoreConfirmationOverride;

        /// <summary>
        /// Wires the Backup &amp; Restore page. All work goes through <see cref="BackupService"/>;
        /// this method only picks files and renders outcomes.
        /// </summary>
        private void WireBackupSection()
        {
            var service = new BackupService(AppPaths.RootDirectory, log: AppLogger.Log);

            var btnExport = this.FindControl<Button>("BtnBackupExport");
            var btnImport = this.FindControl<Button>("BtnBackupImport");
            var btnRestore = this.FindControl<Button>("BtnRestoreSnapshot");
            var status = this.FindControl<TextBlock>("BackupStatusText");
            var snapshotList = this.FindControl<ListBox>("SnapshotList");

            void SetStatus(string message, bool success)
            {
                if (status is null) return;
                status.Text = message;
                status.Foreground = success
                    ? (IBrush?)this.FindResource("NtGreen")
                    : (IBrush?)this.FindResource("NtRed");
            }

            void RefreshSnapshots()
            {
                if (snapshotList is null) return;

                // WireBackupSection runs unconditionally from the constructor, so a locked
                // backups directory, a permissions error, or a transient antivirus lock during
                // Directory.GetFiles must not propagate out of here — that would stop Settings
                // from opening at all. Fall back to an empty list and say so instead.
                try
                {
                    var rows = service.ListSnapshots()
                        .Select(s => new SnapshotRow(
                            s.Id,
                            $"{s.CreatedUtc.LocalDateTime:yyyy-MM-dd HH:mm}  ·  {ReasonLabel(s.Reason)}  ·  {s.SizeBytes / 1024.0:N0} KB",
                            s.Reason,
                            s.CreatedUtc))
                        .ToArray();

                    snapshotList.ItemsSource = rows;
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    snapshotList.ItemsSource = Array.Empty<SnapshotRow>();
                    SetStatus($"Could not read snapshots: {ex.Message}", success: false);
                }
            }

            if (snapshotList is not null)
            {
                // ListBox has no WPF-style DisplayMemberBinding in Avalonia; a FuncDataTemplate
                // is the established pattern here (see MainWindow.ShowAgentActivityJournalAsync's
                // agent-activity ItemsControl).
                snapshotList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SnapshotRow>((row, _) =>
                    new TextBlock { Text = row?.Display ?? string.Empty, Margin = new Thickness(4, 2) });
            }

            RefreshSnapshots();

            if (btnExport != null)
            {
                btnExport.Click += async (_, _) =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel is null) return;

                    var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerSaveOptions
                        {
                            Title = "Export NovaTerminal configuration",
                            SuggestedFileName = $"novaterminal-{DateTime.Now:yyyy-MM-dd}{BackupService.BundleExtension}",
                            DefaultExtension = BackupService.BundleExtension.TrimStart('.')
                        });

                    if (file is null) return;

                    var outcome = service.Export(file.Path.LocalPath);
                    SetStatus(outcome.Success ? $"Exported to {file.Name}." : outcome.Message, outcome.Success);
                };
            }

            if (btnImport != null)
            {
                btnImport.Click += async (_, _) =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel is null) return;

                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerOpenOptions
                        {
                            Title = "Import NovaTerminal configuration",
                            AllowMultiple = false
                        });

                    if (files.Count == 0) return;
                    string path = files[0].Path.LocalPath;

                    // Inspect first so the confirmation names what is about to change.
                    var inspection = service.Inspect(path);
                    if (!inspection.Success)
                    {
                        SetStatus(inspection.Message, success: false);
                        return;
                    }

                    var mode = await PromptForImportModeAsync(inspection.Inspection!);
                    if (mode is null) return;

                    var outcome = service.Import(path, mode.Value);
                    if (outcome.Success)
                    {
                        // I1: a successful Import already changed settings.json (and possibly
                        // profiles.json, keybindings, etc.) on disk. _settings and everything
                        // derived from it at construction time are now a stale snapshot of the
                        // PRE-import state - if this window's Save is clicked afterward, it would
                        // silently overwrite the very change Import just made with that stale
                        // snapshot. Reload before anything else can touch _settings again.
                        await ReloadSettingsAfterExternalChangeAsync();
                    }

                    // M2: surface the service's own outcome message rather than a generic one -
                    // it carries the "connection passwords are not included" note when
                    // Connections was among the imported categories, the only place a Settings
                    // window user ever sees that warning.
                    SetStatus(
                        outcome.Success
                            ? $"{outcome.Message} Restart NovaTerminal to pick up all changes."
                            : outcome.Message,
                        outcome.Success);
                    RefreshSnapshots();
                };
            }

            if (btnRestore != null)
            {
                btnRestore.Click += async (_, _) =>
                {
                    if (snapshotList?.SelectedItem is not SnapshotRow row)
                    {
                        SetStatus("Select a snapshot first.", success: false);
                        return;
                    }

                    // Restore overwrites live configuration immediately, and unlike Import (which
                    // offers Merge/Replace) it previously asked nothing at all — the more
                    // surprising of the two destructive actions on this page got the weaker gate.
                    // Cancel must mean no restore; there is no default "yes" other than the
                    // affirmative button. RestoreConfirmationOverride is a test seam (see its own
                    // doc comment) - production always falls through to the real ConfirmRestoreAsync.
                    Func<SnapshotRow, System.Threading.Tasks.Task<bool>> confirm = RestoreConfirmationOverride ?? ConfirmRestoreAsync;
                    bool confirmed = await confirm(row);
                    if (!confirmed) return;

                    var outcome = service.Restore(row.Id);
                    if (outcome.Success)
                    {
                        // I1: same reasoning as the Import handler above - Restore just changed
                        // settings.json (and possibly more) on disk, and this window's in-memory
                        // state must not be allowed to stomp it on a later Save.
                        await ReloadSettingsAfterExternalChangeAsync();
                    }

                    SetStatus(
                        outcome.Success
                            ? $"{outcome.Message} Restart NovaTerminal to pick up all changes."
                            : outcome.Message,
                        outcome.Success);
                    RefreshSnapshots();
                };
            }
        }

        /// <summary>
        /// I1: a successful Import or Restore changes settings.json (and possibly
        /// profiles/keybindings/title-bar layout) on disk out from under this already-open
        /// window. <c>_settings</c> and everything the constructor derived from it once, up
        /// front, are now a stale pre-change snapshot; if <see cref="SaveAndClose"/> ran against
        /// that snapshot afterward, it would silently revert the very change the user just made
        /// (the bug this fix closes). Reloading <c>_settings</c> alone is not enough -
        /// <see cref="SaveAndClose"/> also rebuilds <c>_settings.Profiles</c> from
        /// <c>_profilesList</c>, <c>_settings.Keybindings</c> from <c>_shortcutDraftBindings</c>,
        /// and <c>_settings.TitleBarItems</c> / <c>_settings.TitleBarOrder</c> from
        /// <c>_titleBarDraft</c> — each of those must be re-derived from the freshly reloaded
        /// settings too, or Save would still overwrite the imported/restored values for exactly
        /// those three areas even though every OTHER field would now be correctly preserved.
        /// Mirrors the exact sequence the constructor itself runs once at startup
        /// (profiles/shortcuts/title-bar derivation, then the UI-control population methods), so
        /// the already-open window's controls reflect the new on-disk state too rather than
        /// merely fixing what gets written back on the next Save.
        /// <para>
        /// Asynchronous solely because of the trailing snippet reload (see the comment at the end
        /// of the body); everything else here is synchronous and completes before the first
        /// suspension point. Callers must await it - both the Import and Restore click handlers
        /// already do - so the whole window, snippet rows included, is consistent with disk before
        /// the success status is shown.
        /// </para>
        /// </summary>
        private async System.Threading.Tasks.Task ReloadSettingsAfterExternalChangeAsync()
        {
            // F1: record that configuration was replaced externally so MainWindow.OpenSettings can
            // adopt the reload below no matter how this dialog eventually closes. Set unconditionally
            // here rather than only in the Import/Restore click handlers, so any current or future
            // caller of this method gets the same guarantee.
            ConfigurationReplacedExternally = true;

            // Captured before anything below moves on: _selectedProfile currently points at an
            // object inside the PRE-reload _profilesList, which is about to be discarded and
            // rebuilt from fresh TerminalProfile instances. The Id is the only thing that still
            // means anything about that selection once the rebuild happens.
            Guid? previousSelectedProfileId = _selectedProfile?.Id;

            _settings = TerminalSettings.Load();

            _profilesList = BuildLocalProfilesForEditor(_settings.Profiles);
            _settings.DefaultProfileId = ResolveDefaultLocalProfileId(_settings.DefaultProfileId, _profilesList);
            _shortcutDraftBindings = new Dictionary<string, string>(_settings.Keybindings, StringComparer.OrdinalIgnoreCase);

            // Fix (Codex review round 2, PR #362): the exact same I1-residual-#1 gap as
            // PopulateThemes below, for fonts. PopulateFonts seeds its choices from
            // BuildFontFamilyChoices(_settings.FontFamily, _selectedProfile?.FontFamily) (round 3:
            // both are seeded unconditionally now, not ??-ed down to one - see PopulateFonts' own
            // remarks), explicitly adding whatever font is currently configured even when it is
            // not installed locally - so a bundle imported from another machine naming a font
            // absent here needs this rerun to make that font selectable at all. Without it,
            // LoadCurrentSettings' font-selection loop below has no matching ComboBoxItem, leaves
            // FontList.SelectedItem on the stale pre-reload font, and a subsequent Save writes that
            // stale font back over the just-imported one. Must run before LoadCurrentSettings,
            // exactly like the constructor's own sequence (PopulateFonts, then PopulateThemes,
            // then LoadCurrentSettings). Note that at this point in Reload, _selectedProfile is
            // still the PRE-reload object (RepointSelectedProfileAfterReload runs later) - that's
            // fine for ordering (matches the constructor, where _selectedProfile is always null
            // here) but not for its VALUE, which is why _settings.FontFamily is seeded
            // unconditionally rather than only as a ??-fallback.
            PopulateFonts();

            // Fix (I1 residual #1): the theme combo boxes were filled from disk once, at
            // construction (PopulateThemes, called before LoadCurrentSettings there too - see the
            // constructor). An import/restore can bring a new theme file AND a settings.json
            // naming it; without repopulating here first, LoadCurrentSettings' theme-selection
            // loop below has no matching ComboBoxItem to select, SelectedItem is left on the
            // stale pre-reload theme, and a subsequent Save would write that stale ThemeName back
            // - the original I1 bug, narrowed to one field. Order matters: this must run before
            // LoadCurrentSettings, exactly like the constructor's own sequence.
            PopulateThemes();

            LoadCurrentSettings();
            PopulateProfilesList();
            RepointSelectedProfileAfterReload(previousSelectedProfileId);

            var shortcutSearchInput = this.FindControl<TextBox>("ShortcutSearchInput");
            PopulateShortcutBindingsPanel(shortcutSearchInput?.Text ?? "");

            LoadTitleBarDraft();
            RebuildTitleBarRows();

            // The one populate path that is NOT reachable by repeating the constructor's sequence,
            // and the reason this method is async at all. PopulateCommandAssistSnippetsPanel is
            // driven by WireCommandAssistSnippetsRow's one-shot `Opened` handler rather than by the
            // constructor, because the snippet store arrives by property assignment after the
            // constructor has run. This method runs on an ALREADY-OPEN window and never reopens it,
            // so `Opened` does not fire again: without this call, an import/restore whose bundle
            // carries a different command-assist/snippets.json (which BackupService replaces
            // wholesale in BOTH modes) leaves the visible rows describing the pre-import file until
            // the window is closed and reopened - and the user can edit or delete against them.
            //
            // Awaited last, after every synchronous repopulation above, so the parts of this method
            // that cannot fail on I/O are already done before the first suspension point. Null-safe
            // for a window opened outside MainWindow.OpenSettings: TryGetSnippetEditor returns null
            // when CommandAssistSnippetStore is unset and the panel simply says so.
            await ReloadCommandAssistSnippetsAsync();

            // Codex review (PR #364, P2): refreshing this window's panel is only half of what a
            // snippet change owes the app. MainWindow.OpenSettings subscribes
            // DismissCommandAssistSurfaces to this event, and the add/edit/delete paths all raise it,
            // because the rows an open assist bubble or popup is showing are a snapshot of a ranking
            // pass that nothing else invalidates (see DismissCommandAssistSurfaces' own remarks,
            // written for the identical "Clear history" case). An import/restore is the most
            // destructive snippet change there is - snippets.json is replaced wholesale in BOTH modes
            // - so without this a popup left open behind the dialog goes on displaying, and
            // accepting, snippets the import just deleted.
            //
            // Raised unconditionally rather than only when the bundle actually carried Snippets, for
            // the same reason ConfigurationReplacedExternally is set unconditionally above: dismissing
            // a surface that did not need it costs the user nothing (the next keystroke rebuilds it
            // from the store), while missing one shows them rows that no longer exist.
            OnCommandAssistSnippetsChanged?.Invoke();
        }

        /// <summary>
        /// Fix (I1 residual #2): <c>_selectedProfile</c> points at an entry of the PRE-reload
        /// <c>_profilesList</c>. <see cref="ReloadSettingsAfterExternalChangeAsync"/> rebuilds
        /// <c>_profilesList</c> with fresh <see cref="TerminalProfile"/> instances (freshly
        /// deserialized from the reloaded settings.json) but, without this, never re-points
        /// <c>_selectedProfile</c> at the corresponding new instance - it is left dangling,
        /// referencing an object no longer reachable from <c>_profilesList</c>. The profile
        /// editor's KeyUp handlers (bound to <c>_selectedProfile</c> directly) would keep mutating
        /// that detached object, so edits made after an import/restore are silently dropped at
        /// Save; Delete would likewise become a no-op since the object is no longer in the list to
        /// remove.
        /// </summary>
        /// <remarks>
        /// Re-points by <see cref="TerminalProfile.Id"/> - the one thing that still identifies
        /// "the same" profile across the rebuild - by setting the ProfilesListBox's SelectedItem
        /// to the matching new item. That raises the same SelectionChanged handler a user
        /// re-clicking the profile by hand would, which calls <see cref="SwitchSelectedProfile"/>
        /// and re-derives every editor-pane control from the fresh instance - so this reuses the
        /// existing selection machinery rather than duplicating it. When the previously-selected
        /// profile no longer exists post-reload (the import/restore removed or renamed it),
        /// <c>_selectedProfile</c> is cleared and the editor pane's fields are blanked to match,
        /// rather than continuing to display a profile that is no longer in the list.
        /// </remarks>
        private void RepointSelectedProfileAfterReload(Guid? previousSelectedProfileId)
        {
            if (previousSelectedProfileId is not Guid id)
            {
                return; // Nothing was selected before the reload; nothing to re-point.
            }

            var profilesListBox = this.FindControl<ListBox>("ProfilesListBox");
            var matchedItem = profilesListBox?.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(item => item.Tag is TerminalProfile profile && profile.Id == id);

            if (matchedItem != null)
            {
                if (profilesListBox != null) profilesListBox.SelectedItem = matchedItem;
                return;
            }

            _selectedProfile = null;
            if (profilesListBox != null) profilesListBox.SelectedItem = null;
            ClearProfileEditorFields();
        }

        /// <summary>
        /// Blanks the profile editor pane's controls, mirroring the field set
        /// <see cref="SwitchSelectedProfile"/> populates but with neutral/empty values, so the UI
        /// does not keep showing a profile that is no longer in <c>_profilesList</c>. Does not
        /// touch event wiring - only sets control values, the same way <see cref="SwitchSelectedProfile"/>
        /// does.
        /// </summary>
        private void ClearProfileEditorFields()
        {
            var nameInput = this.FindControl<TextBox>("ProfileNameInput");
            if (nameInput != null) nameInput.Text = string.Empty;
            var commandInput = this.FindControl<TextBox>("ProfileCommandInput");
            if (commandInput != null) commandInput.Text = string.Empty;
            var argsInput = this.FindControl<TextBox>("ProfileArgsInput");
            if (argsInput != null) argsInput.Text = string.Empty;
            var cwdInput = this.FindControl<TextBox>("ProfileCwdInput");
            if (cwdInput != null) cwdInput.Text = string.Empty;
            var groupInput = this.FindControl<TextBox>("ProfileGroupInput");
            if (groupInput != null) groupInput.Text = string.Empty;
            var tagsInput = this.FindControl<TextBox>("ProfileTagsInput");
            if (tagsInput != null) tagsInput.Text = string.Empty;

            var sshHostInput = this.FindControl<TextBox>("SshHostInput");
            if (sshHostInput != null) sshHostInput.Text = string.Empty;
            var sshPortInput = this.FindControl<NumericUpDown>("SshPortInput");
            if (sshPortInput != null) sshPortInput.Value = null;
            var sshUserInput = this.FindControl<TextBox>("SshUserInput");
            if (sshUserInput != null) sshUserInput.Text = string.Empty;
            var sshKeyPathInput = this.FindControl<TextBox>("SshKeyPathInput");
            if (sshKeyPathInput != null) sshKeyPathInput.Text = string.Empty;
            var sshPasswordInput = this.FindControl<TextBox>("SshPasswordInput");
            if (sshPasswordInput != null) sshPasswordInput.Text = string.Empty;

            var checkOverrideFont = this.FindControl<CheckBox>("CheckOverrideFont");
            if (checkOverrideFont != null) checkOverrideFont.IsChecked = false;
            var checkOverrideSize = this.FindControl<CheckBox>("CheckOverrideSize");
            if (checkOverrideSize != null) checkOverrideSize.IsChecked = false;
            var checkOverrideTheme = this.FindControl<CheckBox>("CheckOverrideTheme");
            if (checkOverrideTheme != null) checkOverrideTheme.IsChecked = false;
            var checkOverrideLigatures = this.FindControl<CheckBox>("CheckOverrideLigatures");
            if (checkOverrideLigatures != null) checkOverrideLigatures.IsChecked = false;
        }

        private static string ReasonLabel(SnapshotReason reason) => reason switch
        {
            SnapshotReason.Auto => "automatic",
            SnapshotReason.PreImport => "before import",
            SnapshotReason.PreRestore => "before restore",
            _ => "automatic"
        };

        internal sealed record SnapshotRow(string Id, string Display, SnapshotReason Reason, DateTimeOffset CreatedUtc);

        /// <summary>
        /// The restore confirmation dialog's headline and body text for <paramref name="row"/>.
        /// Factored out of <see cref="ConfirmRestoreAsync"/> so the wording — which must name the
        /// snapshot being restored and describe what happens — is unit-testable on its own.
        /// </summary>
        /// <remarks>
        /// The dialog itself (<see cref="ConfirmRestoreAsync"/>, like <see cref="PromptForImportModeAsync"/>)
        /// is not: a real modal <c>Window.ShowDialog</c> with no owner ever shown and no button for
        /// anything to click does not return in this repo's headless test host — confirmed
        /// previously in <c>MainWindowShellExitTests</c> (a declined-confirmation branch behind the
        /// same pattern was deleted rather than risk the hang). This split keeps the requirement —
        /// name the snapshot's timestamp and reason, state that it replaces the categories the
        /// snapshot contains, and note the pre-restore snapshot taken first — covered by a test
        /// without driving the modal itself.
        /// </remarks>
        private static (string Headline, string Body) BuildRestoreConfirmationText(SnapshotRow row)
        {
            string when = $"{row.CreatedUtc.LocalDateTime:yyyy-MM-dd HH:mm}";
            string headline = $"Restore the snapshot from {when} ({ReasonLabel(row.Reason)})?";
            string body =
                "This replaces the categories the snapshot contains with their state at that time. " +
                "A snapshot of your current configuration is taken first, so this restore itself can be undone.";
            return (headline, body);
        }

        /// <summary>
        /// Confirms before restoring a snapshot. Restore is the more surprising destructive action
        /// on this page (Import at least offers Merge/Replace); this brings it in line. Returns
        /// false when the user cancels — there is no default "yes" other than the affirmative
        /// button.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> ConfirmRestoreAsync(SnapshotRow row)
        {
            var (headline, body) = BuildRestoreConfirmationText(row);

            bool confirmed = false;

            var dialog = new Window
            {
                Title = "Restore configuration",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var restoreButton = new Button { Content = "Restore", Classes = { "Pill" } };
            var cancelButton = new Button { Content = "Cancel", Classes = { "Pill" } };

            restoreButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
            cancelButton.Click += (_, _) => dialog.Close();

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = headline,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = body,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelButton, restoreButton }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return confirmed;
        }

        /// <summary>
        /// The Merge/Replace prompt's explanatory body text for a bundle whose contents are
        /// <paramref name="inspection"/>. Factored out of <see cref="PromptForImportModeAsync"/> so
        /// the wording is unit-testable without going anywhere near <c>ShowDialog</c> - the same
        /// split <see cref="BuildRestoreConfirmationText"/> uses for the Restore confirmation.
        /// </summary>
        /// <remarks>
        /// (P2, Codex review round 2, PR #362): the base sentence — "Merge keeps items you have
        /// locally that the bundle does not contain" — is false for Snippets. <c>BackupService</c>'s
        /// <c>BuildPlan</c> replaces <c>snippets.json</c> wholesale in BOTH modes (a deliberate
        /// design decision, spec'd: the file is a flat array with no stable id, so there is nothing
        /// to merge by — see <c>Snippets_AlwaysReplacedWholesale</c>). A user choosing Merge
        /// specifically to keep local snippets would lose them with no warning. This fixes the
        /// copy, not the semantics: the caveat only appears when the bundle actually contains the
        /// Snippets category (an <see cref="BundleInspection.ItemCounts"/> lookup — <c>Inspect</c>
        /// already gives the caller this for free), so a bundle without Snippets sees the same
        /// wording as before.
        /// </remarks>
        private static string BuildImportModeBodyText(BundleInspection inspection)
        {
            bool bundleHasSnippets = inspection.ItemCounts.TryGetValue(BackupCategory.Snippets, out int snippetCount)
                && snippetCount > 0;

            string snippetsCaveat = bundleHasSnippets
                ? " Snippets are always replaced entirely in either mode — Merge does not apply to them."
                : string.Empty;

            return "Merge keeps items you have locally that the bundle does not contain. " +
                   "Replace makes the bundle the truth for the categories above. " +
                   "A snapshot is taken first either way, so you can roll back." +
                   snippetsCaveat;
        }

        /// <summary>
        /// Asks whether to merge or replace, showing what the bundle contains. Returns null
        /// when the user cancels. Import is destructive, so there is no default —
        /// the user must pick.
        /// </summary>
        private async System.Threading.Tasks.Task<ImportMode?> PromptForImportModeAsync(BundleInspection inspection)
        {
            string summary = string.Join(
                ", ",
                inspection.ItemCounts
                    .Where(pair => pair.Value > 0)
                    .Select(pair => $"{pair.Value} {pair.Key.ToString().ToLowerInvariant()}"));

            string body = BuildImportModeBodyText(inspection);

            ImportMode? choice = null;

            var dialog = new Window
            {
                Title = "Import configuration",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var mergeButton = new Button { Content = "Merge", Classes = { "Pill" } };
            var replaceButton = new Button { Content = "Replace", Classes = { "Pill" } };
            var cancelButton = new Button { Content = "Cancel", Classes = { "Pill" } };

            mergeButton.Click += (_, _) => { choice = ImportMode.Merge; dialog.Close(); };
            replaceButton.Click += (_, _) => { choice = ImportMode.Replace; dialog.Close(); };
            cancelButton.Click += (_, _) => dialog.Close();

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"This bundle contains: {summary}.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = body,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelButton, mergeButton, replaceButton }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return choice;
        }

        /// <summary>
        /// Brings the Appearance tab's "TITLE BAR" section header into view, so the user opening
        /// Settings via the title bar's right-click "Customize Title Bar..." lands on the section
        /// itself instead of the theme editor/preview above it (PR #342 Codex round 6).
        /// </summary>
        /// <remarks>
        /// Targets <c>TitleBarSectionHeader</c> - the <c>SectionHeader</c>-classed "TITLE BAR"
        /// <see cref="TextBlock"/> - rather than <c>TitleBarItemsPanel</c> (the rows themselves),
        /// so the user sees the section title and its description first, not a mid-list row with
        /// no context above it.
        /// </remarks>
        private void ScrollToTitleBarSection()
        {
            var header = this.FindControl<TextBlock>("TitleBarSectionHeader");
            header?.BringIntoView();
        }

        private void PopulateFonts()
        {
            var fontList = this.FindControl<ComboBox>("FontList");
            var overrideFontList = this.FindControl<ComboBox>("OverrideFontList");

            if (fontList != null) fontList.Items.Clear();
            if (overrideFontList != null) overrideFontList.Items.Clear();

            // Codex review round 3: seed BOTH the global font and the selected profile's own
            // override (if any), rather than ??-ing them down to one. In the reload path
            // (ReloadSettingsAfterExternalChangeAsync), _selectedProfile is still the PRE-reload
            // object — RepointSelectedProfileAfterReload does not run until after this — so a
            // profile with a font override used to make _selectedProfile?.FontFamily win the ??
            // and silently hide _settings.FontFamily from this seeding entirely. That left a
            // freshly-imported global font unselectable (not data loss - LoadCurrentSettings'
            // selection loop then finds nothing, SelectedItem stays null, and SaveAndClose's
            // `is ComboBoxItem` guard skips the write - but the user could not pick the imported
            // font until the window was reopened). Adding both to the SortedSet costs nothing:
            // BuildFontFamilyChoices dedupes case-insensitively, and this is only ever a "make
            // sure it's visible even if not installed" seed - not a selection decision.
            var fonts = BuildFontFamilyChoices(
                    SkiaSharp.SKFontManager.Default.FontFamilies,
                    _settings.FontFamily,
                    _selectedProfile?.FontFamily)
                .Select(f => new ComboBoxItem { Content = f })
                .ToList();

            foreach (var f in fonts)
            {
                if (fontList != null) fontList.Items.Add(f);
                // Create a separate instance for the second list to avoid visual tree parenting issues
                if (overrideFontList != null) overrideFontList.Items.Add(new ComboBoxItem { Content = f.Content });
            }
        }

        /// <param name="additionalConfiguredFontFamily">
        /// A second font name to guarantee is present, independent of
        /// <paramref name="configuredFontFamily"/> (Codex review round 3) — e.g. a selected
        /// profile's own font override, seeded alongside the global font rather than instead of
        /// it. Both are seeded unconditionally (each ignored only when null/blank); which one
        /// ultimately gets selected is <c>LoadCurrentSettings</c>'/the caller's decision, not
        /// this method's — it only guarantees visibility in the choice list.
        /// </param>
        internal static System.Collections.Generic.List<string> BuildFontFamilyChoices(
            System.Collections.Generic.IEnumerable<string> systemFonts,
            string? configuredFontFamily,
            string? additionalConfiguredFontFamily = null)
        {
            var names = new System.Collections.Generic.SortedSet<string>(
                systemFonts?.Where(f => !string.IsNullOrWhiteSpace(f)) ?? System.Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            names.Add(BundledFontCatalog.DefaultTerminalFontFamily);

            if (!string.IsNullOrWhiteSpace(configuredFontFamily))
            {
                names.Add(configuredFontFamily);
            }

            if (!string.IsNullOrWhiteSpace(additionalConfiguredFontFamily))
            {
                names.Add(additionalConfiguredFontFamily);
            }

            return names.ToList();
        }

        private void PopulateThemes()
        {
            _settings.ThemeManager.LoadThemes();
            var themeList = this.FindControl<ComboBox>("ThemeList");
            var overrideThemeList = this.FindControl<ComboBox>("OverrideThemeList");

            if (themeList != null) themeList.Items.Clear();
            if (overrideThemeList != null) overrideThemeList.Items.Clear();

            var themes = _settings.ThemeManager.GetAvailableThemes()
                .OrderBy(t => t)
                .Select(t => new ComboBoxItem { Content = t })
                .ToList();

            foreach (var t in themes)
            {
                if (themeList != null) themeList.Items.Add(t);
                if (overrideThemeList != null) overrideThemeList.Items.Add(new ComboBoxItem { Content = t.Content });
            }
        }

        internal static System.Collections.Generic.List<TerminalProfile> BuildLocalProfilesForEditor(System.Collections.Generic.IEnumerable<TerminalProfile> profiles)
        {
            var source = profiles ?? System.Array.Empty<TerminalProfile>();

            return source
                .Where(profile => profile.Type == ConnectionType.Local)
                .Select(profile => new TerminalProfile
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Command = profile.Command,
                    Arguments = profile.Arguments,
                    StartingDirectory = profile.StartingDirectory,
                    Type = ConnectionType.Local,
                    FontFamily = profile.FontFamily,
                    FontSize = profile.FontSize,
                    ThemeName = profile.ThemeName,
                    EnableLigatures = profile.EnableLigatures,
                    Group = profile.Group,
                    Notes = profile.Notes,
                    AccentColor = profile.AccentColor,
                    Tags = profile.Tags?.ToList() ?? new System.Collections.Generic.List<string>()
                })
                .ToList();
        }

        internal static System.Collections.Generic.List<TerminalProfile> NormalizeSettingsProfilesForSave(System.Collections.Generic.IEnumerable<TerminalProfile> profiles)
        {
            var source = profiles ?? System.Array.Empty<TerminalProfile>();

            return source
                .Where(profile => profile.Type == ConnectionType.Local)
                .Select(profile => new TerminalProfile
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Command = profile.Command,
                    Arguments = profile.Arguments,
                    StartingDirectory = profile.StartingDirectory,
                    Type = ConnectionType.Local,
                    FontFamily = profile.FontFamily,
                    FontSize = profile.FontSize,
                    ThemeName = profile.ThemeName,
                    EnableLigatures = profile.EnableLigatures,
                    Group = profile.Group,
                    Notes = profile.Notes,
                    AccentColor = profile.AccentColor,
                    Tags = profile.Tags?.ToList() ?? new System.Collections.Generic.List<string>()
                })
                .ToList();
        }

        internal static Guid ResolveDefaultLocalProfileId(Guid currentDefaultId, System.Collections.Generic.IReadOnlyList<TerminalProfile> localProfiles)
        {
            if (localProfiles == null || localProfiles.Count == 0)
            {
                return Guid.Empty;
            }

            return localProfiles.Any(profile => profile.Id == currentDefaultId)
                ? currentDefaultId
                : localProfiles[0].Id;
        }

        private void PopulateProfilesList()
        {
            var profilesListBox = this.FindControl<ListBox>("ProfilesListBox");
            if (profilesListBox == null) return;

            profilesListBox.Items.Clear();
            foreach (var profile in _profilesList)
            {
                // UI Polish: Show all profiles the user has configured.
                // Previously we hid "invalid" ones, but that hides imported WSL profiles if not found in path.
                // Let the user see and fix them if broken.

                var isDefault = profile.Id == _settings.DefaultProfileId;
                var displayName = profile.Name + (isDefault ? " (Default)" : "");
                var item = new ListBoxItem
                {
                    Content = displayName,
                    Tag = profile
                };
                profilesListBox.Items.Add(item);
            }
        }

        private void RefreshProfileListItem(TerminalProfile profile)
        {
            var profilesListBox = this.FindControl<ListBox>("ProfilesListBox");
            if (profilesListBox == null) return;

            foreach (ListBoxItem item in profilesListBox.Items.Cast<ListBoxItem>())
            {
                if (item.Tag == profile)
                {
                    var isDefault = profile.Id == _settings.DefaultProfileId;
                    item.Content = profile.Name + (isDefault ? " (Default)" : "");
                    break;
                }
            }
        }

        private void SwitchSelectedProfile(TerminalProfile profile)
        {
            _selectedProfile = profile;

            this.FindControl<TextBox>("ProfileNameInput")!.Text = profile.Name;
            this.FindControl<TextBox>("ProfileCommandInput")!.Text = profile.Command;
            this.FindControl<TextBox>("ProfileArgsInput")!.Text = profile.Arguments ?? "";
            this.FindControl<TextBox>("ProfileCwdInput")!.Text = profile.StartingDirectory ?? "";
            this.FindControl<TextBox>("ProfileGroupInput")!.Text = profile.Group ?? "General";
            this.FindControl<TextBox>("ProfileTagsInput")!.Text = string.Join(", ", profile.Tags ?? new System.Collections.Generic.List<string>());

            var typeList = this.FindControl<ComboBox>("ProfileTypeList");
            if (typeList != null) typeList.SelectedIndex = 0;

            var sshPanel = this.FindControl<StackPanel>("SshSettingsPanel");
            if (sshPanel != null) sshPanel.IsVisible = false;

            this.FindControl<TextBox>("SshHostInput")!.Text = profile.SshHost ?? "";
            this.FindControl<NumericUpDown>("SshPortInput")!.Value = profile.SshPort;
            this.FindControl<TextBox>("SshUserInput")!.Text = profile.SshUser ?? "";
            this.FindControl<TextBox>("SshKeyPathInput")!.Text = profile.SshKeyPath ?? "";
            this.FindControl<TextBox>("SshPasswordInput")!.Text = string.Empty;

            this.FindControl<CheckBox>("CheckOverrideFont")!.IsChecked = profile.FontFamily != null;
            this.FindControl<CheckBox>("CheckOverrideSize")!.IsChecked = profile.FontSize.HasValue;
            this.FindControl<CheckBox>("CheckOverrideTheme")!.IsChecked = profile.ThemeName != null;
            this.FindControl<CheckBox>("CheckOverrideLigatures")!.IsChecked = profile.EnableLigatures.HasValue;

            // Sync values to override inputs
            var overrideLigatureToggle = this.FindControl<CheckBox>("OverrideLigatureToggle");
            if (overrideLigatureToggle != null) overrideLigatureToggle.IsChecked = profile.EnableLigatures ?? _settings.EnableLigatures;
            var overrideFontList = this.FindControl<ComboBox>("OverrideFontList");
            if (overrideFontList != null)
            {
                var targetFont = profile.FontFamily ?? _settings.FontFamily;
                foreach (ComboBoxItem item in overrideFontList.Items.Cast<ComboBoxItem>())
                    if (item.Content?.ToString() == targetFont) { overrideFontList.SelectedItem = item; break; }
            }

            var overrideFontSize = this.FindControl<NumericUpDown>("OverrideFontSizeInput");
            if (overrideFontSize != null)
            {
                overrideFontSize.Value = (decimal)(profile.FontSize ?? _settings.FontSize);
            }

            var overrideThemeList = this.FindControl<ComboBox>("OverrideThemeList");
            if (overrideThemeList != null)
            {
                var targetTheme = profile.ThemeName ?? _settings.ThemeName;
                foreach (var obj in overrideThemeList.Items)
                    if (obj is ComboBoxItem item && item.Content?.ToString() == targetTheme) { overrideThemeList.SelectedItem = item; break; }
            }

            // SSH profile editing moved to Connection Manager.
        }

        private void PopulateJumpHostList(TerminalProfile current)
        {
            var combo = this.FindControl<ComboBox>("JumpHostList");
            if (combo == null) return;

            combo.Items.Clear();
            var noneItem = new ComboBoxItem { Content = "Direct Connection (None)" };
            combo.Items.Add(noneItem);
            combo.SelectedItem = noneItem;

            foreach (var p in _profilesList.Where(x => x.Type == ConnectionType.SSH && x.Id != current.Id))
            {
                var item = new ComboBoxItem { Content = p.Name, Tag = p.Id };
                combo.Items.Add(item);
                if (current.JumpHostProfileId == p.Id)
                {
                    combo.SelectedItem = item;
                }
            }
        }

        private void RefreshForwardsList()
        {
            var panel = this.FindControl<StackPanel>("ForwardsList");
            if (panel == null || _selectedProfile == null) return;
            panel.Children.Clear();
            foreach (var f in _selectedProfile.Forwards)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto"), Margin = new Thickness(0, 2) };

                // Status Indicator
                bool isListening = CheckIfPortIsListening(f);
                var dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = isListening ? Brushes.LimeGreen : Brushes.Gray,
                    Margin = new Thickness(5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(dot, isListening ? "Active / Listening" : "Inactive");

                var txt = new TextBlock { Text = f.ToString(), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                var btn = new Button
                {
                    Content = "×",
                    Classes = { "Danger" },
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    Tag = f
                };
                btn.Click += BtnRemoveForward_Click;

                Grid.SetColumn(dot, 0);
                Grid.SetColumn(txt, 1);
                Grid.SetColumn(btn, 2);

                grid.Children.Add(dot);
                grid.Children.Add(txt);
                grid.Children.Add(btn);
                panel.Children.Add(grid);
            }
        }

        private bool CheckIfPortIsListening(ForwardingRule rule)
        {
            try
            {
                // Dynamic (-D) or Local (-L) both listen on a local port
                if (rule.Type == ForwardingType.Remote) return false; // Remote forwards listen on the SERVER side

                string portStr = rule.LocalAddress;
                if (portStr.Contains(':')) portStr = portStr.Split(':').Last();
                if (!int.TryParse(portStr, out int port)) return false;

                var properties = IPGlobalProperties.GetIPGlobalProperties();
                var listeners = properties.GetActiveTcpListeners();
                return listeners.Any(l => l.Port == port);
            }
            catch { return false; }
        }

        private void BtnAddForward_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_selectedProfile == null) return;

            var typeBox = this.FindControl<ComboBox>("RuleInputType");
            var localBox = this.FindControl<TextBox>("RuleInputLocal");
            var remoteBox = this.FindControl<TextBox>("RuleInputRemote");

            if (string.IsNullOrWhiteSpace(localBox?.Text)) return;

            var rule = new ForwardingRule
            {
                Type = (ForwardingType)(typeBox?.SelectedIndex ?? 0),
                LocalAddress = localBox.Text.Trim(),
                RemoteAddress = remoteBox?.Text?.Trim() ?? ""
            };

            _selectedProfile.Forwards.Add(rule);
            RefreshForwardsList();

            // Clear inputs
            localBox.Text = "";
            if (remoteBox != null) remoteBox.Text = "";
        }

        private void BtnRemoveForward_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_selectedProfile == null || sender is not Button btn || btn.Tag is not ForwardingRule rule) return;
            _selectedProfile.Forwards.Remove(rule);
            RefreshForwardsList();
        }

        internal static IReadOnlyList<ShortcutCatalogEntry> FilterShortcutCatalogEntries(string query)
        {
            IEnumerable<ShortcutCatalogEntry> entries = ShortcutCatalog.GetEntries();
            if (!string.IsNullOrWhiteSpace(query))
            {
                string trimmedQuery = query.Trim();
                entries = entries.Where(entry =>
                    entry.Title.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                    entry.Category.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                    FormatScopeLabel(entry.Scope).Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                    entry.CommandId.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase));
            }

            return entries
                .OrderBy(entry => entry.Scope)
                .ThenBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void LoadTitleBarDraft()
        {
            // Resolve once and let TitleBarDraftState derive both the per-entry draft state and
            // the pinned order from that single result, rather than re-reading
            // _settings.TitleBarItems a second time with a separate lookup. The resolver is the
            // sole owner of state resolution -- including normalizing settings keys to
            // OrdinalIgnoreCase so a hand-edited settings.json entry like "Find" still matches the
            // catalog id "find" -- and a second, independent reader here would inevitably drift
            // from it and silently disagree about a case-variant id.
            var layout = TitleBarLayoutResolver.Resolve(
                _settings.TitleBarItems, _settings.TitleBarOrder, null);

            _titleBarDraft.SeedFrom(layout);
        }

        private void RebuildTitleBarRows()
        {
            var panel = this.FindControl<StackPanel>("TitleBarItemsPanel");
            if (panel == null)
            {
                return;
            }

            panel.Children.Clear();

            // Pinned entries first, in their configured order, so the ▲/▼ buttons act on a list
            // that reads top-to-bottom the way the bar reads left-to-right. Then the rest in
            // catalog order.
            var ordered = _titleBarDraft.GetDisplayOrder();

            var byId = TitleBarCatalog.GetEntries()
                .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < ordered.Count; i++)
            {
                panel.Children.Add(CreateTitleBarRow(byId[ordered[i]], i));
            }
        }

        /// <summary>
        /// Resolves one of the window's Nt* palette brushes (NtPanel, NtHairline, …). Rows built
        /// in code hold the brush INSTANCE, so ThemePaletteResources' in-place recolors keep them
        /// live across theme switches - the same mechanism the XAML StaticResource references use.
        /// Hardcoding hex here is what left the title-bar and shortcut cards dark navy under light
        /// themes (they never saw the palette at all).
        /// </summary>
        private IBrush? ThemeResourceBrush(string key) => this.FindResource(key) as IBrush;

        private Control CreateTitleBarRow(
            TitleBarCatalogEntry entry,
            int index)
        {
            var state = _titleBarDraft.GetState(entry.Id);
            bool isPinned = state == TitleBarItemState.Pinned;

            var row = new Border
            {
                Background = ThemeResourceBrush("NtPanel"),
                BorderBrush = ThemeResourceBrush("NtHairline"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
            };

            var icon = new PathIcon
            {
                Data = Geometry.Parse(entry.IconGeometry),
                Width = entry.IconSize,
                Height = entry.IconSize,
                VerticalAlignment = VerticalAlignment.Center,
            };

            string shortcut = TitleBarShortcuts.Resolve(entry.ShortcutKey, _shortcutDraftBindings);

            var labels = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = entry.Title },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(shortcut) ? "No shortcut" : shortcut,
                        Classes = { RowDescStyleClass },
                    },
                },
            };

            Control placement;
            if (entry.IsLocked)
            {
                // Locked: New Tab is the primary action and hosts the flyout with
                // "New SSH Connection…" / "Manage Profiles…" / "Agent Activity…". Letting it be
                // hidden would lose that flyout entirely.
                placement = new TextBlock
                {
                    Text = "Always pinned",
                    Classes = { RowDescStyleClass },
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else
            {
                var combo = new ComboBox
                {
                    MinWidth = 140,
                    VerticalAlignment = VerticalAlignment.Center,
                    ItemsSource = new[] { "Pinned", "Overflow", "Hidden" },
                    SelectedItem = state.ToString(),
                };

                // Assigning combo.SelectedItem below (to snap a rejected pick back) re-raises
                // SelectionChanged synchronously. Without this guard, the re-entrant call would see
                // the reverted (old) value as "picked", write it into draft state again, and
                // immediately clear the validation message this same handler just showed.
                bool suppressSelectionChanged = false;

                combo.SelectionChanged += (s, e) =>
                {
                    if (suppressSelectionChanged)
                    {
                        return;
                    }

                    if (combo.SelectedItem is not string picked ||
                        !Enum.TryParse(picked, out TitleBarItemState next))
                    {
                        return;
                    }

                    if (!_titleBarDraft.TrySetState(entry.Id, next))
                    {
                        // Explicit placement with no width-driven spill means nothing else stops a
                        // pinned set from running into the tab strip.
                        ShowTitleBarValidationMessage(
                            $"At most {TitleBarCatalog.MaxPinned} actions can be pinned. Move one to Overflow or Hidden first.");

                        suppressSelectionChanged = true;
                        combo.SelectedItem = _titleBarDraft.GetState(entry.Id).ToString();
                        suppressSelectionChanged = false;
                        return;
                    }

                    ClearTitleBarValidationMessage();
                    RebuildTitleBarRows();
                };

                placement = combo;
            }

            var up = new Button
            {
                Content = "▲",
                Classes = { "Pill" },
                IsEnabled = isPinned && !entry.IsLocked && index > 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            up.Click += (s, e) => MoveDraftPinned(entry.Id, -1);

            var down = new Button
            {
                Content = "▼",
                Classes = { "Pill" },
                IsEnabled = isPinned && !entry.IsLocked && index < _titleBarDraft.CountPinned() - 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            down.Click += (s, e) => MoveDraftPinned(entry.Id, +1);

            row.Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
                ColumnSpacing = 12,
                Children = { icon, labels, placement, up, down },
            };

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(labels, 1);
            Grid.SetColumn(placement, 2);
            Grid.SetColumn(up, 3);
            Grid.SetColumn(down, 4);

            return row;
        }

        private void MoveDraftPinned(string id, int delta)
        {
            _titleBarDraft.MovePinned(id, delta);
            RebuildTitleBarRows();
        }

        private void ShowTitleBarValidationMessage(string message)
        {
            var label = this.FindControl<TextBlock>("TitleBarValidationMessage");
            if (label == null) return;
            label.Text = message;
            label.IsVisible = true;
        }

        private void ClearTitleBarValidationMessage()
        {
            var label = this.FindControl<TextBlock>("TitleBarValidationMessage");
            if (label == null) return;
            label.IsVisible = false;
        }

        private void InitializeShortcutEditor()
        {
            var shortcutSearchInput = this.FindControl<TextBox>("ShortcutSearchInput");
            if (shortcutSearchInput != null)
            {
                shortcutSearchInput.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == "Text")
                    {
                        PopulateShortcutBindingsPanel(shortcutSearchInput.Text ?? "");
                    }
                };
            }

            PopulateShortcutBindingsPanel(shortcutSearchInput?.Text ?? "");
        }

        private void PopulateShortcutBindingsPanel(string query)
        {
            var panel = this.FindControl<StackPanel>("ShortcutBindingsPanel");
            if (panel == null)
            {
                return;
            }

            panel.Children.Clear();
            string? activeScope = null;
            IReadOnlyList<ShortcutCatalogEntry> entries = FilterShortcutCatalogEntries(query);
            foreach (ShortcutCatalogEntry entry in entries)
            {
                string scopeLabel = FormatScopeLabel(entry.Scope);
                if (!string.Equals(activeScope, scopeLabel, StringComparison.Ordinal))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = scopeLabel.ToUpperInvariant(),
                        Classes = { "SectionHeader" },
                        Margin = new Thickness(0, activeScope == null ? 0 : 12, 0, 8),
                    });
                    activeScope = scopeLabel;
                }

                panel.Children.Add(CreateShortcutBindingRow(entry));
            }

            if (panel.Children.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No shortcuts match the current filter.",
                    Classes = { RowDescStyleClass },
                });
            }
        }

        private Control CreateShortcutBindingRow(ShortcutCatalogEntry entry)
        {
            string effectiveBinding = GetEffectiveShortcutBinding(entry);
            var row = new Border
            {
                Background = ThemeResourceBrush("NtPanel"),
                BorderBrush = ThemeResourceBrush("NtHairline"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
            };

            var errorText = new TextBlock
            {
                Foreground = Brushes.IndianRed,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = false,
            };

            var bindingEditor = new TextBox
            {
                Text = effectiveBinding,
                IsReadOnly = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "Press shortcut",
                MinWidth = 180,
            };

            bindingEditor.KeyDown += (s, e) => HandleShortcutEditorKeyDown(entry, bindingEditor, errorText, e);

            var resetButton = new Button
            {
                Content = "Reset",
                Classes = { "Pill" },
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            resetButton.Click += (s, e) =>
            {
                _shortcutDraftBindings.Remove(entry.CommandId);
                bindingEditor.Text = entry.DefaultBinding;
                errorText.IsVisible = false;
                ClearShortcutValidationMessage();
                // The title bar rows (Appearance tab) show the same effective shortcut text and
                // read straight from _shortcutDraftBindings; without this they'd keep showing the
                // binding that was just reset until the window is closed and reopened.
                RebuildTitleBarRows();
            };

            row.Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,220,Auto"),
                        ColumnSpacing = 12,
                        Children =
                        {
                            new StackPanel
                            {
                                Spacing = 2,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = entry.Title,
                                        Classes = { "RowLabel" },
                                    },
                                    new TextBlock
                                    {
                                        Text = $"{entry.Category} · {FormatScopeLabel(entry.Scope)} · Default {entry.DefaultBinding}",
                                        Classes = { RowDescStyleClass },
                                    },
                                },
                            },
                            bindingEditor,
                            resetButton,
                        }
                    },
                    errorText,
                }
            };

            Grid.SetColumn(bindingEditor, 1);
            Grid.SetColumn(resetButton, 2);
            return row;
        }

        private void HandleShortcutEditorKeyDown(
            ShortcutCatalogEntry entry,
            TextBox bindingEditor,
            TextBlock errorText,
            KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                bindingEditor.Text = GetEffectiveShortcutBinding(entry);
                errorText.IsVisible = false;
                ClearShortcutValidationMessage();
                e.Handled = true;
                return;
            }

            if (IsModifierKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            string binding = ShortcutMatcher.Normalize(e);
            Dictionary<string, string> candidateBindings = new(_shortcutDraftBindings, StringComparer.OrdinalIgnoreCase);
            if (string.Equals(binding, entry.DefaultBinding, StringComparison.Ordinal))
            {
                candidateBindings.Remove(entry.CommandId);
            }
            else
            {
                candidateBindings[entry.CommandId] = binding;
            }

            ShortcutBindingResolution resolution = ShortcutBindingResolver.Resolve(ShortcutCatalog.GetDefinitions(), candidateBindings);
            ShortcutBindingConflict? conflict = resolution.Conflicts.FirstOrDefault(item =>
                item.Bindings.Any(bindingRecord => string.Equals(bindingRecord.CommandId, entry.CommandId, StringComparison.OrdinalIgnoreCase)));

            if (conflict != null)
            {
                string conflictOwner = conflict.Bindings
                    .Select(bindingRecord => ShortcutCatalog.GetEntries().First(catalogEntry => catalogEntry.CommandId == bindingRecord.CommandId).Title)
                    .First(title => !string.Equals(title, entry.Title, StringComparison.OrdinalIgnoreCase));
                errorText.Text = $"{binding} is already assigned to {conflictOwner}.";
                errorText.IsVisible = true;
                ShowShortcutValidationMessage("Resolve duplicate shortcuts before saving.");
                e.Handled = true;
                return;
            }

            _shortcutDraftBindings = candidateBindings;
            bindingEditor.Text = binding;
            errorText.IsVisible = false;
            ClearShortcutValidationMessage();
            e.Handled = true;

            // Keep the Appearance tab's title bar rows in sync: they render the same effective
            // shortcut (TitleBarShortcuts.Resolve over _shortcutDraftBindings) but only did so at
            // RebuildTitleBarRows's last call, so without this refresh they'd show the old binding
            // until Settings is closed and reopened even though Save will persist the new one.
            // This rebuilds a different panel (TitleBarItemsPanel, not ShortcutBindingsPanel) than
            // the one bindingEditor lives in, so it does not touch bindingEditor's focus or
            // re-enter this handler.
            RebuildTitleBarRows();
        }

        private static bool IsModifierKey(Key key)
        {
            return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift;
        }

        private string GetEffectiveShortcutBinding(ShortcutCatalogEntry entry)
        {
            return _shortcutDraftBindings.TryGetValue(entry.CommandId, out string? binding) &&
                   !string.IsNullOrWhiteSpace(binding)
                ? binding
                : entry.DefaultBinding;
        }

        private static string FormatScopeLabel(ShortcutScope scope)
        {
            return scope switch
            {
                ShortcutScope.App => "Application",
                ShortcutScope.Pane => "Pane",
                ShortcutScope.CommandAssist => "Command Assist",
                _ => scope.ToString(),
            };
        }

        private void ShowShortcutValidationMessage(string message)
        {
            var validationMessage = this.FindControl<TextBlock>("ShortcutValidationMessage");
            if (validationMessage == null)
            {
                return;
            }

            validationMessage.Text = message;
            validationMessage.IsVisible = true;
        }

        private void ClearShortcutValidationMessage()
        {
            var validationMessage = this.FindControl<TextBlock>("ShortcutValidationMessage");
            if (validationMessage == null)
            {
                return;
            }

            validationMessage.Text = string.Empty;
            validationMessage.IsVisible = false;
        }


        /// <summary>
        /// The "Remote shell integration" row (V2 Phase 2b): pick a remote shell, put that snippet on
        /// the clipboard, and show what to do with it.
        /// </summary>
        /// <remarks>
        /// No setting is read or written here - this row is an action, not a preference. Whether Nova
        /// consumes remote marks at all is governed by the existing shell-integration setting, and
        /// whether the remote host emits them is governed by whether the user installed the snippet.
        /// Neither is something this row can toggle.
        /// </remarks>
        private void WireRemoteShellIntegrationRow()
        {
            var shellList = this.FindControl<ComboBox>("RemoteShellIntegrationShellList");
            var copyInstallerButton = this.FindControl<Button>("BtnCopyRemoteShellIntegration");
            var copySnippetButton = this.FindControl<Button>("BtnCopyRemoteShellIntegrationSnippet");
            var status = this.FindControl<TextBlock>("RemoteShellIntegrationStatus");
            if (shellList == null || copyInstallerButton == null)
            {
                return;
            }

            shellList.ItemsSource = RemoteShellIntegrationSnippets.All
                .Select(RemoteShellIntegrationSnippets.GetDisplayName)
                .ToList();
            shellList.SelectedIndex = 0;

            RemoteShellIntegrationShell SelectedShell()
            {
                int index = Math.Clamp(
                    shellList.SelectedIndex,
                    0,
                    RemoteShellIntegrationSnippets.All.Count - 1);
                return RemoteShellIntegrationSnippets.All[index];
            }

            copyInstallerButton.Click += async (_, _) =>
                await CopyRemoteShellIntegrationInstallerAsync(SelectedShell(), status);

            if (copySnippetButton != null)
            {
                copySnippetButton.Click += async (_, _) =>
                    await CopyRemoteShellIntegrationSnippetAsync(SelectedShell(), status);
            }
        }

        /// <summary>
        /// The primary action: one line the user pastes at the remote prompt.
        /// </summary>
        /// <remarks>
        /// The status text describes what the paste does rather than what the user must do next:
        /// the installer writes the snippet and patches the rc file itself, so there is no next
        /// step to describe. It deliberately does not promise the current session becomes
        /// integrated: the installer runs as a child process and never touches the live shell, so
        /// marks arrive with the next session.
        /// </remarks>
        private async System.Threading.Tasks.Task CopyRemoteShellIntegrationInstallerAsync(
            RemoteShellIntegrationShell shell,
            TextBlock? status)
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null)
                {
                    ShowRemoteShellIntegrationStatus(status, "Clipboard is not available.");
                    return;
                }

                await clipboard.SetTextAsync(RemoteShellIntegrationSnippets.BuildInstallerCommand(shell));

                string? loaderLine = RemoteShellIntegrationSnippets.GetLoaderLine(shell);
                string writeDescription = loaderLine != null
                    ? $"It writes {RemoteShellIntegrationSnippets.GetRemotePath(shell)} and adds the loader " +
                      "line to your rc file if it isn't already there."
                    : $"It writes {RemoteShellIntegrationSnippets.GetRemotePath(shell)}, which is sourced " +
                      "automatically, so there is nothing else to add.";

                ShowRemoteShellIntegrationStatus(
                    status,
                    $"Copied the installer for {RemoteShellIntegrationSnippets.GetDisplayName(shell)}. " +
                    "Paste it at the remote prompt and press Enter - one line, one history entry. " +
                    writeDescription);
            }
            catch (Exception ex)
            {
                // Reported in the row rather than swallowed: the whole point of the affordance is
                // that the user now has the installer, and silently not having it looks identical.
                ShowRemoteShellIntegrationStatus(status, $"Could not copy the installer: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task CopyRemoteShellIntegrationSnippetAsync(
            RemoteShellIntegrationShell shell,
            TextBlock? status)
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null)
                {
                    ShowRemoteShellIntegrationStatus(status, "Clipboard is not available.");
                    return;
                }

                await clipboard.SetTextAsync(RemoteShellIntegrationSnippets.Read(shell));
                ShowRemoteShellIntegrationStatus(
                    status,
                    RemoteShellIntegrationSnippets.BuildInstallInstructions(shell));
            }
            catch (Exception ex)
            {
                // Reported in the row rather than swallowed: the whole point of the affordance is
                // that the user now has the snippet, and silently not having it looks identical.
                ShowRemoteShellIntegrationStatus(status, $"Could not copy the snippet: {ex.Message}");
            }
        }

        /// <summary>
        /// The "Clear history" button (V2 Phase 3b task 5): <see cref="IHistoryStore.ClearAsync"/>
        /// finally gets a caller.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two clicks, not a modal. The first click arms and relabels the button, the second one clears;
        /// clicking anything else, or reopening the window, forgets. A destructive action a user cannot
        /// undo needs a confirmation, and an <c>await</c>-ed dialog here would be the only modal in this
        /// window - the arm-then-confirm pattern is cheaper to reason about and impossible to
        /// double-click through.
        /// </para>
        /// <para>
        /// Goes through the <em>live</em> store rather than constructing one over the same file.
        /// <c>JsonlHistoryStore</c> caches an index and a physical line count and compacts from that
        /// cache, so a second instance would clear the file while the pane's instance carried on
        /// appending against a stale view - see <c>CommandAssistServices.ApplyHistoryRetentionLimit</c>
        /// for the same argument about the retention cap. <c>MainWindow</c> injects the instance it hands
        /// to panes; with no injection the row reports that instead of pretending.
        /// </para>
        /// </remarks>
        private void WireClearCommandAssistHistoryRow()
        {
            var clearButton = this.FindControl<Button>("BtnClearCommandAssistHistory");
            var status = this.FindControl<TextBlock>("CommandAssistHistoryStatus");
            if (clearButton == null)
            {
                return;
            }

            clearButton.Click += async (_, _) =>
            {
                if (CommandAssistHistoryStore == null)
                {
                    ShowRemoteShellIntegrationStatus(status, "Command history is not available in this window.");
                    return;
                }

                if (!_isClearCommandAssistHistoryArmed)
                {
                    _isClearCommandAssistHistoryArmed = true;
                    clearButton.Content = "Confirm clear";
                    ShowRemoteShellIntegrationStatus(
                        status,
                        "This deletes every recorded command, including history carried over from an older version. Click again to confirm.");
                    return;
                }

                _isClearCommandAssistHistoryArmed = false;
                clearButton.Content = "Clear history";

                try
                {
                    await CommandAssistHistoryStore.ClearAsync();

                    // Any assist surface still on screen is showing rows that no longer exist (PR #293
                    // review, non-blocking 7). The host owns the panes, so it is told rather than
                    // reached for from here; with no host wired the clear still reports success, which is
                    // true - only the refresh is missing.
                    OnCommandAssistHistoryCleared?.Invoke();
                    ShowRemoteShellIntegrationStatus(status, "Command history cleared.");
                }
                catch (Exception ex)
                {
                    // Reported rather than swallowed, for the same reason as the snippet copy: a clear
                    // that silently failed looks exactly like one that worked.
                    ShowRemoteShellIntegrationStatus(status, $"Could not clear history: {ex.Message}");
                }
            };
        }

        /// <summary>
        /// The snippet manager (V2 Phase 4b, Phase 4 task 4): list, edit in place, delete, add.
        /// <see cref="ISnippetStore.RemoveAsync"/> finally gets a caller.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Handlers are attached during construction; the list is populated on <c>Opened</c>, because
        /// the store arrives by property assignment after the constructor has run - the same
        /// injection shape as the history store, and the same reason it is safe: the constructor only
        /// wires, it does not read.
        /// </para>
        /// <para>
        /// <strong>Edits commit on focus loss, not on a Save button.</strong> The window's Save
        /// button writes <c>settings.json</c>, and snippets do not live there - they are their own
        /// store, written by panes at any moment. Routing them through Save would mean either
        /// pretending they are settings or growing a second Save; committing per field keeps the file
        /// and the boxes in step and matches the shortcut editor immediately above, which also has no
        /// per-row confirm.
        /// </para>
        /// </remarks>
        private void WireCommandAssistSnippetsRow()
        {
            var addButton = this.FindControl<Button>("BtnAddCommandAssistSnippet");
            var status = this.FindControl<TextBlock>("CommandAssistSnippetStatus");
            if (addButton == null)
            {
                return;
            }

            Opened += async (_, _) => await ReloadCommandAssistSnippetsAsync();

            addButton.Click += async (_, _) =>
            {
                SnippetEditor? editor = TryGetSnippetEditor(status);
                if (editor == null)
                {
                    return;
                }

                try
                {
                    // Created blank and immediately focused, rather than opening a dialog: the row
                    // the user is about to fill in is the same row they will later edit, so there is
                    // no second form to learn. The command placeholder text carries the requirement
                    // that an empty command is not saved.
                    CommandSnippet? created = await editor.AddAsync("New snippet", "# replace with your command");
                    if (created == null)
                    {
                        return;
                    }

                    PopulateCommandAssistSnippetsPanel();
                    OnCommandAssistSnippetsChanged?.Invoke();
                    ShowRemoteShellIntegrationStatus(status, "Snippet added. Give it a name and a command.");
                }
                catch (Exception ex)
                {
                    ShowRemoteShellIntegrationStatus(status, $"Could not add the snippet: {ex.Message}");
                }
            };
        }

        private async System.Threading.Tasks.Task ReloadCommandAssistSnippetsAsync()
        {
            var status = this.FindControl<TextBlock>("CommandAssistSnippetStatus");
            SnippetEditor? editor = TryGetSnippetEditor(status: null);
            if (editor == null)
            {
                PopulateCommandAssistSnippetsPanel();
                return;
            }

            try
            {
                await editor.LoadAsync();
            }
            catch (Exception ex)
            {
                ShowRemoteShellIntegrationStatus(status, $"Could not read your snippets: {ex.Message}");
            }

            PopulateCommandAssistSnippetsPanel();
        }

        private SnippetEditor? TryGetSnippetEditor(TextBlock? status)
        {
            if (CommandAssistSnippetStore == null)
            {
                ShowRemoteShellIntegrationStatus(status, "Snippets are not available in this window.");
                return null;
            }

            return _snippetEditor ??= new SnippetEditor(CommandAssistSnippetStore);
        }

        private void PopulateCommandAssistSnippetsPanel()
        {
            var panel = this.FindControl<StackPanel>("CommandAssistSnippetsPanel");
            if (panel == null)
            {
                return;
            }

            panel.Children.Clear();

            IReadOnlyList<CommandSnippet> snippets = _snippetEditor?.Snippets ?? Array.Empty<CommandSnippet>();
            if (snippets.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = CommandAssistSnippetStore == null
                        ? "Snippets are not available in this window."
                        : "No snippets yet. Pin a suggestion with Ctrl+Shift+S, or add one here.",
                    Classes = { RowDescStyleClass },
                });
                return;
            }

            foreach (CommandSnippet snippet in snippets)
            {
                panel.Children.Add(CreateCommandAssistSnippetRow(snippet));
            }
        }

        private Control CreateCommandAssistSnippetRow(CommandSnippet snippet)
        {
            var status = this.FindControl<TextBlock>("CommandAssistSnippetStatus");

            // The row's idea of what is currently saved. Updated in place after every successful
            // commit, because the row outlives the edit: without it a second edit would compare
            // against the values the row was built with and the blank-command restore below would put
            // back a command the user replaced two edits ago.
            CommandSnippet current = snippet;

            var nameEditor = new TextBox
            {
                Text = snippet.Name,
                PlaceholderText = "Name",
                MinWidth = 160,
            };

            var commandEditor = new TextBox
            {
                Text = snippet.CommandText,
                PlaceholderText = "Command (required)",
                FontFamily = new FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // Commit on focus loss rather than on every keystroke: a per-keystroke write would put a
            // file rewrite behind every character, and the store re-sorts on write, so the list would
            // reorder under the caret mid-word.
            //
            // The panel is deliberately NOT rebuilt afterwards. Focus loss is usually focus moving to
            // something else in the same row - most often the Delete button - and destroying the row
            // out from under an in-flight click means the click never lands. The boxes already show
            // what was saved; only the sort position can be stale, and it settles on the next open.
            async void Commit()
            {
                CommandSnippet? saved = await CommitCommandAssistSnippetAsync(current, nameEditor, commandEditor, status);
                if (saved != null)
                {
                    current = saved;
                }
            }

            nameEditor.LostFocus += (_, _) => Commit();
            commandEditor.LostFocus += (_, _) => Commit();

            var deleteButton = new Button
            {
                Content = "Delete",
                Classes = { "Pill" },
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            deleteButton.Click += async (_, _) =>
            {
                // No arm/confirm here, unlike Clear history: one snippet is a row the user can
                // retype, and the two-click pattern earns its friction against "every command you
                // have ever run", not against a single line the user just looked at.
                SnippetEditor? editor = TryGetSnippetEditor(status);
                if (editor == null)
                {
                    return;
                }

                try
                {
                    if (await editor.RemoveAsync(current.Id))
                    {
                        PopulateCommandAssistSnippetsPanel();
                        OnCommandAssistSnippetsChanged?.Invoke();
                        ShowRemoteShellIntegrationStatus(status, $"Deleted \"{current.Name}\".");
                    }
                }
                catch (Exception ex)
                {
                    ShowRemoteShellIntegrationStatus(status, $"Could not delete the snippet: {ex.Message}");
                }
            };

            // Columns are assigned explicitly. Avalonia defaults Grid.Column to 0 for every child, so
            // a Children initializer alone stacks all three controls on top of each other in the
            // first column - which is exactly what the first build of this row did.
            Grid.SetColumn(nameEditor, 0);
            Grid.SetColumn(commandEditor, 1);
            Grid.SetColumn(deleteButton, 2);

            return new Border
            {
                Background = ThemeResourceBrush("NtPanel"),
                BorderBrush = ThemeResourceBrush("NtHairline"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("220,*,Auto"),
                    ColumnSpacing = 10,
                    Children = { nameEditor, commandEditor, deleteButton },
                },
            };
        }

        /// <summary>
        /// Writes one row's edits, returning the saved snippet or <see langword="null"/> when nothing
        /// was written (unchanged, refused, or no store).
        /// </summary>
        private async System.Threading.Tasks.Task<CommandSnippet?> CommitCommandAssistSnippetAsync(
            CommandSnippet snippet,
            TextBox nameEditor,
            TextBox commandEditor,
            TextBlock? status)
        {
            SnippetEditor? editor = _snippetEditor;
            if (editor == null)
            {
                return null;
            }

            string name = nameEditor.Text ?? string.Empty;
            string command = commandEditor.Text ?? string.Empty;
            if (string.Equals(name, snippet.Name, StringComparison.Ordinal) &&
                string.Equals(command, snippet.CommandText, StringComparison.Ordinal))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                // Refused rather than saved-as-empty, and the box is put back, so the user can see
                // which snippet they were about to blank. Deleting is what the Delete button is for.
                commandEditor.Text = snippet.CommandText;
                ShowRemoteShellIntegrationStatus(status, "A snippet needs a command. Use Delete to remove it.");
                return null;
            }

            try
            {
                if (!await editor.UpdateAsync(snippet.Id, name, command))
                {
                    return null;
                }

                OnCommandAssistSnippetsChanged?.Invoke();
                ShowRemoteShellIntegrationStatus(status, "Snippet saved.");

                CommandSnippet? saved = editor.Snippets
                    .FirstOrDefault(x => string.Equals(x.Id, snippet.Id, StringComparison.Ordinal));

                // The name the editor derived may differ from what was typed (a blank name becomes
                // the command's first line), so the box is reconciled with what was actually stored.
                if (saved != null)
                {
                    nameEditor.Text = saved.Name;
                }

                return saved;
            }
            catch (Exception ex)
            {
                ShowRemoteShellIntegrationStatus(status, $"Could not save the snippet: {ex.Message}");
                return null;
            }
        }

        private static void ShowRemoteShellIntegrationStatus(TextBlock? status, string message)
        {
            if (status == null)
            {
                return;
            }

            status.Text = message;
            status.IsVisible = true;
        }

        private void LoadCurrentSettings()
        {
            var fontList = this.FindControl<ComboBox>("FontList");
            var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");
            var scrollbackInput = this.FindControl<NumericUpDown>("ScrollbackInput");
            var themeList = this.FindControl<ComboBox>("ThemeList");
            var opacitySlider = this.FindControl<Slider>("WindowOpacitySlider");
            var opacityDisplay = this.FindControl<TextBlock>("OpacityValueDisplay");
            var ligatureToggle = this.FindControl<CheckBox>("LigatureToggle");

            // Bg Image Controls
            var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
            var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
            var bgOpacityDisplay = this.FindControl<TextBlock>("BgImageOpacityDisplay");
            var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");
            var complexShapingToggle = this.FindControl<CheckBox>("ComplexShapingToggle");
            var commandAssistToggle = this.FindControl<CheckBox>("CommandAssistToggle");
            var agentAccessObserveToggle = this.FindControl<CheckBox>("AgentAccessObserveToggle");

            var nativeSshToggle = this.FindControl<CheckBox>("NativeSshToggle");
            if (nativeSshToggle != null) nativeSshToggle.IsChecked = _settings.ExperimentalNativeSshEnabled;
            if (agentAccessObserveToggle != null) agentAccessObserveToggle.IsChecked = _settings.AgentAccessObserveEnabled;
            var agentReplayExportToggle = this.FindControl<CheckBox>("AgentReplayExportToggle");
            if (agentReplayExportToggle != null) agentReplayExportToggle.IsChecked = _settings.AgentReplayExportEnabled;
            var agentScreenshotToggle = this.FindControl<CheckBox>("AgentScreenshotToggle");
            if (agentScreenshotToggle != null) agentScreenshotToggle.IsChecked = _settings.AgentScreenshotEnabled;
            var agentAccessActToggle = this.FindControl<CheckBox>("AgentAccessActToggle");
            if (agentAccessActToggle != null) agentAccessActToggle.IsChecked = _settings.AgentAccessActEnabled;
            var agentIndicatorTabRollupList = this.FindControl<ComboBox>("AgentIndicatorTabRollupList");
            if (agentIndicatorTabRollupList != null)
            {
                agentIndicatorTabRollupList.SelectedItem = agentIndicatorTabRollupList.Items
                    .Cast<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Content?.ToString(), _settings.AgentIndicatorTabRollup, StringComparison.Ordinal));
                if (agentIndicatorTabRollupList.SelectedItem == null) agentIndicatorTabRollupList.SelectedIndex = 0;
            }
            var longCommandNotificationsToggle = this.FindControl<CheckBox>("LongCommandNotificationsToggle");
            if (longCommandNotificationsToggle != null) longCommandNotificationsToggle.IsChecked = _settings.LongCommandNotificationsEnabled;
            var automaticUpdateChecksToggle = this.FindControl<CheckBox>("AutomaticUpdateChecksToggle");
            if (automaticUpdateChecksToggle != null) automaticUpdateChecksToggle.IsChecked = _settings.AutomaticUpdateChecks;
            if (fontSizeInput != null) fontSizeInput.Value = (decimal)_settings.FontSize;
            if (scrollbackInput != null) scrollbackInput.Value = (decimal)_settings.MaxHistory;
            if (opacitySlider != null)
            {
                opacitySlider.Value = _settings.WindowOpacity;
                if (opacityDisplay != null)
                    opacityDisplay.Text = $"{(int)(_settings.WindowOpacity * 100)}%";
            }

            if (ligatureToggle != null) ligatureToggle.IsChecked = _settings.EnableLigatures;
            if (complexShapingToggle != null) complexShapingToggle.IsChecked = _settings.EnableComplexShaping;
            if (commandAssistToggle != null) commandAssistToggle.IsChecked = _settings.CommandAssistEnabled;

            // The Command Assist group (V2 Phase 3b task 5). Three sub-rows under the master toggle,
            // following the Agent access group's indented-row convention.
            var commandAssistPassiveBubbleToggle = this.FindControl<CheckBox>("CommandAssistPassiveBubbleToggle");
            if (commandAssistPassiveBubbleToggle != null) commandAssistPassiveBubbleToggle.IsChecked = _settings.CommandAssistPassiveBubbleEnabled;
            var commandAssistHistoryToggle = this.FindControl<CheckBox>("CommandAssistHistoryToggle");
            if (commandAssistHistoryToggle != null) commandAssistHistoryToggle.IsChecked = _settings.CommandAssistHistoryEnabled;
            var commandAssistShellIntegrationToggle = this.FindControl<CheckBox>("CommandAssistShellIntegrationToggle");
            if (commandAssistShellIntegrationToggle != null) commandAssistShellIntegrationToggle.IsChecked = _settings.CommandAssistShellIntegrationEnabled;

            if (bgPathInput != null) bgPathInput.Text = _settings.BackgroundImagePath;
            if (bgOpacitySlider != null)
            {
                bgOpacitySlider.Value = _settings.BackgroundImageOpacity;
                if (bgOpacityDisplay != null)
                    bgOpacityDisplay.Text = $"{(int)(_settings.BackgroundImageOpacity * 100)}%";
            }

            // ... (font/theme loops omitted for brevity as they are unchanged logic, but we need to keep them if we replace the whole block) ... 
            // Wait, I am replacing a chunk. I should keep the existing logic.
            // Re-implementing existing loops to be safe:

            if (fontList != null)
            {
                foreach (ComboBoxItem item in fontList.Items.Cast<ComboBoxItem>())
                {
                    if (item.Content?.ToString() == _settings.FontFamily)
                    {
                        fontList.SelectedItem = item;
                        break;
                    }
                }
            }

            if (themeList != null)
            {
                // Legacy settings store "Default (Dark)" while the combo lists the manager's
                // canonical "Default" (ThemeManager.GetTheme maps between them). Normalize that
                // one alias rather than resolving through GetTheme: for a name GetTheme cannot
                // find it falls back to the Default theme OBJECT, whose .Name would match the
                // "Default" combo item and silently rewrite the stored name on Save. When nothing
                // matches here the selection stays empty and SaveAndClose skips the theme field,
                // preserving the stored name - that must keep holding for unresolvable names.
                var themeName = _settings.ThemeName == "Default (Dark)" ? "Default" : _settings.ThemeName;
                foreach (ComboBoxItem item in themeList.Items.Cast<ComboBoxItem>())
                {
                    if (item.Content?.ToString() == themeName)
                    {
                        themeList.SelectedItem = item;
                        break;
                    }
                }
            }

            var blurList = this.FindControl<ComboBox>("BlurList");
            if (blurList != null)
            {
                foreach (ComboBoxItem item in blurList.Items.Cast<ComboBoxItem>())
                {
                    if (item.Content?.ToString() == _settings.BlurEffect)
                    {
                        blurList.SelectedItem = item;
                        break;
                    }
                }
                if (blurList.SelectedItem == null && blurList.ItemCount > 0) blurList.SelectedIndex = 0;
            }

            var tabOrientationList = this.FindControl<ComboBox>("TabOrientationList");
            if (tabOrientationList != null)
            {
                foreach (ComboBoxItem item in tabOrientationList.Items.Cast<ComboBoxItem>())
                {
                    if (string.Equals(item.Content?.ToString(), _settings.TabStripOrientation, StringComparison.OrdinalIgnoreCase))
                    {
                        tabOrientationList.SelectedItem = item;
                        break;
                    }
                }
                if (tabOrientationList.SelectedItem == null && tabOrientationList.ItemCount > 0) tabOrientationList.SelectedIndex = 0;
            }

            if (bgStretchList != null)
            {
                foreach (ComboBoxItem item in bgStretchList.Items.Cast<ComboBoxItem>())
                {
                    if (item.Content?.ToString() == _settings.BackgroundImageStretch)
                    {
                        bgStretchList.SelectedItem = item;
                        break;
                    }
                }
                if (bgStretchList.SelectedItem == null && bgStretchList.ItemCount > 0) bgStretchList.SelectedIndex = 3; // UniformToFill
            }
        }

        private void UpdateThemePreview(TerminalTheme theme, string context)
        {
            // Update the single preview area we have regardless of whether it's the global theme 
            // or a profile override being touched.
            var sampleBorder = this.FindControl<Border>("SampleTextBorder");
            var sampleText = this.FindControl<TextBlock>("SampleTextBlock");
            var previewArea = this.FindControl<Border>("ThemePreviewArea");

            if (sampleBorder != null) sampleBorder.Background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
            if (sampleText != null) sampleText.Foreground = new SolidColorBrush(theme.Foreground.ToAvaloniaColor());

            // Also update the container background to match the theme (so it doesn't look like a black box in a light theme)
            // But lets make it slightly different so we can distinguish the "terminal area"
            if (previewArea != null)
            {
                previewArea.Background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
                // Ensure the preview label is visible against this background
                var children = (previewArea.Child as StackPanel)?.Children;
                if (children != null && children.Count > 0 && children[0] is TextBlock label)
                {
                    // Calculate contrast for the label
                    double lum = (0.299 * theme.Background.R + 0.587 * theme.Background.G + 0.114 * theme.Background.B) / 255.0;
                    label.Foreground = lum > 0.5 ? Brushes.Black : Brushes.White;
                }
            }

            for (int i = 0; i < 16; i++)
            {
                var swatch = this.FindControl<Border>($"Swatch{i}");
                if (swatch != null)
                {
                    swatch.Background = new SolidColorBrush(theme.GetAnsiColor(i % 8, i >= 8).ToAvaloniaColor());
                }
            }
        }

        private void OpenThemeEditor(TerminalTheme theme)
        {
            _editingTheme = theme;
            var panel = this.FindControl<Border>("ThemeEditorPanel");
            var nameInput = this.FindControl<TextBox>("EditThemeNameInput");
            var fgInput = this.FindControl<TextBox>("EditThemeFgInput");
            var bgInput = this.FindControl<TextBox>("EditThemeBgInput");
            var cursorInput = this.FindControl<TextBox>("EditThemeCursorInput");
            var btnDelete = this.FindControl<Button>("BtnDeleteTheme");

            if (panel != null) panel.IsVisible = true;
            if (nameInput != null) nameInput.Text = theme.Name;
            if (fgInput != null) fgInput.Text = theme.Foreground.ToString();
            if (bgInput != null) bgInput.Text = theme.Background.ToString();
            if (cursorInput != null) cursorInput.Text = theme.CursorColor.ToString();

            if (btnDelete != null) btnDelete.IsEnabled = theme.Name != "Default";

            UpdateEditorSwatches();
            UpdateThemePreview(theme, "Editor");
        }

        private void UpdateEditorSwatches()
        {
            if (_editingTheme == null) return;
            for (int i = 0; i < 16; i++)
            {
                var btn = this.FindControl<Button>($"EditSwatch{i}");
                if (btn != null)
                {
                    btn.Background = new SolidColorBrush(_editingTheme.GetAnsiColor(i % 8, i >= 8).ToAvaloniaColor());
                }
            }
        }

        private void OpenSwatchFlyout(Button target, int index)
        {
            if (_editingTheme == null) return;

            var current = _editingTheme.GetAnsiColor(index % 8, index >= 8);
            var hexInput = new TextBox { Text = current.ToString(), Width = 100 };
            var preview = new Border { Width = 30, Height = 30, Background = new SolidColorBrush(current.ToAvaloniaColor()), CornerRadius = new CornerRadius(4), Margin = new Thickness(5, 0, 0, 0) };

            hexInput.TextChanged += (s, e) =>
            {
                if (Color.TryParse(hexInput.Text, out var color))
                {
                    preview.Background = new SolidColorBrush(color);
                    _editingTheme.SetAnsiColor(index % 8, index >= 8, TermColorHelper.FromAvaloniaColor(color));
                    target.Background = new SolidColorBrush(color);
                    UpdateThemePreview(_editingTheme, "Editor");
                }
            };

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(10),
                Children = { hexInput, preview }
            };

            var flyout = new Flyout { Content = content };
            flyout.ShowAt(target);
        }

        private void ApplyTheme(TerminalTheme? theme = null)
        {
            if (theme == null) theme = _settings.ActiveTheme;

            var contrastColor = theme.GetContrastForeground();
            var contrastForeground = new SolidColorBrush(contrastColor.ToAvaloniaColor());
            UpdatePaletteResources(theme);

            this.Background = new Avalonia.Media.SolidColorBrush(theme.Background.ToAvaloniaColor());
            this.Foreground = contrastForeground;

            // Set the window theme variant so standard controls (ComboBox, ScrollBar, etc.) adapt
            this.RequestedThemeVariant = contrastColor == TermColor.Black ? ThemeVariant.Light : ThemeVariant.Dark;

            // Ensure Profile Editor Panel stays readable (it has dark background)
            var profilePanel = this.FindControl<Border>("ThemeEditorPanel");
            if (profilePanel != null)
            {
                // Already set Force White in XAML, but good to double check if needed
            }
        }

        private void UpdatePaletteResources(TerminalTheme theme)
        {
            ThemePaletteResources.Apply(Resources, theme);
        }

        private void SaveAndClose()
        {
            var fontList = this.FindControl<ComboBox>("FontList");
            var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");
            var scrollbackInput = this.FindControl<NumericUpDown>("ScrollbackInput");
            var themeList = this.FindControl<ComboBox>("ThemeList");
            var opacitySlider = this.FindControl<Slider>("WindowOpacitySlider");
            var ligatureToggle = this.FindControl<CheckBox>("LigatureToggle");
            var complexShapingToggle = this.FindControl<CheckBox>("ComplexShapingToggle");
            var commandAssistToggle = this.FindControl<CheckBox>("CommandAssistToggle");

            // Bg Image inputs
            var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
            var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
            var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");
            var blurList = this.FindControl<ComboBox>("BlurList");

            if (fontSizeInput != null) _settings.FontSize = (double)(fontSizeInput.Value ?? 14);
            if (scrollbackInput != null) _settings.MaxHistory = (int)(scrollbackInput.Value ?? 10000);
            if (opacitySlider != null) _settings.WindowOpacity = opacitySlider.Value;
            if (ligatureToggle != null) _settings.EnableLigatures = ligatureToggle.IsChecked == true;
            if (complexShapingToggle != null) _settings.EnableComplexShaping = complexShapingToggle.IsChecked == true;
            if (commandAssistToggle != null) _settings.CommandAssistEnabled = commandAssistToggle.IsChecked == true;
            var commandAssistPassiveBubbleToggle = this.FindControl<CheckBox>("CommandAssistPassiveBubbleToggle");
            if (commandAssistPassiveBubbleToggle != null) _settings.CommandAssistPassiveBubbleEnabled = commandAssistPassiveBubbleToggle.IsChecked == true;
            var commandAssistHistoryToggle = this.FindControl<CheckBox>("CommandAssistHistoryToggle");
            if (commandAssistHistoryToggle != null) _settings.CommandAssistHistoryEnabled = commandAssistHistoryToggle.IsChecked == true;
            var commandAssistShellIntegrationToggle = this.FindControl<CheckBox>("CommandAssistShellIntegrationToggle");
            if (commandAssistShellIntegrationToggle != null) _settings.CommandAssistShellIntegrationEnabled = commandAssistShellIntegrationToggle.IsChecked == true;
            var nativeSshToggle = this.FindControl<CheckBox>("NativeSshToggle");
            if (nativeSshToggle != null) _settings.ExperimentalNativeSshEnabled = nativeSshToggle.IsChecked == true;
            var agentAccessObserveToggle = this.FindControl<CheckBox>("AgentAccessObserveToggle");
            if (agentAccessObserveToggle != null) _settings.AgentAccessObserveEnabled = agentAccessObserveToggle.IsChecked == true;
            var agentReplayExportToggle = this.FindControl<CheckBox>("AgentReplayExportToggle");
            if (agentReplayExportToggle != null) _settings.AgentReplayExportEnabled = agentReplayExportToggle.IsChecked == true;
            var agentScreenshotToggle = this.FindControl<CheckBox>("AgentScreenshotToggle");
            if (agentScreenshotToggle != null) _settings.AgentScreenshotEnabled = agentScreenshotToggle.IsChecked == true;
            var agentAccessActToggle = this.FindControl<CheckBox>("AgentAccessActToggle");
            if (agentAccessActToggle != null) _settings.AgentAccessActEnabled = agentAccessActToggle.IsChecked == true;
            var agentIndicatorTabRollupList = this.FindControl<ComboBox>("AgentIndicatorTabRollupList");
            if (agentIndicatorTabRollupList?.SelectedItem is ComboBoxItem agentRollupItem)
            {
                _settings.AgentIndicatorTabRollup = agentRollupItem.Content?.ToString() ?? "WritesOnly";
            }
            var longCommandNotificationsToggle = this.FindControl<CheckBox>("LongCommandNotificationsToggle");
            if (longCommandNotificationsToggle != null) _settings.LongCommandNotificationsEnabled = longCommandNotificationsToggle.IsChecked == true;
            var automaticUpdateChecksToggle = this.FindControl<CheckBox>("AutomaticUpdateChecksToggle");
            if (automaticUpdateChecksToggle != null) _settings.AutomaticUpdateChecks = automaticUpdateChecksToggle.IsChecked == true;

            if (fontList?.SelectedItem is ComboBoxItem fontItem)
                _settings.FontFamily = fontItem.Content?.ToString() ?? BundledFontCatalog.DefaultTerminalFontFamily;

            if (themeList?.SelectedItem is ComboBoxItem themeItem)
                _settings.ThemeName = themeItem.Content?.ToString() ?? "Default";

            if (blurList?.SelectedItem is ComboBoxItem blurItem)
                _settings.BlurEffect = blurItem.Content?.ToString() ?? "Acrylic";

            var tabOrientationList = this.FindControl<ComboBox>("TabOrientationList");
            if (tabOrientationList?.SelectedItem is ComboBoxItem tabOrientationItem)
                _settings.TabStripOrientation = tabOrientationItem.Content?.ToString() ?? "Horizontal";

            if (bgPathInput != null) _settings.BackgroundImagePath = bgPathInput.Text ?? "";
            if (bgOpacitySlider != null) _settings.BackgroundImageOpacity = bgOpacitySlider.Value;
            if (bgStretchList?.SelectedItem is ComboBoxItem stretchItem)
                _settings.BackgroundImageStretch = stretchItem.Content?.ToString() ?? "UniformToFill";

            ShortcutBindingResolution shortcutResolution = ShortcutBindingResolver.Resolve(ShortcutCatalog.GetDefinitions(), _shortcutDraftBindings);
            if (!shortcutResolution.IsValid)
            {
                ShowShortcutValidationMessage("Resolve duplicate shortcuts before saving.");
                var tabs = this.FindControl<TabControl>("MainTabs");
                if (tabs != null)
                {
                    tabs.SelectedIndex = 2;
                }

                return;
            }

            _settings.Keybindings = new Dictionary<string, string>(_shortcutDraftBindings, StringComparer.OrdinalIgnoreCase);

            // Sync local profiles list back to settings (SSH connections are store-backed separately).
            _settings.Profiles = NormalizeSettingsProfilesForSave(_profilesList);
            _settings.DefaultProfileId = ResolveDefaultLocalProfileId(_settings.DefaultProfileId, _settings.Profiles);

            // Deltas only: an id at its catalog default is omitted, so a future catalog change
            // reaches existing users without a migration.
            _settings.TitleBarItems = _titleBarDraft.BuildSaveDelta();
            _settings.TitleBarOrder = _titleBarDraft.BuildSaveOrder();

            _settings.Save();
            Close(true); // Return true to indicate saved
        }

        public partial class Helper
        {
            // This method added via partial update manually
        }
    }
}
