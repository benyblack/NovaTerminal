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

        private DispatcherTimer? _statusTimer;

        public SettingsWindow() : this(0, null) { }

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
                ThemeName = p.ThemeName,
                JumpHostProfileId = p.JumpHostProfileId,
                UseSshAgent = p.UseSshAgent,
                IdentityFilePath = p.IdentityFilePath,
                Tags = p.Tags.ToList(),
                Forwards = p.Forwards.Select(f => new ForwardingRule { Type = f.Type, LocalAddress = f.LocalAddress, RemoteAddress = f.RemoteAddress }).ToList()
            }).ToList();

            PopulateFonts();
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

            var btnAddRule = this.FindControl<Button>("BtnAddRule");
            if (btnAddRule != null) btnAddRule.Click += BtnAddForward_Click;

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
                    _selectedProfile.Password = sshPasswordInput.Text ?? "";
            };

            // Note: SshKeyPathInput binding is handled in the Advanced SSH Controls block below


            // Advanced SSH Controls
            var jumpList = this.FindControl<ComboBox>("JumpHostList");
            var radioAgent = this.FindControl<RadioButton>("RadioAuthAgent");
            var radioKey = this.FindControl<RadioButton>("RadioAuthKey");

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
            // Bind SshKeyPathInput directly to IdentityFilePath too
            if (sshKeyPathInput != null) sshKeyPathInput.KeyUp += (s, e) =>
            {
                if (_selectedProfile != null)
                {
                    _selectedProfile.IdentityFilePath = sshKeyPathInput.Text ?? "";
                    _selectedProfile.SshKeyPath = sshKeyPathInput.Text ?? ""; // Sync
                }
            };

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
            if (pwdInput != null)
            {
                // Try new format first (specific to profile name)
                string key = $"SSH:{profile.Name}:{profile.SshUser}@{profile.SshHost}";
                // Fallback 1: Old shared format (User@Host)
                // Fallback 2: Legacy ID format
                string? pass = _vault.GetSecret(key)
                            ?? _vault.GetSecret($"SSH:{profile.SshUser}@{profile.SshHost}")
                            ?? _vault.GetSecret($"profile_{profile.Id}_password");

                pwdInput.Text = pass ?? "";
                profile.Password = pass; // Ensure object is sync'd
            }

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
                foreach (var obj in overrideThemeList.Items)
                    if (obj is ComboBoxItem item && item.Content?.ToString() == targetTheme) { overrideThemeList.SelectedItem = item; break; }
            }

            // Advanced SSH & Port Forwarding
            if (profile.Type == ConnectionType.SSH)
            {
                PopulateJumpHostList(profile);

                var radioAgent = this.FindControl<RadioButton>("RadioAuthAgent");
                var radioKey = this.FindControl<RadioButton>("RadioAuthKey");
                if (radioAgent != null) radioAgent.IsChecked = profile.UseSshAgent;
                if (radioKey != null) radioKey.IsChecked = !profile.UseSshAgent;

                var keyPathInput = this.FindControl<TextBox>("SshKeyPathInput");
                var browseBtn = this.FindControl<Button>("BtnBrowseSshKey");
                if (keyPathInput != null) { keyPathInput.Text = profile.IdentityFilePath ?? profile.SshKeyPath ?? ""; keyPathInput.IsEnabled = !profile.UseSshAgent; }
                if (browseBtn != null) browseBtn.IsEnabled = !profile.UseSshAgent;

                RefreshForwardsList();
            }
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
            var theme = _settings.ThemeName switch
            {
                "Solarized Dark" => TerminalTheme.SolarizedDark,
                "Dracula" => TerminalTheme.Dracula,
                "Monokai" => TerminalTheme.Monokai,
                "One Half Dark" => TerminalTheme.OneHalfDark,
                "GitHub Dark" => TerminalTheme.GitHubDark,
                _ => TerminalTheme.Dark
            };
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

            // Save passwords for any modified/visited SSH profiles
            foreach (var p in _profilesList)
            {
                if (p.Type == ConnectionType.SSH && p.Password != null)
                {
                    // Include Profile Name to ensure uniqueness (distinct passwords per profile)
                    // Format: "SSH:ProfileName:User@Host"
                    string key = $"SSH:{p.Name}:{p.SshUser}@{p.SshHost}";
                    if (!string.IsNullOrEmpty(p.Password))
                        _vault.SetSecret(key, p.Password);
                    else
                        _vault.RemoveSecret(key);
                }
            }

            _settings.Save();
            Close(true); // Return true to indicate saved
        }

        public partial class Helper
        {
            // This method added via partial update manually
        }
    }
}
