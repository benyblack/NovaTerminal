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
using NovaTerminal.Services.Ssh;
using NovaTerminal.Shell.Shortcuts;
using NovaTerminal.Shell.TitleBar;

namespace NovaTerminal
{
    public partial class SettingsWindow : Window
    {
        private TerminalSettings _settings;
        public TerminalSettings Settings => _settings; // Expose for main window to grab without reloading disk

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

        public SettingsWindow() : this(0, null) { }

        public SettingsWindow(int initialTab = 0, Guid? initialProfileId = null)
        {
            InitializeComponent();
            _settings = TerminalSettings.Load();
            var sshMigration = new SshLegacyProfileMigrationService();
            if (sshMigration.MigrateLegacyProfiles(_settings))
            {
                _settings.Save();
            }
            ApplyTheme();

            var tabs = this.FindControl<TabControl>("MainTabs");
            if (tabs != null) tabs.SelectedIndex = initialTab;

            // Keep the sidebar list boxes in sync with the tab control. The previous single
            // list box drove selection via a direct SelectedIndex binding; that breaks once the
            // sidebar is split (InterfaceNav holds tabs 0-2, AssistantNav holds tabs 3-4,
            // ConnectionNav holds tab 5), so route everything through this small dispatcher
            // instead. The tab header strip is not the navigation — these lists are — so a new
            // tab MUST get a sidebar item and a mapping here, or it is unreachable. That is not
            // hypothetical: the SSH tab initially shipped without one, which silently remapped
            // the "Agent Access" item onto SSH and stranded the real Agent Access tab
            // (Codex review finding on #332). New tabs go at the END of the TabControl so the
            // existing offsets stay true.
            var interfaceNav = this.FindControl<ListBox>("InterfaceNav");
            var assistantNav = this.FindControl<ListBox>("AssistantNav");
            var connectionNav = this.FindControl<ListBox>("ConnectionNav");
            if (tabs != null && interfaceNav != null && assistantNav != null && connectionNav != null)
            {
                tabs.SelectionChanged += (_, _) => SyncSidebarFromTabs(tabs, interfaceNav, assistantNav, connectionNav);
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
                SyncSidebarFromTabs(tabs, interfaceNav, assistantNav, connectionNav);
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
        /// tabs 3-4 (Command Assist / Agent Access), ConnectionNav owns tab 5 (SSH). The other
        /// list boxes are cleared so only one item ever reads as selected.
        /// </summary>
        private static void SyncSidebarFromTabs(TabControl tabs, ListBox interfaceNav, ListBox assistantNav, ListBox connectionNav)
        {
            var idx = tabs.SelectedIndex;
            if (idx < 0)
            {
                interfaceNav.SelectedIndex = -1;
                assistantNav.SelectedIndex = -1;
                connectionNav.SelectedIndex = -1;
                return;
            }

            if (idx < 3)
            {
                interfaceNav.SelectedIndex = idx;
                assistantNav.SelectedIndex = -1;
                connectionNav.SelectedIndex = -1;
            }
            else if (idx < 5)
            {
                interfaceNav.SelectedIndex = -1;
                assistantNav.SelectedIndex = idx - 3;
                connectionNav.SelectedIndex = -1;
            }
            else
            {
                interfaceNav.SelectedIndex = -1;
                assistantNav.SelectedIndex = -1;
                connectionNav.SelectedIndex = idx - 5;
            }
        }

        private void PopulateFonts()
        {
            var fontList = this.FindControl<ComboBox>("FontList");
            var overrideFontList = this.FindControl<ComboBox>("OverrideFontList");

            if (fontList != null) fontList.Items.Clear();
            if (overrideFontList != null) overrideFontList.Items.Clear();

            var fonts = BuildFontFamilyChoices(
                    SkiaSharp.SKFontManager.Default.FontFamilies,
                    _selectedProfile?.FontFamily ?? _settings.FontFamily)
                .Select(f => new ComboBoxItem { Content = f })
                .ToList();

            foreach (var f in fonts)
            {
                if (fontList != null) fontList.Items.Add(f);
                // Create a separate instance for the second list to avoid visual tree parenting issues
                if (overrideFontList != null) overrideFontList.Items.Add(new ComboBoxItem { Content = f.Content });
            }
        }

        internal static System.Collections.Generic.List<string> BuildFontFamilyChoices(
            System.Collections.Generic.IEnumerable<string> systemFonts,
            string? configuredFontFamily)
        {
            var names = new System.Collections.Generic.SortedSet<string>(
                systemFonts?.Where(f => !string.IsNullOrWhiteSpace(f)) ?? System.Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            names.Add(BundledFontCatalog.DefaultTerminalFontFamily);

            if (!string.IsNullOrWhiteSpace(configuredFontFamily))
            {
                names.Add(configuredFontFamily);
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

        private Control CreateTitleBarRow(
            TitleBarCatalogEntry entry,
            int index)
        {
            var state = _titleBarDraft.GetState(entry.Id);
            bool isPinned = state == TitleBarItemState.Pinned;

            var row = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#23272f")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2a2f38")),
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
                Background = new SolidColorBrush(Color.Parse("#23272f")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2a2f38")),
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
                Background = new SolidColorBrush(Color.Parse("#23272f")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2a2f38")),
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
            var longCommandNotificationsToggle = this.FindControl<CheckBox>("LongCommandNotificationsToggle");
            if (longCommandNotificationsToggle != null) longCommandNotificationsToggle.IsChecked = _settings.LongCommandNotificationsEnabled;
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
                foreach (ComboBoxItem item in themeList.Items.Cast<ComboBoxItem>())
                {
                    if (item.Content?.ToString() == _settings.ThemeName)
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
            var longCommandNotificationsToggle = this.FindControl<CheckBox>("LongCommandNotificationsToggle");
            if (longCommandNotificationsToggle != null) _settings.LongCommandNotificationsEnabled = longCommandNotificationsToggle.IsChecked == true;

            if (fontList?.SelectedItem is ComboBoxItem fontItem)
                _settings.FontFamily = fontItem.Content?.ToString() ?? BundledFontCatalog.DefaultTerminalFontFamily;

            if (themeList?.SelectedItem is ComboBoxItem themeItem)
                _settings.ThemeName = themeItem.Content?.ToString() ?? "Default";

            if (blurList?.SelectedItem is ComboBoxItem blurItem)
                _settings.BlurEffect = blurItem.Content?.ToString() ?? "Acrylic";

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
