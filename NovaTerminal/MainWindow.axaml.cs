using Avalonia.Controls;
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

using NovaTerminal.Controls;

namespace NovaTerminal
{
    public partial class MainWindow : Window
    {
        private TerminalPane? _currentPane;
        private TerminalSettings _settings;

        public MainWindow()
        {
            InitializeComponent();
            _settings = TerminalSettings.Load();

            var tabs = this.FindControl<TabControl>("Tabs");
            var btnNew = this.FindControl<Button>("BtnNewTab");
            var settingsBtn = this.FindControl<Button>("SettingsBtn");
            var titleBar = this.FindControl<Grid>("TitleBar");
            var dragBorder = this.FindControl<Border>("DragBorder");
            var btnNewTab = this.FindControl<Button>("BtnNewTab");

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
            AddTab(defaultProfile);

            SetupCommandPalette();
            InitializeCommandPaletteUI();

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
                string host = string.IsNullOrWhiteSpace(profile.SshHost) ? "localhost" : profile.SshHost;
                string user = string.IsNullOrWhiteSpace(profile.SshUser) ? "" : $"{profile.SshUser}@";
                string portString = profile.SshPort == 22 ? "" : $"-p {profile.SshPort} ";
                string key = string.IsNullOrWhiteSpace(profile.SshKeyPath) ? "" : $"-i \"{profile.SshKeyPath}\" ";

                profile.Command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ssh.exe" : "ssh";
                profile.Arguments = $"{portString}{key}{user}{host}";
            }

            var pane = new TerminalPane(profile);

            pane.ApplySettings(_settings);
            var tabItem = new TabItem
            {
                Header = new TextBlock { Text = profile.Name, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Padding = new Thickness(10, 4) },
                Content = pane
            };
            tabs.Items.Add(tabItem);
            tabs.SelectedItem = tabItem;
            _currentPane = pane;

            // Force visual update for the blue line (deferred so visual tree is ready)
            Dispatcher.UIThread.Post(() => UpdateTabVisuals(), DispatcherPriority.Render);
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
                    footerItems.Add(flyout.Items[i]);
            }

            flyout.Items.Clear();

            // Add profiles
            foreach (var profile in _settings.Profiles)
            {
                // UI Polish: Only show profiles that make sense for the current platform
                if (profile.Type == ConnectionType.Local)
                {
                    bool exists = File.Exists(profile.Command) || ShellHelper.InPath(profile.Command);
                    if (!exists) continue; // Skip "Bash" on Windows or "cmd.exe" on Linux
                }

                var item = new MenuItem { Header = profile.Name };
                item.Click += (s, e) => AddTab(profile);
                flyout.Items.Add(item);
            }

            // Add footer back
            foreach (var footer in footerItems)
                flyout.Items.Add(footer);
        }

        private void UpdateTabVisuals()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var theme = (_settings.ThemeName == "Solarized Dark") ? TerminalTheme.SolarizedDark : TerminalTheme.Dark;
            var borderBrush = new SolidColorBrush(theme.Blue);

            foreach (TabItem ti in tabs.Items.Cast<TabItem>())
            {
                var border = ti.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Border");
                if (border != null)
                {
                    border.Background = Brushes.Transparent;
                    border.BorderBrush = ti.IsSelected ? borderBrush : Brushes.Transparent;
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
                    if (!string.IsNullOrEmpty(text)) _currentPane?.Session?.SendInput(text);
                }
#pragma warning restore CS0618
            }
            catch { }
        }

        private void ApplyThemeToUI()
        {
            var theme = (_settings.ThemeName == "Solarized Dark") ? TerminalTheme.SolarizedDark : TerminalTheme.Dark;

            // Background brush for the main content area
            var bgBrush = new SolidColorBrush(theme.Background, _settings.WindowOpacity);

            // Header/TitleBar brush (slightly darker/different to provide contrast)
            var headerBg = Color.FromRgb(
                (byte)Math.Max(0, theme.Background.R - 10),
                (byte)Math.Max(0, theme.Background.G - 10),
                (byte)Math.Max(0, theme.Background.B - 10));

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

            var titleBar = this.FindControl<Grid>("TitleBar");
            if (titleBar != null) titleBar.Background = Brushes.Transparent;

            var dragBorder = this.FindControl<Border>("DragBorder");
            if (dragBorder != null) dragBorder.PointerPressed += (s, e) => BeginMoveDrag(e);

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

            // Themes
            CommandRegistry.Register("Theme: Solarized Dark", "Theme", () => { _settings.ThemeName = "Solarized Dark"; ApplyThemeToUI(); ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Theme: Default Dark", "Theme", () => { _settings.ThemeName = "Default (Dark)"; ApplyThemeToUI(); ApplySettingsToAllTabs(); _settings.Save(); }, "");
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
                        list.ScrollIntoView(list.SelectedItem);
                    }
                    else if (e.Key == Key.Up)
                    {
                        list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                        list.ScrollIntoView(list.SelectedItem);
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

        private async Task OpenSettings(int tabIndex)
        {
            var sw = new SettingsWindow(tabIndex);

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
            sw.OnThemeChanged += (theme) => { _settings.ThemeName = theme; ApplyThemeToUI(); ApplySettingsToAllTabs(); };

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

                PopulateNewTabMenu();
                SetupCommandPalette();
                ApplyThemeToUI();
                ApplySettingsToAllTabs();
                UpdateTransparencyHints();
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
    }
}