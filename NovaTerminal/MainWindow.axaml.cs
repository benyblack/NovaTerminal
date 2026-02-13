using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using NovaTerminal.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SkiaSharp;

using NovaTerminal.Controls;

namespace NovaTerminal
{
    public partial class MainWindow : Window
    {
        private TerminalPane? _currentPane;
        private TerminalSettings _settings;
        private GlobalHotkey? _globalHotkey;

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (_settings.QuakeModeEnabled)
            {
                try
                {
                    _globalHotkey = new GlobalHotkey(this);
                    _globalHotkey.OnHotkeyPressed += ToggleVisibility;
                    // Register Alt (1) + ~ (0xC0). 
                    // MVP Hardcoded. Future: Parse _settings.GlobalHotkey
                    _globalHotkey.Register(1, 0xC0);
                }
                catch { /* Ignore P/Invoke errors on non-Windows */ }
            }
        }

        private void ToggleConnections()
        {
            var overlay = this.FindControl<Border>("ConnectionOverlay");
            var connManager = this.FindControl<ConnectionManager>("ConnectionManagerControl");

            if (overlay != null)
            {
                overlay.IsVisible = !overlay.IsVisible;
                if (overlay.IsVisible && connManager != null)
                {
                    connManager.LoadProfiles(_settings.Profiles);
                    // Focus search
                    var search = connManager.FindControl<TextBox>("SearchInput");
                    search?.Focus();
                }
                else
                {
                    _currentPane?.ActiveControl.Focus();
                }
            }
        }

        private void ToggleVisibility()
        {
            if (this.IsVisible)
            {
                if (this.IsActive)
                {
                    // Visible and Focused -> Hide
                    this.Hide();
                }
                else
                {
                    // Visible but Blur -> Focus
                    this.Activate();
                    this.Focus();
                    _currentPane?.ActiveControl.Focus();
                }
            }
            else
            {
                // Hidden -> Show
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
                this.Focus();
                _currentPane?.ActiveControl.Focus();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            _settings = TerminalSettings.Load();

            // Ensure visual tree is ready for initial tab border
            this.Loaded += (s, e) =>
            {
                // Give layout one more tick to settle
                Dispatcher.UIThread.Post(() => UpdateTabVisuals(), DispatcherPriority.Input);
            };

            var tabs = this.FindControl<TabControl>("Tabs");
            var btnNew = this.FindControl<Button>("BtnNewTab");
            var settingsBtn = this.FindControl<Button>("SettingsBtn");
            var titleBar = this.FindControl<Grid>("TitleBar");
            var dragBorder = this.FindControl<Border>("DragBorder");

            if (dragBorder != null)
            {
                dragBorder.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                        BeginMoveDrag(e);
                };
            }

            if (titleBar != null)
            {
                titleBar.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                        BeginMoveDrag(e);
                };
            }


            var btnConnections = this.FindControl<Button>("BtnConnections");
            var btnCloseConn = this.FindControl<Button>("BtnCloseConnections");
            var connOverlay = this.FindControl<Border>("ConnectionOverlay");
            var connManager = this.FindControl<ConnectionManager>("ConnectionManagerControl");

            if (btnConnections != null) btnConnections.Click += (s, e) => ToggleConnections();
            if (btnCloseConn != null) btnCloseConn.Click += (s, e) => ToggleConnections();

            if (connManager != null)
            {
                connManager.OnConnect += (profile) =>
                {
                    AddTab(profile);
                    ToggleConnections();
                    // Save LastUsed update
                    _settings.Save();
                };
                connManager.OnSyncRequested += HandleSshSync;
                connManager.OnEditProfile += async (profile) =>
                {
                    // Open Settings (Tab 1 = Profiles), select profile
                    await OpenSettings(1, profile.Id);
                };
            }

            if (settingsBtn != null) settingsBtn.Click += async (s, e) =>
            {
                await OpenSettings(0);
            };



            if (tabs != null)
            {
                tabs.SelectionChanged += (s, e) =>
                {
                    if (tabs.SelectedItem is TabItem ti && ti.Content is TerminalPane pane)
                    {
                        _currentPane = pane;
                        pane.Focus();
                    }
                    UpdateTabVisuals();
                };
            }

            ApplyThemeToUI();

            PopulateNewTabMenu();

            var menuManage = this.FindControl<MenuItem>("MenuManageProfiles");
            if (menuManage != null) menuManage.Click += async (s, e) =>
            {
                await OpenSettings(1); // Open Tab 1 (Profiles)
            };

            var menuOpenRec = this.FindControl<MenuItem>("MenuOpenRecording");
            if (menuOpenRec != null) menuOpenRec.Click += async (s, e) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open Replay File",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Nova Recordings") { Patterns = new[] { "*.rec", "*.cast" } } }
                });

                if (files.Count >= 1)
                {
                    var path = files[0].Path.LocalPath;
                    var replayWin = new NovaTerminal.UI.Replay.ReplayWindow(path);
                    replayWin.Show();
                }
            };

            // Global Focus Tracking
            this.AddHandler(GotFocusEvent, (s, e) =>
            {
                var pane = (e.Source as Control)?.FindAncestorOfType<TerminalPane>();
                if (pane != null)
                {
                    _currentPane = pane;
                }
            }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);

            var defaultProfile = _settings.Profiles.Find(p => p.Id == _settings.DefaultProfileId) ?? _settings.Profiles[0];

            // Attempt to restore session
            if (tabs != null)
            {
                SessionManager.RestoreSession(this, tabs, _settings);

                // If restore failed or was empty, load default tab
                if (tabs.Items.Count == 0)
                {
                    AddTab(defaultProfile);
                }
            }
            else
            {
                AddTab(defaultProfile);
            }

            SetupCommandPalette();
            InitializeCommandPaletteUI();
            InitializeTransferCenterUI();

            // Keyboard Shortcuts
            this.AddHandler(KeyDownEvent, (s, e) =>
            {
                var modifiers = e.KeyModifiers;
                bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
                bool isShift = (modifiers & KeyModifiers.Shift) != 0;

                if (isCtrl && isShift && e.Key == Key.P)
                {
                    ToggleCommandPalette();
                    e.Handled = true;
                    return;
                }

                var overlay = this.FindControl<Grid>("CommandPaletteOverlay");
                if (overlay != null && overlay.IsVisible)
                {
                    // Trap keys when palette is open
                    if (e.Key == Key.Escape) ToggleCommandPalette();
                    // Let TextBox handle the rest, but prevent bubbling to terminal
                    // Actually, if we are focused on the TextBox, we don't need to do much.
                    // But if focus somehow escaped, we want to ensure we don't type in terminal.
                    return;
                }

                if (isCtrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
                {
                    _settings.FontSize++;
                    ApplySettingsToAllTabs();
                    _settings.Save();
                    e.Handled = true;
                    return;
                }
                if (isCtrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
                {
                    _settings.FontSize = Math.Max(6, _settings.FontSize - 1);
                    ApplySettingsToAllTabs();
                    _settings.Save();
                    e.Handled = true;
                    return;
                }
                if (isCtrl && isShift && e.Key == Key.T)
                {
                    AddTab();
                    e.Handled = true;
                    return;
                }
                if (isCtrl && isShift && e.Key == Key.W)
                {
                    CloseActivePane();
                    e.Handled = true;
                    return;
                }
                if ((isCtrl && isShift && e.Key == Key.F) || (isCtrl && e.Key == Key.F))
                {
                    _currentPane?.ToggleSearch();
                    e.Handled = true;
                    return;
                }
                if (isCtrl && isShift && e.Key == Key.D)
                {
                    SplitPane(Avalonia.Layout.Orientation.Vertical);
                    e.Handled = true;
                    return;
                }
                if (isCtrl && isShift && e.Key == Key.E)
                {
                    SplitPane(Avalonia.Layout.Orientation.Horizontal);
                    e.Handled = true;
                    return;
                }
                if (isCtrl && e.Key == Key.Tab && tabs != null)
                {
                    int count = tabs.Items.Count;
                    if (count > 1)
                    {
                        int current = tabs.SelectedIndex;
                        current = isShift ? (current - 1 + count) % count : (current + 1) % count;
                        tabs.SelectedIndex = current;
                    }
                    e.Handled = true;
                    return;
                }
                if (isCtrl && e.Key == Key.V)
                {
                    _ = PasteFromClipboardAsync();
                    e.Handled = true;
                    return;
                }
                if ((modifiers & KeyModifiers.Alt) != 0)
                {
                    MoveDirection? dir = e.Key switch
                    {
                        Key.Left => MoveDirection.Left,
                        Key.Right => MoveDirection.Right,
                        Key.Up => MoveDirection.Up,
                        Key.Down => MoveDirection.Down,
                        _ => null
                    };
                    if (dir.HasValue && NavigatePane(dir.Value))
                    {
                        e.Handled = true;
                        return;
                    }
                }
            }, RoutingStrategies.Tunnel);

            try { Vault = new VaultService(); } catch { }
        }

        private void HandleSshSync()
        {
            try
            {
                var sshProfiles = NovaTerminal.Core.ProfileImporter.ImportSshConfig();
                bool changed = false;

                foreach (var newProfile in sshProfiles)
                {
                    // Match by Name and Type
                    var existing = _settings.Profiles.FirstOrDefault(p => p.Name == newProfile.Name && p.Type == NovaTerminal.Core.ConnectionType.SSH);
                    if (existing != null)
                    {
                        // MERGE: Update technical details, preserve user metadata (Groups, Tags, Icon, LastUsed)
                        bool diff = existing.SshHost != newProfile.SshHost ||
                                    existing.SshPort != newProfile.SshPort ||
                                    existing.SshUser != newProfile.SshUser ||
                                    existing.SshKeyPath != newProfile.SshKeyPath;

                        if (diff)
                        {
                            existing.SshHost = newProfile.SshHost;
                            existing.SshPort = newProfile.SshPort;
                            existing.SshUser = newProfile.SshUser;
                            existing.SshKeyPath = newProfile.SshKeyPath;
                            changed = true;
                        }
                    }
                    else
                    {
                        // ADD: New profile
                        _settings.Profiles.Add(newProfile);
                        changed = true;
                    }
                }

                if (changed)
                {
                    _settings.Save();
                    RefreshProfileUIs();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Sync Error: {ex.Message}");
            }
        }

        public void ApplySettingsRecursive(Control? control, TerminalSettings settings)
        {
            if (control == null) return;
            if (control is TerminalPane pane)
            {
                // Refresh the profile object if possible to pick up overrides
                if (pane.Profile != null)
                {
                    var updatedProfile = settings.Profiles.Find(p => p.Id == pane.Profile.Id);
                    if (updatedProfile != null) pane.UpdateProfile(updatedProfile);
                }
                pane.ApplySettings(settings);
            }
            else if (control is Panel panel) foreach (var child in panel.Children) if (child is Control c) ApplySettingsRecursive(c, settings);
                    else if (control is ContentControl cc) ApplySettingsRecursive(cc.Content as Control, settings);
        }

        private void ApplySettingsToAllTabs()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                foreach (TabItem ti in tabs.Items.Cast<TabItem>())
                {
                    ApplySettingsRecursive(ti.Content as Control, _settings);
                }
            }
        }

        private enum MoveDirection { Left, Right, Up, Down }
        private bool NavigatePane(MoveDirection dir) => NavigatePaneRecursive(_currentPane, dir);
        private bool NavigatePaneRecursive(Control? start, MoveDirection dir)
        {
            if (start == null || start.Parent is not Grid parentGrid) return false;
            int r = Grid.GetRow(start);
            int c = Grid.GetColumn(start);
            int targetR = r, targetC = c;
            switch (dir)
            {
                case MoveDirection.Left: targetC--; break;
                case MoveDirection.Right: targetC++; break;
                case MoveDirection.Up: targetR--; break;
                case MoveDirection.Down: targetR++; break;
            }
            var sibling = parentGrid.Children.FirstOrDefault(x => Grid.GetRow(x as Control) == targetR && Grid.GetColumn(x as Control) == targetC) as Control;
            if (sibling != null)
            {
                FocusFirstPane(sibling);
                return true;
            }
            if (parentGrid.Parent is Control grandParent && (grandParent is Grid || grandParent is ContentPresenter || grandParent is TabItem))
                return NavigatePaneRecursive(parentGrid, dir);
            return false;
        }

        private void FocusFirstPane(Control control)
        {
            if (control is TerminalPane pane) pane.ActiveControl.Focus();
            else if (control is Panel panel && panel.Children.Count > 0 && panel.Children[0] is Control child) FocusFirstPane(child);
        }

        public static VaultService? Vault { get; private set; }

        private void CloseTab(TabItem ti)
        {
            if (ti.Content is Control content) DisposeControlTree(content);
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                tabs.Items.Remove(ti);
                if (tabs.Items.Count == 0) Close();
            }
        }

        private void CloseActivePane()
        {
            if (_currentPane == null) return;

            // Check if we are in a split (Parent is Grid with multiple children/splitter)
            if (_currentPane.Parent is Grid parentGrid && parentGrid.Children.Count >= 2)
            {
                // We are in a split!
                // 1. Identify Sibling (The non-splitter control that isn't us)
                var sibling = parentGrid.Children.OfType<Control>()
                                    .FirstOrDefault(c => c != _currentPane && !(c is GridSplitter));

                if (sibling != null)
                {
                    // 2. Identify Grandparent
                    var grandParent = parentGrid.Parent;

                    // 3. Detach visuals
                    parentGrid.Children.Clear();

                    // 4. Promote Sibling to Grandparent
                    if (grandParent is ContentPresenter cp) cp.Content = sibling;
                    else if (grandParent is TabItem tab) tab.Content = sibling;
                    else if (grandParent is Grid gpGrid)
                    {
                        Grid.SetRow(sibling, Grid.GetRow(parentGrid));
                        Grid.SetColumn(sibling, Grid.GetColumn(parentGrid));
                        Grid.SetRowSpan(sibling, Grid.GetRowSpan(parentGrid));
                        Grid.SetColumnSpan(sibling, Grid.GetColumnSpan(parentGrid));

                        int index = gpGrid.Children.IndexOf(parentGrid);
                        if (index >= 0)
                        {
                            gpGrid.Children.RemoveAt(index);
                            gpGrid.Children.Insert(index, sibling);
                        }
                        else gpGrid.Children.Add(sibling);
                    }
                    else if (grandParent is Panel p)
                    {
                        int index = p.Children.IndexOf(parentGrid);
                        p.Children.Remove(parentGrid);
                        if (index >= 0) p.Children.Insert(index, sibling);
                        else p.Children.Add(sibling);
                    }

                    // 5. Dispose the closed pane
                    DisposeControlTree(_currentPane);

                    // 6. Focus Sibling
                    FocusFirstPane(sibling);
                    return;
                }
            }

            // Fallback: If not in a split, close the tab
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs?.SelectedItem is TabItem ti) CloseTab(ti);
        }

        private void DisposeControlTree(Control control)
        {
            if (control is TerminalPane pane) Task.Run(() => { try { pane.Dispose(); } catch { } });
            else if (control is Panel panel) { foreach (var child in panel.Children) if (child is Control c) DisposeControlTree(c); }
            else if (control is ContentPresenter cp && cp.Content is Control childContent) DisposeControlTree(childContent);
        }

        void AddTab(TerminalProfile? profile = null)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            // Fallback to default if null
            if (profile == null)
            {
                profile = _settings.Profiles.Find(p => p.Id == _settings.DefaultProfileId) ?? _settings.Profiles[0];
            }
            else
            {
                // REFRESH the profile from settings to ensure we have the latest version (e.g. updated overrides)
                var freshProfile = _settings.Profiles.Find(p => p.Id == profile.Id);
                if (freshProfile != null) profile = freshProfile;
            }

            // Ensure the command exists on this platform (handles shared settings between Windows/Linux)
            if (profile.Type == ConnectionType.Local)
            {
                bool exists = File.Exists(profile.Command) || ShellHelper.InPath(profile.Command);
                if (!exists)
                {
                    profile.Command = ShellHelper.GetDefaultShell();
                    profile.Arguments = ""; // Reset potentially platform-specific args
                }
            }

            // Construct command if it's an SSH connection
            if (profile.Type == ConnectionType.SSH)
            {
                profile.Command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ssh.exe" : "ssh";
                profile.Arguments = profile.GenerateSshArguments(_settings.Profiles);
            }

            var pane = new TerminalPane(profile);
            pane.RequestSftpTransfer += (srcPane, dir, kind) => _ = InitiateSftpTransfer(srcPane, dir, kind);

            pane.ApplySettings(_settings);
            var tabItem = new TabItem
            {
                Header = new TextBlock { Text = profile.Name, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Padding = new Thickness(10, 4) },
                Content = pane
            };
            tabs.Items.Add(tabItem);
            tabs.SelectedItem = tabItem;
            _currentPane = pane;

            // Defer visual update until layout is complete (ensures template is applied)
            EventHandler? layoutHandler = null;
            layoutHandler = (s, e) =>
            {
                tabItem.LayoutUpdated -= layoutHandler;
                Dispatcher.UIThread.Post(() => UpdateTabVisuals(), DispatcherPriority.Input);
            };
            tabItem.LayoutUpdated += layoutHandler;

            // Fallback: Post anyway
            Dispatcher.UIThread.Post(() => UpdateTabVisuals(), DispatcherPriority.Input);
            Dispatcher.UIThread.Post(() => pane.ActiveControl.Focus());
        }

        private void PopulateNewTabMenu()
        {
            var btnNewTab = this.FindControl<Button>("BtnNewTab");
            var flyout = btnNewTab?.Flyout as MenuFlyout;
            if (flyout == null) return;

            // Clear dynamic items (everything before the separator)
            // Note: Simplest way is to rebuild the Flyout menu items list
            var items = new Avalonia.Controls.ItemsControl().Items; // Temporary collection

            int separatorIndex = -1;
            for (int i = 0; i < flyout.Items.Count; i++)
            {
                if (flyout.Items[i] is Separator) { separatorIndex = i; break; }
            }

            // Keep only the separator and Manage Profiles
            var footerItems = new System.Collections.Generic.List<object>();
            if (separatorIndex != -1)
            {
                for (int i = separatorIndex; i < flyout.Items.Count; i++)
                {
                    var footer = flyout.Items[i];
                    if (footer != null) footerItems.Add(footer);
                }
            }

            flyout.Items.Clear();

            // Add profiles
            foreach (var profile in _settings.Profiles)
            {
                // UI Polish: Show all profiles the user has configured.
                // Previously we hid "invalid" ones, but that hides imported WSL profiles if not found in path.
                // Let the user see and fix them if broken.

                var item = new MenuItem { Header = profile.Name };
                item.Click += (s, e) => AddTab(profile);
                flyout.Items.Add(item);
            }

            // Add footer back
            foreach (var footer in footerItems)
                flyout.Items.Add(footer);
        }

        internal void UpdateTabVisuals()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var theme = _settings.ActiveTheme;
            var borderBrush = new SolidColorBrush(theme.Blue.ToAvaloniaColor());

            // Calculate contrasting foreground for tabs
            double luminance = (0.299 * theme.Background.R + 0.587 * theme.Background.G + 0.114 * theme.Background.B) / 255.0;
            var contrastForeground = luminance > 0.5 ? Brushes.Black : Brushes.White;

            foreach (TabItem ti in tabs.Items.Cast<TabItem>())
            {
                var border = ti.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Border");
                if (border != null)
                {
                    // Let Style handle Background (e.g. #33FFFFFF on select)
                    // border.Background = Brushes.Transparent; 
                    border.BorderBrush = ti.IsSelected ? borderBrush : Brushes.Transparent;
                }

                if (ti.Header is TextBlock tb)
                {
                    tb.Foreground = contrastForeground;

                    if (ti.Content is TerminalPane pane && pane.Profile != null)
                    {
                        string profileName = pane.Profile.Name;
                        var forwards = pane.Profile.Forwards;
                        int activeCount = forwards.Count(f => f.Status == ForwardingStatus.Active);
                        int startingCount = forwards.Count(f => f.Status == ForwardingStatus.Starting);
                        bool hasFailed = forwards.Any(f => f.Status == ForwardingStatus.Failed);

                        if (activeCount > 0 || startingCount > 0)
                        {
                            string badge = activeCount.ToString();
                            if (startingCount > 0) badge += $" ({startingCount})";
                            tb.Text = $"{profileName} 🔁 {badge}";
                        }
                        else if (hasFailed)
                        {
                            tb.Text = $"{profileName} ⚠️";
                        }
                        else
                        {
                            tb.Text = profileName;
                        }
                    }
                }
            }
        }

        private void SplitPane(Avalonia.Layout.Orientation orientation)
        {
            if (_currentPane == null) return;
            var originalPane = _currentPane;
            var parent = originalPane.Parent as Panel;

            // CAPTURE coordinates before we reset them for the new nested grid!
            int oldRow = Grid.GetRow(originalPane);
            int oldCol = Grid.GetColumn(originalPane);

            TerminalPane newPane;
            if (originalPane.Profile != null)
            {
                // Create a copy of the profile for the new split pane
                var profile = originalPane.Profile;
                newPane = new TerminalPane(profile);
            }
            else
            {
                newPane = new TerminalPane(originalPane.ShellCommand);
            }

            newPane.ApplySettings(_settings);
            var grid = new Grid { Background = Brushes.Transparent, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch };

            var dividerBrush = new SolidColorBrush(Color.FromRgb(35, 35, 35)); // Even more subtle

            if (orientation == Avalonia.Layout.Orientation.Horizontal)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel)); // 3px hit area
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

                var splitter = new GridSplitter
                {
                    Width = 3,
                    Background = dividerBrush,
                    ResizeDirection = GridResizeDirection.Columns,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                    Focusable = false
                };
                Grid.SetColumn(splitter, 1);
                grid.Children.Add(splitter);

                Grid.SetRow(originalPane, 0); Grid.SetColumn(originalPane, 0);
                Grid.SetRow(newPane, 0); Grid.SetColumn(newPane, 2);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
                grid.RowDefinitions.Add(new RowDefinition(3, GridUnitType.Pixel)); // 3px hit area
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

                var splitter = new GridSplitter
                {
                    Height = 3,
                    Background = dividerBrush,
                    ResizeDirection = GridResizeDirection.Rows,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    Focusable = false
                };
                Grid.SetRow(splitter, 1);
                grid.Children.Add(splitter);

                Grid.SetRow(originalPane, 0); Grid.SetColumn(originalPane, 0);
                Grid.SetRow(newPane, 2); Grid.SetColumn(newPane, 0);
            }

            if (parent != null)
            {
                Grid.SetRow(grid, oldRow);
                Grid.SetColumn(grid, oldCol);
                int index = parent.Children.IndexOf(originalPane);
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, grid);
                grid.Children.Add(originalPane);
                grid.Children.Add(newPane);
            }
            else if (originalPane.Parent is ContentPresenter cp)
            {
                cp.Content = grid;
                grid.Children.Add(originalPane);
                grid.Children.Add(newPane);
            }
            else if (originalPane.Parent is TabItem tab)
            {
                tab.Content = grid;
                grid.Children.Add(originalPane);
                grid.Children.Add(newPane);
            }
            _currentPane = newPane;
            Dispatcher.UIThread.Post(() => { newPane.ActiveControl.Focus(); this.InvalidateMeasure(); this.InvalidateArrange(); }, DispatcherPriority.Loaded);
        }

        private async Task PasteFromClipboardAsync()
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
#pragma warning disable CS0618
                if (topLevel?.Clipboard != null)
                {
                    var text = await topLevel.Clipboard.GetTextAsync();
                    if (!string.IsNullOrEmpty(text) && _currentPane?.Session != null)
                    {
                        // Handle Bracketed Paste Mode
                        if (_currentPane.Buffer != null && _currentPane.Buffer.Modes.IsBracketedPasteMode)
                        {
                            text = $"\x1b[200~{text}\x1b[201~";
                        }

                        _currentPane.Session.SendInput(text);
                    }
                }
#pragma warning restore CS0618
            }
            catch { }
        }

        private void ApplyThemeToUI()
        {
            var theme = _settings.ActiveTheme;

            // Background brush for the main content area
            var bgBrush = new SolidColorBrush(theme.Background.ToAvaloniaColor(), _settings.WindowOpacity);

            // Header/TitleBar brush (slightly darker/different to provide contrast)
            var themeBg = theme.Background.ToAvaloniaColor();
            var headerBg = Color.FromRgb(
                (byte)Math.Max(0, themeBg.R - 10),
                (byte)Math.Max(0, themeBg.G - 10),
                (byte)Math.Max(0, themeBg.B - 10));

            // Use slightly higher opacity for the header to keep buttons visible
            var titleBarOpacity = Math.Min(1.0, _settings.WindowOpacity + 0.1);
            var headerBrush = new SolidColorBrush(headerBg, titleBarOpacity);

            this.Background = Brushes.Transparent;
            var bgGrid = this.FindControl<Grid>("WindowBackground");
            if (bgGrid != null)
            {
                bgGrid.Background = bgBrush;
                if (!string.IsNullOrEmpty(_settings.BackgroundImagePath) && System.IO.File.Exists(_settings.BackgroundImagePath))
                {
                    try
                    {
                        var bitmap = new Avalonia.Media.Imaging.Bitmap(_settings.BackgroundImagePath);
                        bgGrid.Background = new ImageBrush(bitmap)
                        {
                            Stretch = Enum.TryParse<Stretch>(_settings.BackgroundImageStretch, out var s) ? s : Stretch.UniformToFill,
                            Opacity = _settings.BackgroundImageOpacity
                        };
                    }
                    catch { }
                }
            }

            var contrastColor = theme.GetContrastForeground();
            var contrastForeground = new SolidColorBrush(contrastColor.ToAvaloniaColor());

            // Set the window theme variant to ensure OS caption buttons (Min/Max/Close)
            // adapt to the background brightness (Dark background -> Light buttons, Light background -> Dark buttons).
            this.RequestedThemeVariant = contrastColor == TermColor.Black ? ThemeVariant.Light : ThemeVariant.Dark;

            // Apply to Window Foreground (inherited by many controls)
            this.Foreground = contrastForeground;

            // Apply to Title Bar Buttons
            var btnNew = this.FindControl<Button>("BtnNewTab");
            var btnSettings = this.FindControl<Button>("SettingsBtn");
            var btnConns = this.FindControl<Button>("BtnConnections");

            if (btnNew != null) btnNew.Foreground = contrastForeground;
            if (btnSettings != null) btnSettings.Foreground = contrastForeground;
            if (btnConns != null) btnConns.Foreground = contrastForeground;

            // Force update of tab borders (blue line) since theme color changed
            UpdateTabVisuals();

            var titleBar = this.FindControl<Grid>("TitleBar");
            if (titleBar != null) titleBar.Background = Brushes.Transparent;

            var dragBorder = this.FindControl<Border>("DragBorder");
            if (dragBorder != null)
            {
                dragBorder.Background = headerBrush;
            }

            var connManager = this.FindControl<NovaTerminal.Controls.ConnectionManager>("ConnectionManagerControl");
            if (connManager != null)
            {
                connManager.ApplyTheme(theme);
            }

            var connTitleBar = this.FindControl<Grid>("ConnectionTitleBar");
            var connTitleText = this.FindControl<TextBlock>("ConnectionTitleText");
            var btnCloseConn = this.FindControl<Button>("BtnCloseConnections");

            var themeBgColor = theme.Background.ToAvaloniaColor();
            if (connTitleBar != null) connTitleBar.Background = new SolidColorBrush(themeBgColor.R < 127 ?
                Color.FromRgb((byte)(themeBgColor.R + 20), (byte)(themeBgColor.G + 20), (byte)(themeBgColor.B + 20)) :
                Color.FromRgb((byte)Math.Max(0, themeBgColor.R - 20), (byte)Math.Max(0, themeBgColor.G - 20), (byte)Math.Max(0, themeBgColor.B - 20)));

            if (connTitleText != null) connTitleText.Foreground = contrastForeground;
            if (btnCloseConn != null) btnCloseConn.Foreground = contrastForeground;

            var connOverlay = this.FindControl<Border>("ConnectionOverlay");
            if (connOverlay != null) connOverlay.Background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
        }

        private void SetupCommandPalette()
        {
            CommandRegistry.Clear();

            // 1. Register Default Commands
            CommandRegistry.Register("New Tab", "General", () => AddTab(), "Ctrl+Shift+T");

            // Dynamic Profile Tabs
            if (_settings.Profiles != null)
            {
                foreach (var profile in _settings.Profiles)
                {
                    // UI Polish: Only register commands for profiles that make sense for the current platform
                    if (profile.Type == ConnectionType.Local)
                    {
                        bool exists = File.Exists(profile.Command) || ShellHelper.InPath(profile.Command);
                        if (!exists) continue;
                    }
                    CommandRegistry.Register($"New Tab: {profile.Name}", "Shell", () => AddTab(profile), "");
                }
            }

            CommandRegistry.Register("Close Pane", "General", () => CloseActivePane(), "Ctrl+Shift+W");
            CommandRegistry.Register("Split Vertical", "View", () => SplitPane(Avalonia.Layout.Orientation.Vertical), "Ctrl+Shift+D");
            CommandRegistry.Register("Split Horizontal", "View", () => SplitPane(Avalonia.Layout.Orientation.Horizontal), "Ctrl+Shift+E");
            CommandRegistry.Register("Find in Terminal", "Edit", () => _currentPane?.ToggleSearch(), "Ctrl+Shift+F");
            CommandRegistry.Register("Paste", "Edit", () => _ = PasteFromClipboardAsync(), "Ctrl+V");
            CommandRegistry.Register("Settings", "General", async () =>
            {
                await OpenSettings(0);
            }, "");

            // SFTP Actions
            CommandRegistry.Register("SFTP: Upload File...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Upload, TransferKind.File), "");
            CommandRegistry.Register("SFTP: Upload Folder...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Upload, TransferKind.Folder), "");
            CommandRegistry.Register("SFTP: Download File...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Download, TransferKind.File), "");
            CommandRegistry.Register("SFTP: Download Folder...", "Remote", () => _ = InitiateSftpTransfer(null, TransferDirection.Download, TransferKind.Folder), "");
            CommandRegistry.Register("SFTP: Show Transfers", "Remote", () => ToggleTransferCenter(), "");

            // Themes
            CommandRegistry.Register("Theme: Solarized Dark", "Theme", () => { _settings.ThemeName = "Solarized Dark"; ApplyThemeToUI(); ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Theme: Default Dark", "Theme", () => { _settings.ThemeName = "Default (Dark)"; ApplyThemeToUI(); ApplySettingsToAllTabs(); _settings.Save(); }, "");
        }

        private async Task InitiateSftpTransfer(TerminalPane? explicitPane, TransferDirection direction, TransferKind kind)
        {
            var pane = explicitPane ?? _currentPane;
            if (pane == null || pane.Profile == null || pane.Profile.Type != ConnectionType.SSH)
            {
                // Only for SSH sessions
                return;
            }

            var profile = pane.Profile;
            var sessionId = pane.Session?.Id ?? Guid.Empty;

            string? localPath = null;
            string? remotePath = null;

            if (direction == TransferDirection.Upload)
            {
                // File Picker
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                if (kind == TransferKind.File)
                {
                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select File to Upload", AllowMultiple = false });
                    if (files != null && files.Count > 0) localPath = files[0].Path.LocalPath;
                }
                else
                {
                    var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Folder to Upload", AllowMultiple = false });
                    if (folders != null && folders.Count > 0) localPath = folders[0].Path.LocalPath;
                }

                if (string.IsNullOrEmpty(localPath)) return;

                remotePath = await PromptForRemotePathAsync("Remote Destination Path", profile!.DefaultRemoteDir ?? "~");
            }
            else
            {
                // Download
                remotePath = await PromptForRemotePathAsync("Remote Source Path", profile!.DefaultRemoteDir ?? "~");
                if (string.IsNullOrEmpty(remotePath)) return;

                // Folder/File Picker for destination
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                if (kind == TransferKind.File)
                {
                    var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Select Local Destination", SuggestedFileName = Path.GetFileName(remotePath) });
                    if (file != null) localPath = file.Path.LocalPath;
                }
                else
                {
                    var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Local Destination Folder", AllowMultiple = false });
                    if (folders != null && folders.Count > 0) localPath = folders[0].Path.LocalPath;
                }
            }

            if (!string.IsNullOrEmpty(localPath) && !string.IsNullOrEmpty(remotePath))
            {
                var job = new TransferJob
                {
                    SessionId = sessionId,
                    ProfileName = profile.Name,
                    Direction = direction,
                    Kind = kind,
                    LocalPath = localPath,
                    RemotePath = remotePath
                };
                SftpService.Instance.AddJob(job);
            }
        }

        private TaskCompletionSource<string?>? _pathPromptTcs;
        private async Task<string?> PromptForRemotePathAsync(string title, string defaultValue)
        {
            var overlay = this.FindControl<Grid>("PathPromptOverlay");
            var titleBlock = this.FindControl<TextBlock>("PathPromptTitle");
            var box = this.FindControl<TextBox>("PathPromptBox");
            var btnConfirm = this.FindControl<Button>("BtnPathConfirm");
            var btnCancel = this.FindControl<Button>("BtnPathCancel");

            if (overlay == null || box == null || titleBlock == null || btnConfirm == null || btnCancel == null) return null;

            titleBlock.Text = title;
            box.Text = defaultValue;
            overlay.IsVisible = true;
            box.Focus();

            _pathPromptTcs = new TaskCompletionSource<string?>();

            EventHandler<Avalonia.Interactivity.RoutedEventArgs>? confirmHandler = null;
            EventHandler<Avalonia.Interactivity.RoutedEventArgs>? cancelHandler = null;

            confirmHandler = (s, e) =>
            {
                overlay.IsVisible = false;
                _pathPromptTcs.TrySetResult(box.Text);
            };

            cancelHandler = (s, e) =>
            {
                overlay.IsVisible = false;
                _pathPromptTcs.TrySetResult(null);
            };

            btnConfirm.Click += confirmHandler;
            btnCancel.Click += cancelHandler;

            try
            {
                return await _pathPromptTcs.Task;
            }
            finally
            {
                btnConfirm.Click -= confirmHandler;
                btnCancel.Click -= cancelHandler;
            }
        }

        private void InitializeCommandPaletteUI()
        {
            // 2. Wire UI (Wires only ONCE)
            var overlay = this.FindControl<Grid>("CommandPaletteOverlay");
            var box = this.FindControl<TextBox>("CommandSearchBox");
            var list = this.FindControl<ListBox>("CommandList");

            if (overlay != null)
            {
                overlay.PointerPressed += (s, e) =>
                {
                    // If we clicked the background overlay itself (not the border dialog), close it
                    if (e.Source == overlay) ToggleCommandPalette();
                };
            }

            if (box != null && list != null)
            {
                box.KeyUp += (s, e) =>
                {
                    if (e.Key == Key.Down)
                    {
                        list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1);
                        list.ScrollIntoView(list.SelectedIndex);
                    }
                    else if (e.Key == Key.Up)
                    {
                        list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                        list.ScrollIntoView(list.SelectedIndex);
                    }
                    else if (e.Key == Key.Enter)
                    {
                        if (list.SelectedItem is TerminalCommand cmd)
                        {
                            ExecuteCommand(cmd);
                        }
                    }
                    else if (e.Key == Key.Escape)
                    {
                        ToggleCommandPalette();
                    }
                    else
                    {
                        var results = CommandRegistry.Search(box.Text ?? "");
                        list.ItemsSource = results;
                        if (results.Count > 0) list.SelectedIndex = 0;
                    }
                };

                // Filter on text changed too for smoother feel
                box.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == "Text")
                    {
                        var results = CommandRegistry.Search(box.Text ?? "");
                        list.ItemsSource = results;
                        if (results.Count > 0) list.SelectedIndex = 0;
                    }
                };
            }

            if (list != null)
            {
                list.DoubleTapped += (s, e) =>
                {
                    if (list.SelectedItem is TerminalCommand cmd) ExecuteCommand(cmd);
                };
            }
        }

        private void ToggleCommandPalette()
        {
            var overlay = this.FindControl<Grid>("CommandPaletteOverlay");
            var box = this.FindControl<TextBox>("CommandSearchBox");
            var list = this.FindControl<ListBox>("CommandList");

            if (overlay == null) return;

            bool isVisible = overlay.IsVisible;
            overlay.IsVisible = !isVisible;

            if (!isVisible)
            {
                // Opening
                if (box != null && list != null)
                {
                    box.Text = "";
                    list.ItemsSource = CommandRegistry.GetCommands().OrderBy(c => c.Category).ThenBy(c => c.Title).ToList();
                    list.SelectedIndex = 0;
                    box.Focus();
                }
            }
            else
            {
                // Closing - return focus to terminal
                _currentPane?.ActiveControl.Focus();
            }
        }

        private void ToggleTransferCenter()
        {
            var overlay = this.FindControl<Border>("TransferOverlay");
            if (overlay != null)
            {
                overlay.IsVisible = !overlay.IsVisible;
            }
        }

        private void InitializeTransferCenterUI()
        {
            var btnClose = this.FindControl<Button>("BtnCloseTransfers");
            if (btnClose != null) btnClose.Click += (s, e) => ToggleTransferCenter();
        }

        private async Task OpenSettings(int tabIndex, Guid? profileId = null)
        {
            var sw = new SettingsWindow(tabIndex, profileId);

            // Wire up live preview events
            sw.OnOpacityChanged += (val) => { _settings.WindowOpacity = val; ApplyThemeToUI(); ApplySettingsToAllTabs(); };
            sw.OnBlurChanged += (val) => { _settings.BlurEffect = val; UpdateTransparencyHints(); };
            sw.OnBgImageChanged += (path, opacity, stretch) =>
            {
                _settings.BackgroundImagePath = path;
                _settings.BackgroundImageOpacity = opacity;
                _settings.BackgroundImageStretch = stretch;
                ApplyThemeToUI();
                ApplySettingsToAllTabs();
            };
            sw.OnFontChanged += (font) => { _settings.FontFamily = font; ApplySettingsToAllTabs(); };
            sw.OnFontSizeChanged += (size) => { _settings.FontSize = size; ApplySettingsToAllTabs(); };
            sw.OnThemeChanged += (theme) =>
        {
            // Force reload themes to pick up any changes from settings window
            _settings.ThemeManager.ReloadThemes();
            _settings.RefreshActiveTheme();
            _settings.ThemeName = theme;
            ApplyThemeToUI();
            ApplySettingsToAllTabs();
        };

            bool saved = await sw.ShowDialog<bool>(this);

            if (saved)
            {
                // Use the settings object directly from the dialog to avoid disk I/O race conditions
                if (sw.Settings != null)
                {
                    _settings = sw.Settings;
                }
                else
                {
                    _settings = TerminalSettings.Load();
                }

                RefreshProfileUIs();
                ApplyThemeToUI();
                ApplySettingsToAllTabs();
                UpdateTransparencyHints();

                // Refresh Connection Manager if open (or just always update it)
                var connManager = this.FindControl<NovaTerminal.Controls.ConnectionManager>("ConnectionManagerControl");
                if (connManager != null)
                {
                    connManager.LoadProfiles(_settings.Profiles);
                }
            }
        }

        private void RefreshProfileUIs()
        {
            PopulateNewTabMenu();
            SetupCommandPalette();

            // Refresh Connection Manager if open (or just always update it)
            var connManager = this.FindControl<NovaTerminal.Controls.ConnectionManager>("ConnectionManagerControl");
            if (connManager != null)
            {
                connManager.LoadProfiles(_settings.Profiles);
            }
        }

        private void ExecuteCommand(TerminalCommand cmd)
        {
            ToggleCommandPalette(); // Close first
            try
            {
                cmd.Action?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing command {cmd.Title}: {ex.Message}");
            }
        }


        private void UpdateTransparencyHints()
        {
            var hints = new List<WindowTransparencyLevel>();
            switch (_settings.BlurEffect)
            {
                case "Mica": hints.Add(WindowTransparencyLevel.Mica); break;
                case "Acrylic": case "Blur": hints.Add(WindowTransparencyLevel.AcrylicBlur); hints.Add(WindowTransparencyLevel.Blur); break;
                case "None": hints.Add(WindowTransparencyLevel.Transparent); break;
                default: hints.Add(WindowTransparencyLevel.AcrylicBlur); break;
            }
            this.TransparencyLevelHint = hints;
        }
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                SessionManager.SaveSession(this, tabs);
            }
            _globalHotkey?.Dispose();
        }
    }
}
