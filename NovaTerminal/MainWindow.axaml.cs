using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using NovaTerminal.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NovaTerminal
{
    public partial class MainWindow : Window
    {
        // Simple storage for active sessions
        // In a real VM pattern, we'd bind TabControl.Items to ObservableCollection.
        // For simplicity, we manipulate TabControl.Items directly.
        
        private class TabContext
        {
            public ConPtySession Session { get; set; }
            public TerminalBuffer Buffer { get; set; }
            public AnsiParser Parser { get; set; }
            public TerminalView View { get; set; }
            
            public TabContext(string shell) 
            {
                 // Match the environment variable defaults (120x30)
                 Buffer = new TerminalBuffer(120, 30);
                 Parser = new AnsiParser(Buffer);
                 View = new TerminalView();
                 View.SetBuffer(Buffer);
                 Session = new ConPtySession(shell);
            }
        }

        private TabContext? _currentContext;
        private TerminalSettings _settings;

        public MainWindow()
        {
            InitializeComponent();
            _settings = TerminalSettings.Load();

            var tabs = this.FindControl<TabControl>("Tabs");
            var btnNew = this.FindControl<Button>("BtnNewTab");
            var settingsBtn = this.FindControl<Button>("SettingsBtn");
            
            // Set initial tab colors immediately
            if (tabs != null)
            {
                tabs.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(20, 20, 20));
                tabs.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White);
            }

            if (settingsBtn != null) settingsBtn.Click += async (s, e) => 
            {
                var sw = new SettingsWindow();
                var result = await sw.ShowDialog<bool>(this);
                if (result)
                {
                    _settings = TerminalSettings.Load();
                    ApplyThemeToUI();
                    ApplySettingsToAllTabs();
                }
            };
            
            // Helper to broadcast settings
            void ApplySettingsToAllTabs()
            {
                var tabs = this.FindControl<TabControl>("Tabs");
                if (tabs != null)
                {
                    foreach (TabItem ti in tabs.Items.Cast<TabItem>())
                    {
                        if (ti.Tag is TabContext ctx)
                        {
                            ctx.View.ApplySettings(_settings);
                            ctx.View.InvalidateVisual();
                            ctx.Buffer.Invalidate();
                        }
                    }
                }
            }

            ApplyThemeToUI();
            
            // Menu Items
            var menuCmd = this.FindControl<MenuItem>("MenuCmd");
            var menuPs = this.FindControl<MenuItem>("MenuPs");
            var menuWsl = this.FindControl<MenuItem>("MenuWsl");

            if (menuCmd != null) menuCmd.Click += (s, e) => AddTab("cmd.exe");
            if (menuPs != null) menuPs.Click += (s, e) => AddTab("powershell.exe");
            if (menuWsl != null) menuWsl.Click += (s, e) => AddTab("wsl.exe");
            
            // Search UI controls
            var searchPanel = this.FindControl<Border>("SearchPanel");
            var searchBox = this.FindControl<TextBox>("SearchBox");
            var searchPrev = this.FindControl<Button>("SearchPrev");
            var searchNext = this.FindControl<Button>("SearchNext");
            var searchClose = this.FindControl<Button>("SearchClose");
            var searchCount = this.FindControl<TextBlock>("SearchCount");

            if (searchBox != null)
            {
                searchBox.TextChanged += (s, e) => 
                {
                    if (_currentContext != null)
                        _currentContext.View.Search(searchBox.Text ?? "");
                };
            }

            if (searchPrev != null) searchPrev.Click += (s, e) => _currentContext?.View.PrevMatch();
            if (searchNext != null) searchNext.Click += (s, e) => _currentContext?.View.NextMatch();
            if (searchClose != null && searchPanel != null) 
            {
                searchClose.Click += (s, e) => 
                {
                    searchPanel.IsVisible = false;
                    _currentContext?.View.ClearSearch();
                    _currentContext?.View.Focus();
                };
            }

            if (tabs != null)
            {
                tabs.SelectionChanged += (s, e) => 
                {
                   if (tabs.SelectedItem is TabItem ti && ti.Tag is TabContext ctx)
                   {
                       _currentContext = ctx;
                       
                       // Hook new state event
                       ctx.View.SearchStateChanged -= UpdateSearchCountUI; // Ensure no double-hook
                       ctx.View.SearchStateChanged += UpdateSearchCountUI;

                       // Re-trigger search if panel is open to update the counter
                       if (searchPanel != null && searchPanel.IsVisible && searchBox != null)
                       {
                           _currentContext.View.Search(searchBox.Text ?? "");
                       }
                       else if (searchCount != null)
                       {
                           searchCount.Text = "0/0";
                       }

                       ctx.View.Focus();
                   }
                };
            }

            void UpdateSearchCountUI(int idx, int total)
            {
                if (searchCount != null)
                {
                    Dispatcher.UIThread.Post(() => searchCount.Text = $"{idx}/{total}");
                }
            }
            
            // Add initial tab
            AddTab();
            
            // Handle Global Input (sent to current tab)
            this.AddHandler(KeyDownEvent, (s, e) => 
            {
                var modifiers = e.KeyModifiers;
                bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
                bool isShift = (modifiers & KeyModifiers.Shift) != 0;

                // Zoom In (Ctrl + Plus/Equal)
                if (isCtrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
                {
                    if (_settings != null)
                    {
                        _settings.FontSize += 1;
                        if (_settings.FontSize > 72) _settings.FontSize = 72;
                        
                        ApplySettingsToAllTabs();
                        _settings.Save();
                    }
                    e.Handled = true;
                    return;
                }

                // Zoom Out (Ctrl + Minus/Subtract)
                if (isCtrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
                {
                    if (_settings != null)
                    {
                        _settings.FontSize -= 1;
                        if (_settings.FontSize < 6) _settings.FontSize = 6;
                        
                        ApplySettingsToAllTabs();
                        _settings.Save();
                    }
                    e.Handled = true;
                    return;
                }

                // New Tab (Ctrl + Shift + T)
                if (isCtrl && isShift && e.Key == Key.T)
                {
                    AddTab();
                    e.Handled = true;
                    return;
                }

                // Close Tab (Ctrl + Shift + W)
                if (isCtrl && isShift && e.Key == Key.W)
                {
                    if (tabs != null && tabs.SelectedItem is TabItem ti)
                    {
                        CloseTab(ti);
                    }
                    e.Handled = true;
                    return;
                }

                // Toggle Search (Ctrl + Shift + F) matches README
                // Also keeping Ctrl+F as a convenient alias
                if ((isCtrl && isShift && e.Key == Key.F) || (isCtrl && e.Key == Key.F))
                {
                    if (searchPanel != null)
                    {
                        searchPanel.IsVisible = !searchPanel.IsVisible;
                        if (searchPanel.IsVisible) 
                        {
                            searchBox?.Focus();
                            if (searchBox != null && !string.IsNullOrEmpty(searchBox.Text))
                                _currentContext?.View.Search(searchBox.Text);
                        }
                        else
                        {
                            _currentContext?.View.ClearSearch();
                            _currentContext?.View.Focus();
                        }
                    }
                    e.Handled = true;
                    return;
                }
                
                // Tab Switching (Ctrl + Tab / Ctrl + Shift + Tab)
                if (isCtrl && e.Key == Key.Tab && tabs != null)
                {
                     int count = tabs.Items.Count;
                     if (count > 1)
                     {
                         int current = tabs.SelectedIndex;
                         if (isShift)
                         {
                             // Previous
                             current--;
                             if (current < 0) current = count - 1;
                         }
                         else
                         {
                             // Next
                             current++;
                             if (current >= count) current = 0;
                         }
                         tabs.SelectedIndex = current;
                     }
                     e.Handled = true;
                     return;
                }

                // Close search on Escape
                if (e.Key == Key.Escape && searchPanel != null && searchPanel.IsVisible)
                {
                    searchPanel.IsVisible = false;
                    _currentContext?.View.ClearSearch();
                    _currentContext?.View.Focus();
                    e.Handled = true;
                    return;
                }

                // If search has focus, let search handle it
                if (searchBox != null && searchBox.IsFocused) return;

                // Otherwise, it's terminal input
                OnKeyDown(s, e);
            }, RoutingStrategies.Tunnel);

            this.TextInput += (s, e) => 
            {
                if (searchBox != null && searchBox.IsFocused) return;
                OnTextInput(s, e);
            };

            // Initialize Vault
            try
            {
                Vault = new VaultService();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VAULT] Failed to initialize: {ex.Message}");
            }
        }

        public static VaultService? Vault { get; private set; }

        private void CloseTab(TabItem ti)
        {
            if (ti.Tag is TabContext ctx)
            {
                // Offload disposal to background thread to prevent UI freeze from native cleanup
                Task.Run(() => 
                {
                    try 
                    {
                        ctx.Session?.Dispose(); 
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error disposing session: {ex.Message}");
                    }
                });
            }
            
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                tabs.Items.Remove(ti);
            }
        }

        // Public for access from menu/shortcuts
        void AddTab(string shell = "cmd.exe")
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var ctx = new TabContext(shell);
            ctx.View.ApplySettings(_settings);
            
            // Wire up output
            ctx.Session.OnOutputReceived += text => 
            {
                // Dispatch to UI for this specific tab
                Dispatcher.UIThread.InvokeAsync(() => {
                    ctx.Parser.Process(text);
                });
            };

            var tabHeaderText = new TextBlock
            {
                Text = shell,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White),
                FontSize = 14,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            
            var tabItem = new TabItem
            {
                Header = tabHeaderText,  // Use TextBlock directly instead of string
                Tag = ctx,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(32, 32, 32)),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White),
                IsVisible = true,
                Opacity = 1.0
            };
            
            // Create Grid with ScrollBar (Overlay Mode)
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            
            var scrollBar = new ScrollBar
            {
                Orientation = Avalonia.Layout.Orientation.Vertical,
                AllowAutoHide = true,
                Minimum = 0,
                Maximum = 0,
                ViewportSize = 20,
                Visibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Width = 10,  // Thin
                Opacity = 0.6 // Semi-transparent
            };
            
            grid.Children.Add(ctx.View); // Layer 0
            
            grid.Children.Add(scrollBar); // Layer 1 (Top)
            // No column setting needed (default 0)
            
            tabItem.Content = grid;
            
            // Scroll Logic
            bool isUpdatingScroll = false;
            
            // UI -> View
            scrollBar.ValueChanged += (s, e) => 
            {
                if (isUpdatingScroll) return;
                // Invert logic: ScrollBar Top (0) -> History Top (Max Offset)
                // ScrollBar Bottom (Max) -> History Bottom (0)
                int newOffset = (int)(scrollBar.Maximum - e.NewValue);
                ctx.View.SetScrollOffset(newOffset);
            };
            
            // View -> UI
            ctx.View.ScrollStateChanged += (offset, totalLines) => 
            {
                isUpdatingScroll = true;
                try
                {
                    int rows = ctx.Buffer.Rows;
                    int maxScroll = Math.Max(0, totalLines - rows);
                    
                    scrollBar.Maximum = maxScroll;
                    scrollBar.ViewportSize = rows;
                    scrollBar.LargeChange = rows;
                    scrollBar.SmallChange = 3;
                    
                    // Invert
                    scrollBar.Value = maxScroll - offset;
                }
                finally
                {
                    isUpdatingScroll = false;
                }
            };
            
            tabs.Items.Add(tabItem);
            tabs.SelectedItem = tabItem;
            _currentContext = ctx;
            
            // Force visual invalidation to ensure tab renders properly with GPU acceleration
            Dispatcher.UIThread.Post(() =>
            {
                tabItem.InvalidateVisual();
                tabItem.InvalidateMeasure();
                tabs.InvalidateVisual();
            }, DispatcherPriority.Render);
            
            // Explicitly force focus to the view (will attach visual tree)
            ctx.View.Focus();

            // Defer Start until View measures itself and fires OnReady
            // This ensures we pass the CORRECT columns/rows to the shell environment.
            ctx.View.OnReady += () => 
            {
                 // Start Session with actual measured size
                 _ = ctx.Session.StartAsync(ctx.Buffer.Cols, ctx.Buffer.Rows);
            };
            
             // Wire up resize events to ConPTY
            ctx.View.OnResize += (cols, rows) => 
            {
                ctx.Session.Resize(cols, rows);
            };
            
            // Wire up buffer invalidate to view - THIS IS CRITICAL FOR RENDERING UPDATES
            ctx.Buffer.OnInvalidate += () =>
            {
                ctx.View.InvalidateBuffer();
            };

            // Wire up session for mouse event forwarding
            ctx.View.SetBuffer(ctx.Buffer);
            ctx.View.SetSession(ctx.Session);
        }

        private void OnTextInput(object? sender, TextInputEventArgs e)
        {
            if (_currentContext == null) return;
            
            if (!string.IsNullOrEmpty(e.Text))
            {
               // Local echo removed; rely on shell echo
               // _currentContext.Parser.Process(e.Text); 
               _currentContext.Session.SendInput(e.Text);
               e.Handled = true;
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (_currentContext == null) return;

            string? sequence = null;

            switch (e.Key)
            {
                case Key.Enter:
                    _currentContext.Session.SendInput("\r");
                    e.Handled = true;
                    return;
                    
                case Key.Back:
                    // Send DEL (0x7F) instead of BS (0x08) for proper backspace behavior
                    _currentContext.Session.SendInput("\x7f");
                    e.Handled = true;
                    return;

                case Key.C:
                    // Ctrl+C: Copy if selection exists, otherwise send Ctrl+C to shell
                    if (e.KeyModifiers == KeyModifiers.Control)
                    {
                        if (_currentContext.View.HasSelection())
                        {
                            _ = _currentContext.View.CopySelectionToClipboard();
                            _currentContext.View.ClearSelection();
                            e.Handled = true;
                            return;
                        }
                        // If no selection, send Ctrl+C to shell (interrupt)
                        _currentContext.Session.SendInput("\x03");
                        e.Handled = true;
                        return;
                    }
                    break;

                case Key.V:
                    // Ctrl+V: Paste from clipboard
                    if (e.KeyModifiers == KeyModifiers.Control)
                    {
                        _ = PasteFromClipboardAsync();
                        e.Handled = true;
                        return;
                    }
                    break;
                    
                case Key.Tab:
                    _currentContext.Session.SendInput("\t");
                    e.Handled = true;
                    return;
                    
                // Arrow keys (VT100 sequences)
                case Key.Up:
                    sequence = "\x1b[A";
                    break;
                case Key.Down:
                    sequence = "\x1b[B";
                    break;
                case Key.Right:
                    sequence = "\x1b[C";
                    break;
                case Key.Left:
                    sequence = "\x1b[D";
                    break;
                    
                // Home/End
                case Key.Home:
                    sequence = "\x1b[H";
                    break;
                case Key.End:
                    sequence = "\x1b[F";
                    break;
                    
                // Page Up/Down
                case Key.PageUp:
                    sequence = "\x1b[5~";
                    break;
                case Key.PageDown:
                    sequence = "\x1b[6~";
                    break;
                    
                // Delete/Insert
                case Key.Delete:
                    sequence = "\x1b[3~";
                    break;
                case Key.Insert:
                    sequence = "\x1b[2~";
                    break;
                    
                // Function keys
                case Key.F1:
                    sequence = "\x1bOP";
                    break;
                case Key.F2:
                    sequence = "\x1bOQ";
                    break;
                case Key.F3:
                    sequence = "\x1bOR";
                    break;
                case Key.F4:
                    sequence = "\x1bOS";
                    break;
                case Key.F5:
                    sequence = "\x1b[15~";
                    break;
                case Key.F6:
                    sequence = "\x1b[17~";
                    break;
                case Key.F7:
                    sequence = "\x1b[18~";
                    break;
                case Key.F8:
                    sequence = "\x1b[19~";
                    break;
                case Key.F9:
                    sequence = "\x1b[20~";
                    break;
                case Key.F10:
                    sequence = "\x1b[21~";
                    break;
                case Key.F11:
                    sequence = "\x1b[23~";
                    break;
                case Key.F12:
                    sequence = "\x1b[24~";
                    break;
                    
                // Escape
                case Key.Escape:
                    sequence = "\x1b";
                    break;
            }

            if (sequence != null)
            {
                _currentContext.Session.SendInput(sequence);
                e.Handled = true;
            }
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
                    if (!string.IsNullOrEmpty(text) && _currentContext != null)
                    {
                        _currentContext.Session.SendInput(text);
                    }
                }
#pragma warning restore CS0618
            }
            catch
            {
                // Clipboard operations can fail silently
            }
        }

        private void ApplyThemeToUI()
        {
            var theme = (_settings.ThemeName == "Solarized Dark") ? TerminalTheme.SolarizedDark : TerminalTheme.Dark;
            var bgBrush = new Avalonia.Media.SolidColorBrush(theme.Background);
            var fgBrush = new Avalonia.Media.SolidColorBrush(theme.Foreground);
            
            // Slightly darker for headers
            var headerBg = Avalonia.Media.Color.FromArgb(255, 
                (byte)Math.Max(0, theme.Background.R - 15), 
                (byte)Math.Max(0, theme.Background.G - 15), 
                (byte)Math.Max(0, theme.Background.B - 15));
            var headerBrush = new Avalonia.Media.SolidColorBrush(headerBg);

            this.Background = bgBrush;
            
            // Force window-level invalidation to ensure complete redraw
            this.InvalidateVisual();
            this.InvalidateMeasure();
            this.InvalidateArrange();
            
            var mainRoot = this.FindControl<Grid>("MainRoot");
            if (mainRoot != null) 
            {
                mainRoot.Background = bgBrush;
                mainRoot.InvalidateVisual();
                mainRoot.InvalidateMeasure();
                mainRoot.InvalidateArrange();
            }

            // First child of MainRoot is the TabStrip (StackPanel)
            if (mainRoot != null && mainRoot.Children.Count > 0 && mainRoot.Children[0] is StackPanel sp)
            {
                sp.Background = headerBrush;
                sp.InvalidateVisual();
                sp.InvalidateMeasure();
                sp.InvalidateArrange();
            }

            var searchPanel = this.FindControl<Border>("SearchPanel");
            if (searchPanel != null) 
            {
                searchPanel.Background = headerBrush;
                searchPanel.InvalidateVisual();
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                // Now that XAML doesn't have hardcoded colors, we can set them dynamically
                tabs.Background = headerBrush;
                tabs.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White); // Keep white text for visibility
                tabs.InvalidateVisual();
                tabs.InvalidateMeasure();
                tabs.InvalidateArrange();
            }
            
            // Schedule a delayed second invalidation pass to ensure all visual updates are applied
            // This ensures the rendering completes even if the initial pass was optimized away
            Dispatcher.UIThread.Post(() =>
            {
                this.InvalidateVisual();
                mainRoot?.InvalidateVisual();
                if (mainRoot != null && mainRoot.Children.Count > 0 && mainRoot.Children[0] is StackPanel sp2)
                {
                    sp2.InvalidateVisual();
                }
                tabs?.InvalidateVisual();
            }, DispatcherPriority.Render);
        }
    }
}