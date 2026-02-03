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
                var sw = new SettingsWindow();
                sw.OnOpacityChanged += (val) =>
                {
                    _settings.WindowOpacity = val;
                    ApplyThemeToUI();
                    ApplySettingsToAllTabs();
                };
                sw.OnBlurChanged += (val) =>
                {
                    _settings.BlurEffect = val;
                    UpdateTransparencyHints();
                };
                sw.OnBgImageChanged += (path, opacity, stretch) =>
                {
                    _settings.BackgroundImagePath = path;
                    _settings.BackgroundImageOpacity = opacity;
                    _settings.BackgroundImageStretch = stretch;
                    ApplyThemeToUI();
                    ApplySettingsToAllTabs();
                };
                sw.OnFontChanged += (font) =>
                {
                    _settings.FontFamily = font;
                    ApplySettingsToAllTabs();
                };
                sw.OnFontSizeChanged += (size) =>
                {
                    _settings.FontSize = size;
                    ApplySettingsToAllTabs();
                };
                sw.OnThemeChanged += (theme) =>
                {
                    _settings.ThemeName = theme;
                    ApplyThemeToUI();
                    ApplySettingsToAllTabs();
                };
                await sw.ShowDialog<bool>(this);
                _settings = TerminalSettings.Load();
                ApplyThemeToUI();
                ApplySettingsToAllTabs();
                UpdateTransparencyHints();
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

            // Menu Items
            var menuCmd = this.FindControl<MenuItem>("MenuCmd");
            var menuPs = this.FindControl<MenuItem>("MenuPs");
            var menuWsl = this.FindControl<MenuItem>("MenuWsl");
            if (menuCmd != null) menuCmd.Click += (s, e) => AddTab("cmd.exe");
            if (menuPs != null) menuPs.Click += (s, e) => AddTab("powershell.exe");
            if (menuWsl != null) menuWsl.Click += (s, e) => AddTab("wsl.exe");

            // Global Focus Tracking
            this.AddHandler(GotFocusEvent, (s, e) =>
            {
                var pane = (e.Source as Control)?.FindAncestorOfType<TerminalPane>();
                if (pane != null)
                {
                    _currentPane = pane;
                }
            }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);

            AddTab();

            // Keyboard Shortcuts
            this.AddHandler(KeyDownEvent, (s, e) =>
            {
                var modifiers = e.KeyModifiers;
                bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
                bool isShift = (modifiers & KeyModifiers.Shift) != 0;

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
                    if (tabs?.SelectedItem is TabItem ti) CloseTab(ti);
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
            if (control is TerminalPane pane) pane.ApplySettings(settings);
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

        private void DisposeControlTree(Control control)
        {
            if (control is TerminalPane pane) Task.Run(() => { try { pane.Dispose(); } catch { } });
            else if (control is Panel panel) { foreach (var child in panel.Children) if (child is Control c) DisposeControlTree(c); }
            else if (control is ContentPresenter cp && cp.Content is Control childContent) DisposeControlTree(childContent);
        }

        void AddTab(string shell = "cmd.exe")
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;
            var pane = new TerminalPane(shell);
            pane.ApplySettings(_settings);
            var tabItem = new TabItem
            {
                Header = new TextBlock { Text = shell, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Padding = new Thickness(10, 4) },
                Content = pane
            };
            tabs.Items.Add(tabItem);
            tabs.SelectedItem = tabItem;
            _currentPane = pane;

            // Force visual update for the blue line (deferred so visual tree is ready)
            Dispatcher.UIThread.Post(() => UpdateTabVisuals(), DispatcherPriority.Render);
            Dispatcher.UIThread.Post(() => pane.ActiveControl.Focus());
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

            var newPane = new TerminalPane(originalPane.ShellCommand);
            newPane.ApplySettings(_settings);
            var grid = new Grid { Background = Brushes.Transparent, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch };

            var dividerBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)); // Subtle gray

            if (orientation == Avalonia.Layout.Orientation.Horizontal)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Pixel)); // 1px divider space
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

                var splitter = new GridSplitter
                {
                    Width = 1,
                    Background = dividerBrush,
                    ResizeDirection = GridResizeDirection.Columns
                };
                Grid.SetColumn(splitter, 1);
                grid.Children.Add(splitter);

                Grid.SetRow(originalPane, 0); Grid.SetColumn(originalPane, 0);
                Grid.SetRow(newPane, 0); Grid.SetColumn(newPane, 2);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Pixel)); // 1px divider space
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

                var splitter = new GridSplitter
                {
                    Height = 1,
                    Background = dividerBrush,
                    ResizeDirection = GridResizeDirection.Rows
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
            if (titleBar != null) titleBar.Background = headerBrush;

            var dragBorder = this.FindControl<Border>("DragBorder");
            if (dragBorder != null) dragBorder.Background = headerBrush;

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                // Ensure the tab headers area matches the title bar
            }

            UpdateTabVisuals();
            UpdateTransparencyHints();
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