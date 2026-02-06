using Avalonia.Controls;
using NovaTerminal.Core;
using System;
using System.Linq;
using SkiaSharp;

namespace NovaTerminal
{
    public partial class SettingsWindow : Window
    {
        private TerminalSettings _settings;
        public TerminalSettings Settings => _settings; // Expose for main window to grab without reloading disk

        private TerminalProfile? _selectedProfile;
        private System.Collections.Generic.List<TerminalProfile> _profilesList = new();
        private readonly VaultService _vault = new();

        public event Action<double>? OnOpacityChanged;
        public event Action<string>? OnBlurChanged;
        public event Action<string, double, string>? OnBgImageChanged;
        public event Action<string>? OnFontChanged;
        public event Action<double>? OnFontSizeChanged;
        public event Action<string>? OnThemeChanged;

        public SettingsWindow(int initialTab = 0, Guid? initialProfileId = null)
        {
            InitializeComponent();
            _settings = TerminalSettings.Load();

            var tabs = this.FindControl<TabControl>("MainTabs");
            if (tabs != null) tabs.SelectedIndex = initialTab;

            // Clone profiles for local editing
            _profilesList = _settings.Profiles.Select(p => new TerminalProfile
            {
                Id = p.Id,
                Name = p.Name,
                Command = p.Command,
                Arguments = p.Arguments,
                StartingDirectory = p.StartingDirectory,
                Type = p.Type,
                SshHost = p.SshHost,
                SshPort = p.SshPort,
                SshUser = p.SshUser,
                SshKeyPath = p.SshKeyPath,
                FontFamily = p.FontFamily,
                FontSize = p.FontSize,
                ThemeName = p.ThemeName
            }).ToList();

            PopulateFonts();
            LoadCurrentSettings();
            PopulateProfilesList();
            ApplyTheme();

            var btnSave = this.FindControl<Button>("BtnSave");
            var btnCancel = this.FindControl<Button>("BtnCancel");
            var opacitySlider = this.FindControl<Slider>("WindowOpacitySlider");
            var opacityDisplay = this.FindControl<TextBlock>("OpacityValueDisplay");
            var blurList = this.FindControl<ComboBox>("BlurList");

            // Profile Controls
            var profilesListBox = this.FindControl<ListBox>("ProfilesListBox");
            var btnAddProfile = this.FindControl<Button>("BtnAddProfile");
            var btnDeleteProfile = this.FindControl<Button>("BtnDeleteProfile");
            var btnSetDefault = this.FindControl<Button>("BtnSetDefault");

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
                    var newProfile = new TerminalProfile { Name = "New Profile", Command = "cmd.exe" };
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

            var importStatus = this.FindControl<TextBlock>("ImportStatusText");

            var btnImportWT = this.FindControl<Button>("BtnImportWT");
            if (btnImportWT != null)
            {
                btnImportWT.Click += (s, e) =>
                {
                    var imported = ProfileImporter.ImportWindowsTerminalProfiles();
                    int added = 0;
                    foreach (var p in imported)
                    {
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

            var btnImportSSH = this.FindControl<Button>("BtnImportSSH");
            if (btnImportSSH != null)
            {
                btnImportSSH.Click += (s, e) =>
                {
                    var imported = ProfileImporter.ImportSshConfig();
                    int added = 0;
                    foreach (var p in imported)
                    {
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
                        if (importStatus != null) importStatus.Text = $"Imported {added} SSH hosts.";
                    }
                    else
                    {
                        if (importStatus != null) importStatus.Text = "No new hosts found.";
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
            var nameInput = this.FindControl<TextBox>("ProfileNameInput");
            var commandInput = this.FindControl<TextBox>("ProfileCommandInput");
            var argsInput = this.FindControl<TextBox>("ProfileArgsInput");
            var cwdInput = this.FindControl<TextBox>("ProfileCwdInput");
            var groupInput = this.FindControl<TextBox>("ProfileGroupInput");
            var tagsInput = this.FindControl<TextBox>("ProfileTagsInput");

            if (nameInput != null) nameInput.KeyUp += (s, e) => { if (_selectedProfile != null) { _selectedProfile.Name = nameInput.Text ?? ""; RefreshProfileListItem(_selectedProfile); } };
            if (commandInput != null) commandInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.Command = commandInput.Text ?? ""; };
            if (argsInput != null) argsInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.Arguments = argsInput.Text ?? ""; };
            if (argsInput != null) argsInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.Arguments = argsInput.Text ?? ""; };
            if (cwdInput != null) cwdInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.StartingDirectory = cwdInput.Text ?? ""; };
            if (groupInput != null) groupInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.Group = groupInput.Text ?? "General"; };
            if (tagsInput != null) tagsInput.KeyUp += (s, e) =>
            {
                if (_selectedProfile != null)
                    _selectedProfile.Tags = (tagsInput.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            };

            // Connection Type and SSH Inputs
            var typeList = this.FindControl<ComboBox>("ProfileTypeList");
            var sshPanel = this.FindControl<StackPanel>("SshSettingsPanel");
            var sshHostInput = this.FindControl<TextBox>("SshHostInput");
            var sshPortInput = this.FindControl<NumericUpDown>("SshPortInput");
            var sshUserInput = this.FindControl<TextBox>("SshUserInput");
            var sshPasswordInput = this.FindControl<TextBox>("SshPasswordInput");
            var sshKeyPathInput = this.FindControl<TextBox>("SshKeyPathInput");
            var btnBrowseSshKey = this.FindControl<Button>("BtnBrowseSshKey");

            if (typeList != null)
            {
                typeList.SelectionChanged += (s, e) =>
                {
                    if (_selectedProfile != null)
                    {
                        _selectedProfile.Type = (ConnectionType)typeList.SelectedIndex;
                        if (sshPanel != null) sshPanel.IsVisible = _selectedProfile.Type == ConnectionType.SSH;
                    }
                };
            }

            if (sshHostInput != null) sshHostInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.SshHost = sshHostInput.Text ?? ""; };
            if (sshPortInput != null) sshPortInput.ValueChanged += (s, e) => { if (_selectedProfile != null && sshPortInput.Value.HasValue) _selectedProfile.SshPort = (int)sshPortInput.Value.Value; };
            if (sshUserInput != null) sshUserInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.SshUser = sshUserInput.Text ?? ""; };
            if (sshPasswordInput != null) sshPasswordInput.KeyUp += (s, e) =>
            {
                if (_selectedProfile != null)
                    _vault.SetSecret($"profile_{_selectedProfile.Id}_password", sshPasswordInput.Text ?? "");
            };
            if (sshKeyPathInput != null) sshKeyPathInput.KeyUp += (s, e) => { if (_selectedProfile != null) _selectedProfile.SshKeyPath = sshKeyPathInput.Text ?? ""; };

            if (btnBrowseSshKey != null)
            {
                btnBrowseSshKey.Click += async (s, e) =>
                {
                    var dlg = new OpenFileDialog { Title = "Select Private Key", AllowMultiple = false };
                    var result = await dlg.ShowAsync(this);
                    if (result != null && result.Length > 0 && sshKeyPathInput != null)
                    {
                        sshKeyPathInput.Text = result[0];
                        if (_selectedProfile != null) _selectedProfile.SshKeyPath = result[0];
                    }
                };
            }

            var checkFont = this.FindControl<CheckBox>("CheckOverrideFont");
            var checkSize = this.FindControl<CheckBox>("CheckOverrideSize");
            var checkTheme = this.FindControl<CheckBox>("CheckOverrideTheme");

            var overrideFontList = this.FindControl<ComboBox>("OverrideFontList");
            var overrideFontSize = this.FindControl<NumericUpDown>("OverrideFontSizeInput");
            var overrideThemeList = this.FindControl<ComboBox>("OverrideThemeList");

            // Logic: 
            // - Checking the box initializes the override with the current global value if it was null
            // - Unchecking sets it to null
            // - changing the combo/input updates the override value directly

            if (checkFont != null)
            {
                checkFont.IsCheckedChanged += (s, e) =>
                {
                    if (_selectedProfile != null)
                    {
                        if (checkFont.IsChecked == true)
                        {
                            // If we are enabling, ensure we have a valid value (fallback to global)
                            if (_selectedProfile.FontFamily == null) _selectedProfile.FontFamily = _settings.FontFamily;
                            // Also ensure UI matches
                            if (overrideFontList != null)
                                foreach (ComboBoxItem item in overrideFontList.Items)
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
                                foreach (ComboBoxItem item in overrideThemeList.Items)
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

            // Core Settings Controls
            var fontList = this.FindControl<ComboBox>("FontList");
            var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");
            var themeList = this.FindControl<ComboBox>("ThemeList");

            if (fontList != null)
            {
                fontList.SelectionChanged += (s, e) =>
                {
                    if (fontList.SelectedItem is ComboBoxItem item && item.Content != null)
                    {
                        OnFontChanged?.Invoke(item.Content.ToString() ?? "Consolas");
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
                        OnThemeChanged?.Invoke(item.Content.ToString() ?? "Default");

                        // Also apply theme to Settings Window itself for preview consistency
                        // (Optional, user might prefer settings window stays static, but it's nice)
                        // Actually, let's keep SettingsWindow static for now to avoid complexity, main window is what matters.
                    }
                };
            }

            // Bg Image Controls
            var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
            var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
            var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");
            var bgOpacityDisplay = this.FindControl<TextBlock>("BgImageOpacityDisplay");

            // Define trigger helper
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

            // Update opacity display when slider changes
            if (opacitySlider != null && opacityDisplay != null)
            {
                // Set initial value
                opacityDisplay.Text = $"{(int)(opacitySlider.Value * 100)}%";

                // Update on change
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
                    var dlg = new OpenFileDialog();
                    dlg.Filters.Add(new FileDialogFilter { Name = "Images", Extensions = { "png", "jpg", "jpeg", "bmp", "webp" } });
                    dlg.AllowMultiple = false;
                    var result = await dlg.ShowAsync(this);
                    if (result != null && result.Length > 0)
                    {
                        if (bgPathInput != null)
                        {
                            bgPathInput.Text = result[0];
                            TriggerBgUpdate();
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

            if (btnSave != null) btnSave.Click += (s, e) => SaveAndClose();
            if (btnCancel != null) btnCancel.Click += (s, e) => Close();

            // Auto-select profile if requested
            if (initialProfileId.HasValue && profilesListBox != null)
            {
                foreach (ListBoxItem item in profilesListBox.Items)
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

            var fonts = SkiaSharp.SKFontManager.Default.FontFamilies
                .OrderBy(f => f)
                .Select(f => new ComboBoxItem { Content = f })
                .ToList();

            foreach (var f in fonts)
            {
                if (fontList != null) fontList.Items.Add(f);
                // Create a separate instance for the second list to avoid visual tree parenting issues
                if (overrideFontList != null) overrideFontList.Items.Add(new ComboBoxItem { Content = f.Content });
            }
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
            this.FindControl<TextBox>("ProfileArgsInput")!.Text = profile.Arguments ?? "";
            this.FindControl<TextBox>("ProfileCwdInput")!.Text = profile.StartingDirectory ?? "";
            this.FindControl<TextBox>("ProfileGroupInput")!.Text = profile.Group ?? "General";
            this.FindControl<TextBox>("ProfileTagsInput")!.Text = string.Join(", ", profile.Tags ?? new System.Collections.Generic.List<string>());

            var typeList = this.FindControl<ComboBox>("ProfileTypeList");
            if (typeList != null) typeList.SelectedIndex = (int)profile.Type;

            var sshPanel = this.FindControl<StackPanel>("SshSettingsPanel");
            if (sshPanel != null) sshPanel.IsVisible = profile.Type == ConnectionType.SSH;

            this.FindControl<TextBox>("SshHostInput")!.Text = profile.SshHost ?? "";
            this.FindControl<NumericUpDown>("SshPortInput")!.Value = profile.SshPort;
            this.FindControl<TextBox>("SshUserInput")!.Text = profile.SshUser ?? "";
            this.FindControl<TextBox>("SshKeyPathInput")!.Text = profile.SshKeyPath ?? "";

            // Load password from vault
            var pwdInput = this.FindControl<TextBox>("SshPasswordInput");
            if (pwdInput != null) pwdInput.Text = _vault.GetSecret($"profile_{profile.Id}_password") ?? "";

            this.FindControl<CheckBox>("CheckOverrideFont")!.IsChecked = profile.FontFamily != null;
            this.FindControl<CheckBox>("CheckOverrideSize")!.IsChecked = profile.FontSize.HasValue;
            this.FindControl<CheckBox>("CheckOverrideTheme")!.IsChecked = profile.ThemeName != null;

            // Sync values to override inputs
            var overrideFontList = this.FindControl<ComboBox>("OverrideFontList");
            if (overrideFontList != null)
            {
                var targetFont = profile.FontFamily ?? _settings.FontFamily;
                foreach (ComboBoxItem item in overrideFontList.Items)
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
                foreach (ComboBoxItem item in overrideThemeList.Items)
                    if (item.Content?.ToString() == targetTheme) { overrideThemeList.SelectedItem = item; break; }
            }
        }

        private void LoadCurrentSettings()
        {
            var fontList = this.FindControl<ComboBox>("FontList");
            var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");
            var scrollbackInput = this.FindControl<NumericUpDown>("ScrollbackInput");
            var themeList = this.FindControl<ComboBox>("ThemeList");
            var opacitySlider = this.FindControl<Slider>("WindowOpacitySlider");
            var opacityDisplay = this.FindControl<TextBlock>("OpacityValueDisplay");

            // Bg Image Controls
            var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
            var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
            var bgOpacityDisplay = this.FindControl<TextBlock>("BgImageOpacityDisplay");
            var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");

            if (fontSizeInput != null) fontSizeInput.Value = (decimal)_settings.FontSize;
            if (scrollbackInput != null) scrollbackInput.Value = (decimal)_settings.MaxHistory;
            if (opacitySlider != null)
            {
                opacitySlider.Value = _settings.WindowOpacity;
                if (opacityDisplay != null)
                    opacityDisplay.Text = $"{(int)(_settings.WindowOpacity * 100)}%";
            }

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

        private void ApplyTheme()
        {
            var theme = (_settings.ThemeName == "Solarized Dark") ? TerminalTheme.SolarizedDark : TerminalTheme.Dark;
            this.Background = new Avalonia.Media.SolidColorBrush(theme.Background);
            this.Foreground = new Avalonia.Media.SolidColorBrush(theme.Foreground);
        }

        private void SaveAndClose()
        {
            var fontList = this.FindControl<ComboBox>("FontList");
            var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");
            var scrollbackInput = this.FindControl<NumericUpDown>("ScrollbackInput");
            var themeList = this.FindControl<ComboBox>("ThemeList");
            var opacitySlider = this.FindControl<Slider>("WindowOpacitySlider");

            // Bg Image inputs
            var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
            var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
            var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");
            var blurList = this.FindControl<ComboBox>("BlurList");

            if (fontSizeInput != null) _settings.FontSize = (double)(fontSizeInput.Value ?? 14);
            if (scrollbackInput != null) _settings.MaxHistory = (int)(scrollbackInput.Value ?? 10000);
            if (opacitySlider != null) _settings.WindowOpacity = opacitySlider.Value;

            if (fontList?.SelectedItem is ComboBoxItem fontItem)
                _settings.FontFamily = fontItem.Content?.ToString() ?? "Consolas";

            if (themeList?.SelectedItem is ComboBoxItem themeItem)
                _settings.ThemeName = themeItem.Content?.ToString() ?? "Default";

            if (blurList?.SelectedItem is ComboBoxItem blurItem)
                _settings.BlurEffect = blurItem.Content?.ToString() ?? "Acrylic";

            if (bgPathInput != null) _settings.BackgroundImagePath = bgPathInput.Text ?? "";
            if (bgOpacitySlider != null) _settings.BackgroundImageOpacity = bgOpacitySlider.Value;
            if (bgStretchList?.SelectedItem is ComboBoxItem stretchItem)
                _settings.BackgroundImageStretch = stretchItem.Content?.ToString() ?? "UniformToFill";

            // Sync profiles list back to settings
            _settings.Profiles = _profilesList;

            _settings.Save();
            Close(true); // Return true to indicate saved
        }

        public partial class Helper
        {
            // This method added via partial update manually
        }
    }
}
