using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NovaTerminal.Core;
using System;
using System.Linq;
using System.Net.NetworkInformation;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal
{
    public partial class SettingsWindow : Window
    {
        private TerminalSettings _settings;
        public TerminalSettings Settings => _settings; // Expose for main window to grab without reloading disk

        private TerminalProfile? _selectedProfile;
        private System.Collections.Generic.List<TerminalProfile> _profilesList = new();

        public event Action<double>? OnOpacityChanged;
        public event Action<string>? OnBlurChanged;
        public event Action<string, double, string>? OnBgImageChanged;
        public event Action<string>? OnFontChanged;
        public event Action<double>? OnFontSizeChanged;
        public event Action<string>? OnThemeChanged;

        private DispatcherTimer? _statusTimer;
        private TerminalTheme? _editingTheme;

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

            // Settings editor is local-profiles only; SSH connections are managed in Connection Manager.
            _profilesList = BuildLocalProfilesForEditor(_settings.Profiles);
            _settings.DefaultProfileId = ResolveDefaultLocalProfileId(_settings.DefaultProfileId, _profilesList);

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
                    var newProfile = new TerminalProfile { Name = "New Profile", Command = "cmd.exe", Type = ConnectionType.Local };
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

            // Sync local profiles list back to settings (SSH connections are store-backed separately).
            _settings.Profiles = NormalizeSettingsProfilesForSave(_profilesList);
            _settings.DefaultProfileId = ResolveDefaultLocalProfileId(_settings.DefaultProfileId, _settings.Profiles);

            _settings.Save();
            Close(true); // Return true to indicate saved
        }

        public partial class Helper
        {
            // This method added via partial update manually
        }
    }
}
