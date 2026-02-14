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
using Avalonia.Automation;
using SkiaSharp;

using NovaTerminal.Controls;

namespace NovaTerminal
{
    public partial class MainWindow : Window
    {
        private TerminalPane? _currentPane;
        private readonly Dictionary<TabItem, TerminalPane> _activePaneByTab = new();
        private readonly Dictionary<TabItem, PaneZoomState> _paneZoomStateByTab = new();
        private readonly Dictionary<TabItem, Guid> _zoomedPaneIdByTab = new();
        private readonly HashSet<TabItem> _broadcastEnabledTabs = new();
        private readonly Dictionary<TabItem, Guid> _tabIds = new();
        private readonly Dictionary<TabItem, PaneLayoutModel> _layoutModelByTab = new();
        private readonly List<TabItem> _tabMru = new();
        private readonly Dictionary<TabItem, TabRuntimeState> _tabStateByTab = new();
        private readonly HashSet<TabItem> _pendingVisualRefreshTabs = new();
        private bool _suppressMruTouchOnSelection;
        private bool _tabVisualRefreshScheduled;
        private TerminalSettings _settings;
        private GlobalHotkey? _globalHotkey;
        private bool _closePaneInProgress;
        private bool _closeTabInProgress;
        private static readonly TimeSpan BellDebounceWindow = TimeSpan.FromMilliseconds(750);

        private sealed class PaneZoomState
        {
            public required Control OriginalRoot { get; init; }
            public required Control Placeholder { get; init; }
        }

        private sealed class TabRuntimeState
        {
            public string? UserTitle { get; set; }
            public bool IsPinned { get; set; }
            public bool IsProtected { get; set; }
            public bool HasActivity { get; set; }
            public bool HasBell { get; set; }
            public DateTime LastBellUtc { get; set; }
            public int? LastExitCode { get; set; }
        }

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

            FocusCurrentTerminal(defer: true);
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
                    FocusCurrentTerminal(defer: true);
                }
            }
            else
            {
                // Hidden -> Show
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
                this.Focus();
                FocusCurrentTerminal(defer: true);
            }
        }

        private bool IsShortcut(KeyEventArgs e, string id, string fallback)
        {
            string binding = fallback;
            if (_settings.Keybindings != null &&
                _settings.Keybindings.TryGetValue(id, out var custom) &&
                !string.IsNullOrWhiteSpace(custom))
            {
                binding = custom;
            }

            return ShortcutMatches(e, binding);
        }

        private static bool ShortcutMatches(KeyEventArgs e, string shortcut)
        {
            if (string.IsNullOrWhiteSpace(shortcut)) return false;

            bool wantCtrl = false;
            bool wantShift = false;
            bool wantAlt = false;
            Key? wantKey = null;

            foreach (var raw in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string token = raw.Trim();
                if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
                {
                    wantCtrl = true;
                    continue;
                }
                if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                {
                    wantShift = true;
                    continue;
                }
                if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                {
                    wantAlt = true;
                    continue;
                }

                wantKey = token.ToLowerInvariant() switch
                {
                    "+" => Key.OemPlus,
                    "-" => Key.OemMinus,
                    "plus" => Key.OemPlus,
                    "minus" => Key.OemMinus,
                    "tab" => Key.Tab,
                    "space" => Key.Space,
                    _ => Enum.TryParse<Key>(token, true, out var parsed) ? parsed : null
                };
            }

            if (wantKey == null) return false;

            bool hasCtrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            bool hasShift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            bool hasAlt = (e.KeyModifiers & KeyModifiers.Alt) != 0;

            return hasCtrl == wantCtrl &&
                   hasShift == wantShift &&
                   hasAlt == wantAlt &&
                   e.Key == wantKey.Value;
        }

        private bool TryGetSelectedTab(out TabItem tabItem)
        {
            tabItem = null!;
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs?.SelectedItem is not TabItem selected) return false;
            tabItem = selected;
            return true;
        }

        private Guid GetTabId(TabItem tab)
        {
            if (_tabIds.TryGetValue(tab, out var id))
            {
                return id;
            }

            if (tab.Tag is TabSession session &&
                !string.IsNullOrWhiteSpace(session.TabId) &&
                Guid.TryParse(session.TabId, out var restoredId))
            {
                id = restoredId;
            }
            else
            {
                id = Guid.NewGuid();
            }

            _tabIds[tab] = id;
            return id;
        }

        internal Guid GetPersistentTabId(TabItem tab)
        {
            return GetTabId(tab);
        }

        private TabRuntimeState GetOrCreateTabState(TabItem tab)
        {
            if (_tabStateByTab.TryGetValue(tab, out var state))
            {
                return state;
            }

            state = new TabRuntimeState();
            if (tab.Tag is TabSession saved)
            {
                state.UserTitle = saved.UserTitle;
                state.IsPinned = saved.IsPinned;
                state.IsProtected = saved.IsProtected;
            }

            _tabStateByTab[tab] = state;
            return state;
        }

        internal string? GetTabUserTitle(TabItem tab)
        {
            return GetOrCreateTabState(tab).UserTitle;
        }

        internal bool IsTabPinned(TabItem tab)
        {
            return GetOrCreateTabState(tab).IsPinned;
        }

        internal bool IsTabProtected(TabItem tab)
        {
            return GetOrCreateTabState(tab).IsProtected;
        }

        private void ClearTabAttention(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            state.HasActivity = false;
            state.HasBell = false;
        }

        private void QueueTabVisualRefresh(TabItem tab)
        {
            _pendingVisualRefreshTabs.Add(tab);
            if (_tabVisualRefreshScheduled) return;

            _tabVisualRefreshScheduled = true;
            Dispatcher.UIThread.Post(() =>
            {
                _tabVisualRefreshScheduled = false;
                var toRefresh = _pendingVisualRefreshTabs.ToList();
                _pendingVisualRefreshTabs.Clear();

                if (toRefresh.Count == 0) return;
                foreach (var item in toRefresh)
                {
                    UpdateTabVisuals(item);
                }
            }, DispatcherPriority.Background);
        }

        private void TouchTabMru(TabItem tab)
        {
            _tabMru.Remove(tab);
            _tabMru.Insert(0, tab);
        }

        private void CleanupTabMru(TabControl tabs)
        {
            var liveTabs = tabs.Items.Cast<TabItem>().ToHashSet();
            _tabMru.RemoveAll(t => !liveTabs.Contains(t));
            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                if (!_tabMru.Contains(tab))
                {
                    _tabMru.Add(tab);
                }
            }
        }

        private bool SwitchTabByMru(bool reverse)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return false;

            CleanupTabMru(tabs);
            if (tabs.SelectedItem is not TabItem selected) return false;

            if (_tabMru.Count < 2) return false;

            int selectedIndex = _tabMru.IndexOf(selected);
            if (selectedIndex < 0)
            {
                TouchTabMru(selected);
                selectedIndex = 0;
            }

            int targetIndex = reverse
                ? (selectedIndex - 1 + _tabMru.Count) % _tabMru.Count
                : (selectedIndex + 1) % _tabMru.Count;

            var target = _tabMru[targetIndex];
            if (tabs.SelectedItem == target) return false;

            _suppressMruTouchOnSelection = true;
            tabs.SelectedItem = target;
            return true;
        }

        private string GetTabHeaderText(TabItem tab)
        {
            if (tab.Header is TextBlock tb)
            {
                return string.IsNullOrWhiteSpace(tb.Text) ? "Terminal" : tb.Text;
            }

            return "Terminal";
        }

        private string GetTabMenuLabel(TabItem tab, int index)
        {
            var state = GetOrCreateTabState(tab);
            string icon = state.IsPinned ? "📌 " : string.Empty;
            if (state.HasBell) icon += "🔔 ";
            else if (state.HasActivity) icon += "• ";
            string label = GetTabHeaderText(tab);
            return $"{index}. {icon}{label}";
        }

        private ItemsPresenter? FindTabItemsPresenter()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return null;

            return tabs.GetVisualDescendants()
                .OfType<ItemsPresenter>()
                .FirstOrDefault(p => p.Name == "PART_ItemsPresenter");
        }

        private ScrollViewer? FindTabHeaderScrollViewer()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return null;

            return tabs.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault(s => s.Name == "PART_TabHeaderScrollViewer");
        }

        private void UpdateTabHeaderViewport()
        {
            var scrollViewer = FindTabHeaderScrollViewer();
            var titleBar = this.FindControl<Grid>("TitleBar");
            if (scrollViewer == null) return;

            double reservedRight = 440;
            if (titleBar != null)
            {
                double titleBarWidth = titleBar.Bounds.Width;
                if (titleBarWidth > 0)
                {
                    reservedRight = Math.Max(
                        reservedRight,
                        Math.Ceiling(titleBarWidth + titleBar.Margin.Right + 16));
                }
            }

            scrollViewer.Margin = new Thickness(0, 0, reservedRight, 0);
            scrollViewer.Height = 36;
            scrollViewer.ClipToBounds = true;

            UpdateTabOverflowIndicator();
        }

        private void UpdateTabOverflowIndicator()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            var badge = this.FindControl<TextBlock>("TabOverflowBadge");
            var button = this.FindControl<Button>("BtnTabList");
            var scrollViewer = FindTabHeaderScrollViewer();
            if (tabs == null || badge == null || button == null || scrollViewer == null) return;

            double viewportWidth = scrollViewer.Bounds.Width;
            if (viewportWidth <= 0)
            {
                badge.IsVisible = false;
                ToolTip.SetTip(button, "Tab List");
                button.Foreground = Brushes.White;
                return;
            }

            double usedWidth = 0;
            int hiddenCount = 0;
            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                double tabWidth = tab.Bounds.Width;
                if (tabWidth <= 0) tabWidth = 120;

                if (usedWidth + tabWidth <= viewportWidth + 0.5)
                {
                    usedWidth += tabWidth;
                }
                else
                {
                    hiddenCount++;
                }
            }

            badge.IsVisible = hiddenCount > 0;
            badge.Text = hiddenCount > 0 ? $"+{hiddenCount}" : string.Empty;
            ToolTip.SetTip(button, hiddenCount > 0 ? $"Tab List ({hiddenCount} hidden)" : "Tab List");
            button.Foreground = hiddenCount > 0 ? new SolidColorBrush(Color.FromRgb(255, 210, 90)) : Brushes.White;
        }

        private void EnsureSelectedTabHeaderVisible()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            var scrollViewer = FindTabHeaderScrollViewer();
            if (tabs?.SelectedItem is not TabItem selected || scrollViewer == null) return;

            var tabOrigin = selected.TranslatePoint(new Point(0, 0), scrollViewer);
            if (tabOrigin == null) return;

            double viewportWidth = scrollViewer.Bounds.Width;
            if (viewportWidth <= 0) return;

            double tabLeft = tabOrigin.Value.X;
            double tabRight = tabLeft + selected.Bounds.Width;
            double offsetX = scrollViewer.Offset.X;
            double nextOffset = offsetX;

            if (tabLeft < 0)
            {
                nextOffset = Math.Max(0, offsetX + tabLeft - 12);
            }
            else if (tabRight > viewportWidth)
            {
                nextOffset = Math.Max(0, offsetX + (tabRight - viewportWidth) + 12);
            }

            if (Math.Abs(nextOffset - offsetX) > 0.5)
            {
                scrollViewer.Offset = new Vector(nextOffset, scrollViewer.Offset.Y);
            }
        }

        private void PopulateTabListMenu(bool showFlyout = false)
        {
            var button = this.FindControl<Button>("BtnTabList");
            var flyout = button?.Flyout as MenuFlyout;
            var tabs = this.FindControl<TabControl>("Tabs");
            if (flyout == null || tabs == null) return;

            flyout.Items.Clear();
            int index = 1;
            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                var item = new MenuItem
                {
                    Header = GetTabMenuLabel(tab, index),
                    IsChecked = tabs.SelectedItem == tab,
                    ToggleType = MenuItemToggleType.Radio,
                    StaysOpenOnClick = false
                };
                item.Click += (_, __) =>
                {
                    tabs.SelectedItem = tab;
                    flyout.Hide();
                };
                flyout.Items.Add(item);
                index++;
            }

            if (tabs.Items.Count > 0)
            {
                flyout.Items.Add(new Separator());

                var renameItem = new MenuItem { Header = "Rename Current Tab..." };
                renameItem.Click += async (_, __) => await RenameSelectedTabAsync();
                flyout.Items.Add(renameItem);

                var copyTitleItem = new MenuItem { Header = "Copy Tab Title" };
                copyTitleItem.Click += async (_, __) => await CopySelectedTabTitleAsync();
                flyout.Items.Add(copyTitleItem);

                var closeCurrentItem = new MenuItem { Header = "Close Current Tab" };
                closeCurrentItem.Click += async (_, __) => await CloseSelectedTabAsync();
                flyout.Items.Add(closeCurrentItem);

                var closeOthersItem = new MenuItem { Header = "Close Other Tabs" };
                closeOthersItem.Click += async (_, __) => await CloseOtherTabsAsync();
                flyout.Items.Add(closeOthersItem);

                if (tabs.SelectedItem is TabItem selectedTab)
                {
                    var selectedState = GetOrCreateTabState(selectedTab);

                    var pinItem = new MenuItem { Header = selectedState.IsPinned ? "Unpin Tab" : "Pin Tab" };
                    pinItem.Click += (_, __) => TogglePinSelectedTab();
                    flyout.Items.Add(pinItem);

                    var protectItem = new MenuItem { Header = selectedState.IsProtected ? "Unprotect Tab" : "Protect Tab" };
                    protectItem.Click += (_, __) => ToggleProtectSelectedTab();
                    flyout.Items.Add(protectItem);
                }
            }

            if (showFlyout && button != null)
            {
                flyout.ShowAt(button);
            }

            UpdateTabOverflowIndicator();
        }

        private static string TruncateTabLabel(string value, int maxLength = 40)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            if (maxLength < 5) return value.Substring(0, maxLength);
            return value.Substring(0, maxLength - 1) + "…";
        }

        private static string TruncateTabLabelWithSuffix(string value, int maxLength, string suffix)
        {
            if (string.IsNullOrEmpty(suffix))
            {
                return TruncateTabLabel(value, maxLength);
            }

            if (maxLength <= suffix.Length)
            {
                return suffix.Substring(0, maxLength);
            }

            int available = maxLength - suffix.Length;
            string prefix = value;
            if (prefix.Length > available)
            {
                prefix = available < 5
                    ? prefix.Substring(0, available)
                    : prefix.Substring(0, available - 1) + "…";
            }

            return prefix + suffix;
        }

        private string GetTabPrimaryTitle(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            var pane = ResolvePaneForTab(tab);
            return state.UserTitle ??
                   pane?.GetBaseTabTitle() ??
                   "Terminal";
        }

        private string BuildFullTabLabel(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            var pane = ResolvePaneForTab(tab);
            string label = GetTabPrimaryTitle(tab);

            if (pane?.Profile != null)
            {
                var forwards = pane.Profile.Forwards;
                int activeCount = forwards.Count(f => f.Status == ForwardingStatus.Active);
                int startingCount = forwards.Count(f => f.Status == ForwardingStatus.Starting);
                bool hasFailed = forwards.Any(f => f.Status == ForwardingStatus.Failed);

                if (activeCount > 0 || startingCount > 0)
                {
                    string badge = activeCount.ToString();
                    if (startingCount > 0) badge += $" ({startingCount})";
                    label = $"{label} 🔁 {badge}";
                }
                else if (hasFailed)
                {
                    label = $"{label} ⚠️";
                }
            }

            if (state.LastExitCode.HasValue)
            {
                string statusGlyph = state.LastExitCode.Value == 0 ? " ✓" : $" ✖{state.LastExitCode.Value}";
                label += statusGlyph;
            }

            if (state.HasBell)
            {
                label += " 🔔";
            }
            else if (state.HasActivity)
            {
                label += " •";
            }

            if (state.IsPinned)
            {
                label = "📌 " + label;
            }
            if (state.IsProtected)
            {
                label = "🔒 " + label;
            }

            return label;
        }

        private Dictionary<TabItem, string> BuildTabDisplayLabels(IReadOnlyList<TabItem> tabs, int maxLength)
        {
            var fullLabels = tabs.ToDictionary(t => t, BuildFullTabLabel);
            var truncated = tabs.ToDictionary(t => t, t => TruncateTabLabel(fullLabels[t], maxLength));

            var collisions = tabs
                .GroupBy(t => truncated[t], StringComparer.Ordinal)
                .Where(g => g.Count() > 1);

            foreach (var group in collisions)
            {
                foreach (var tab in group)
                {
                    string hint = "~" + GetTabId(tab).ToString("N").Substring(0, 4);
                    truncated[tab] = TruncateTabLabelWithSuffix(fullLabels[tab], maxLength, hint);
                }
            }

            return truncated;
        }

        private async Task CopySelectedTabTitleAsync()
        {
            if (!TryGetSelectedTab(out var tab)) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null) return;

            string title = GetTabPrimaryTitle(tab);
            await topLevel.Clipboard.SetTextAsync(title);
        }

        private async Task<string?> ShowTextPromptAsync(string title, string prompt, string defaultValue)
        {
            string? result = null;
            var dialog = new Window
            {
                Title = title,
                Width = 520,
                Height = 190,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var input = new TextBox
            {
                Text = defaultValue,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var cancelButton = new Button { Content = "Cancel", Width = 92 };
            cancelButton.Click += (_, __) => dialog.Close();

            var applyButton = new Button { Content = "Apply", Width = 92 };
            applyButton.Click += (_, __) =>
            {
                result = input.Text;
                dialog.Close();
            };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = prompt },
                        input,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, applyButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return result;
        }

        private async Task RenameSelectedTabAsync()
        {
            if (!TryGetSelectedTab(out var tab)) return;
            var state = GetOrCreateTabState(tab);
            string current = state.UserTitle ?? GetTabHeaderText(tab);
            var updated = await ShowTextPromptAsync("Rename Tab", "Tab title", current);
            if (updated == null) return;

            state.UserTitle = string.IsNullOrWhiteSpace(updated) ? null : updated.Trim();
            UpdateTabVisuals(tab);
            PopulateTabListMenu();
        }

        private string GetTabSwitchCommandLabel(TabItem tab)
        {
            var pane = ResolvePaneForTab(tab);
            string title = GetTabHeaderText(tab);
            string process = pane?.ShellCommand ?? "shell";
            string cwd = pane?.CurrentWorkingDirectory ?? "";

            if (!string.IsNullOrWhiteSpace(cwd))
            {
                return $"Switch Tab: {title} [{Path.GetFileName(cwd)} | {Path.GetFileName(process)}]";
            }

            return $"Switch Tab: {title} [{Path.GetFileName(process)}]";
        }

        private async Task CloseOtherTabsAsync()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs?.SelectedItem is not TabItem selected) return;

            var others = tabs.Items.Cast<TabItem>().Where(t => t != selected).ToList();
            foreach (var tab in others)
            {
                if (GetOrCreateTabState(tab).IsPinned) continue;
                await CloseTabAsync(tab);
            }
        }

        private void TogglePinSelectedTab()
        {
            if (!TryGetSelectedTab(out var tab)) return;
            var state = GetOrCreateTabState(tab);
            state.IsPinned = !state.IsPinned;
            UpdateTabVisuals(tab);
            PopulateTabListMenu();
        }

        private void ToggleProtectSelectedTab()
        {
            if (!TryGetSelectedTab(out var tab)) return;
            var state = GetOrCreateTabState(tab);
            state.IsProtected = !state.IsProtected;
            UpdateTabVisuals(tab);
            PopulateTabListMenu();
        }

        private void ResetTabCollections()
        {
            _activePaneByTab.Clear();
            _paneZoomStateByTab.Clear();
            _zoomedPaneIdByTab.Clear();
            _broadcastEnabledTabs.Clear();
            _tabIds.Clear();
            _layoutModelByTab.Clear();
            _tabMru.Clear();
            _tabStateByTab.Clear();
            _pendingVisualRefreshTabs.Clear();
        }

        private void DisposeAllTabs(TabControl tabs)
        {
            foreach (var item in tabs.Items.Cast<TabItem>().ToList())
            {
                if (item.Content is Control content)
                {
                    DisposeControlTree(content);
                }
            }
        }

        private void ApplySessionSnapshot(NovaSession session)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            DisposeAllTabs(tabs);
            ResetTabCollections();
            SessionManager.RestoreSession(this, tabs, _settings, session);
            if (tabs.Items.Count > 0)
            {
                InitializeRestoredTabs(tabs);
            }
            SetupCommandPalette();
        }

        private async Task SaveWorkspaceInteractiveAsync()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            string suggested = $"workspace-{DateTime.Now:yyyyMMdd-HHmm}";
            var name = await ShowTextPromptAsync("Save Workspace", "Workspace name", suggested);
            if (string.IsNullOrWhiteSpace(name)) return;

            var snapshot = SessionManager.CaptureSession(this, tabs);
            if (WorkspaceManager.SaveWorkspace(name.Trim(), snapshot))
            {
                SetupCommandPalette();
            }
        }

        private async Task LoadWorkspaceInteractiveAsync()
        {
            var names = WorkspaceManager.ListWorkspaceNames();
            if (names.Count == 0) return;

            var first = names[0];
            var name = await ShowTextPromptAsync("Load Workspace", "Workspace name", first);
            if (string.IsNullOrWhiteSpace(name)) return;
            LoadWorkspaceByName(name.Trim());
        }

        private void LoadWorkspaceByName(string name)
        {
            var snapshot = WorkspaceManager.LoadWorkspace(name);
            if (snapshot == null) return;
            ApplySessionSnapshot(snapshot);
        }

        internal Guid? GetActivePaneIdForTab(TabItem tabItem)
        {
            return ResolvePaneForTab(tabItem)?.PaneId;
        }

        internal Guid? GetZoomedPaneIdForTab(TabItem tabItem)
        {
            if (_zoomedPaneIdByTab.TryGetValue(tabItem, out var paneId))
            {
                return paneId;
            }

            return null;
        }

        internal bool IsBroadcastEnabledForTab(TabItem tabItem)
        {
            return _broadcastEnabledTabs.Contains(tabItem);
        }

        internal Control? GetLayoutRootForTab(TabItem tabItem)
        {
            if (_paneZoomStateByTab.TryGetValue(tabItem, out var zoomState))
            {
                return zoomState.OriginalRoot;
            }

            return tabItem.Content as Control;
        }

        private void PublishPaneEvent(TabItem tabItem, TerminalPane? pane, PaneAuditEventKind kind, string details = "")
        {
            PaneEventStream.Publish(new PaneAuditEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Kind = kind,
                TabId = GetTabId(tabItem),
                PaneId = pane?.PaneId,
                Details = details
            });
        }

        private void RefreshLayoutModelForTab(TabItem tabItem)
        {
            var root = GetLayoutRootForTab(tabItem);
            if (root == null)
            {
                _layoutModelByTab.Remove(tabItem);
                return;
            }

            _layoutModelByTab[tabItem] = PaneLayoutModel.FromControl(
                root,
                GetActivePaneIdForTab(tabItem),
                GetZoomedPaneIdForTab(tabItem),
                IsBroadcastEnabledForTab(tabItem));
        }

        private void RefreshAllLayoutModels()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                RefreshLayoutModelForTab(item);
            }
        }

        private TerminalPane? FindPaneById(Control? control, Guid paneId)
        {
            return EnumeratePanes(control).FirstOrDefault(p => p.PaneId == paneId);
        }

        private static void CopyGridPlacement(Control from, Control to)
        {
            Grid.SetRow(to, Grid.GetRow(from));
            Grid.SetColumn(to, Grid.GetColumn(from));
            Grid.SetRowSpan(to, Grid.GetRowSpan(from));
            Grid.SetColumnSpan(to, Grid.GetColumnSpan(from));
        }

        private bool EnterPaneZoom(TabItem tabItem, TerminalPane pane, bool publishEvent)
        {
            if (_paneZoomStateByTab.ContainsKey(tabItem)) return false;
            if (tabItem.Content is not Control root) return false;
            if (ReferenceEquals(root, pane)) return false;

            var placeholder = new Border { IsVisible = false };
            CopyGridPlacement(pane, placeholder);

            if (pane.Parent is Panel panel)
            {
                int index = panel.Children.IndexOf(pane);
                if (index < 0) return false;
                panel.Children.RemoveAt(index);
                panel.Children.Insert(index, placeholder);
            }
            else if (pane.Parent is ContentPresenter presenter)
            {
                presenter.Content = placeholder;
            }
            else if (pane.Parent is ContentControl contentControl)
            {
                contentControl.Content = placeholder;
            }
            else
            {
                return false;
            }

            _paneZoomStateByTab[tabItem] = new PaneZoomState
            {
                OriginalRoot = root,
                Placeholder = placeholder
            };
            _zoomedPaneIdByTab[tabItem] = pane.PaneId;

            tabItem.Content = pane;
            UpdateActivePane(pane);
            FocusPaneTerminal(pane, defer: true);
            UpdatePaneAutomationLabels();
            RefreshLayoutModelForTab(tabItem);

            if (publishEvent)
            {
                PublishPaneEvent(tabItem, pane, PaneAuditEventKind.ZoomToggled, "on");
            }

            return true;
        }

        private bool ExitPaneZoom(TabItem tabItem, bool publishEvent)
        {
            if (!_paneZoomStateByTab.TryGetValue(tabItem, out var state)) return false;
            if (tabItem.Content is not TerminalPane zoomedPane) return false;

            tabItem.Content = state.OriginalRoot;

            var placeholder = state.Placeholder;
            if (placeholder.Parent is Panel panel)
            {
                int index = panel.Children.IndexOf(placeholder);
                if (index >= 0)
                {
                    panel.Children.RemoveAt(index);
                    CopyGridPlacement(placeholder, zoomedPane);
                    panel.Children.Insert(index, zoomedPane);
                }
            }
            else if (placeholder.Parent is ContentPresenter presenter)
            {
                presenter.Content = zoomedPane;
            }
            else if (placeholder.Parent is ContentControl contentControl)
            {
                contentControl.Content = zoomedPane;
            }
            else
            {
                return false;
            }

            _paneZoomStateByTab.Remove(tabItem);
            _zoomedPaneIdByTab.Remove(tabItem);
            UpdateActivePane(zoomedPane);
            FocusPaneTerminal(zoomedPane, defer: true);
            UpdatePaneAutomationLabels();
            RefreshLayoutModelForTab(tabItem);

            if (publishEvent)
            {
                PublishPaneEvent(tabItem, zoomedPane, PaneAuditEventKind.ZoomToggled, "off");
            }

            return true;
        }

        private void TogglePaneZoomForCurrentTab()
        {
            if (!TryGetSelectedTab(out var tabItem)) return;

            if (_paneZoomStateByTab.ContainsKey(tabItem))
            {
                ExitPaneZoom(tabItem, publishEvent: true);
                return;
            }

            var pane = ResolvePaneForTab(tabItem);
            if (pane != null)
            {
                EnterPaneZoom(tabItem, pane, publishEvent: true);
            }
        }

        private bool TryMapBroadcastKey(KeyEventArgs e, TerminalBuffer? buffer, out string? sequence)
        {
            sequence = null;
            bool isCtrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            bool isAlt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
            bool isShift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

            if (isAlt) return false;

            switch (e.Key)
            {
                case Key.Enter: sequence = "\r"; return true;
                case Key.Back: sequence = "\x7f"; return true;
                case Key.Tab: sequence = "\t"; return true;
                case Key.Escape: sequence = "\x1b"; return true;
                case Key.Up: sequence = buffer != null && buffer.Modes.IsApplicationCursorKeys ? "\x1bOA" : "\x1b[A"; return true;
                case Key.Down: sequence = buffer != null && buffer.Modes.IsApplicationCursorKeys ? "\x1bOB" : "\x1b[B"; return true;
                case Key.Right: sequence = buffer != null && buffer.Modes.IsApplicationCursorKeys ? "\x1bOC" : "\x1b[C"; return true;
                case Key.Left: sequence = buffer != null && buffer.Modes.IsApplicationCursorKeys ? "\x1bOD" : "\x1b[D"; return true;
                case Key.Home: sequence = "\x1b[H"; return true;
                case Key.End: sequence = "\x1b[F"; return true;
                case Key.Delete: sequence = "\x1b[3~"; return true;
                case Key.Insert: sequence = "\x1b[2~"; return true;
                case Key.PageUp: sequence = "\x1b[5~"; return true;
                case Key.PageDown: sequence = "\x1b[6~"; return true;
                case Key.F1: sequence = "\x1bOP"; return true;
                case Key.F2: sequence = "\x1bOQ"; return true;
                case Key.F3: sequence = "\x1bOR"; return true;
                case Key.F4: sequence = "\x1bOS"; return true;
                case Key.F5: sequence = "\x1b[15~"; return true;
                case Key.F6: sequence = "\x1b[17~"; return true;
                case Key.F7: sequence = "\x1b[18~"; return true;
                case Key.F8: sequence = "\x1b[19~"; return true;
                case Key.F9: sequence = "\x1b[20~"; return true;
                case Key.F10: sequence = "\x1b[21~"; return true;
                case Key.F11: sequence = "\x1b[23~"; return true;
                case Key.F12: sequence = "\x1b[24~"; return true;
            }

            if (isCtrl && !isShift && e.Key >= Key.A && e.Key <= Key.Z)
            {
                char ctrlChar = (char)(e.Key - Key.A + 1);
                sequence = ctrlChar.ToString();
                return true;
            }

            return false;
        }

        private void BroadcastKeyToSiblingPanes(KeyEventArgs e)
        {
            if (IsFocusOverlayVisible()) return;
            if (!TryGetSelectedTab(out var tabItem)) return;
            if (!_broadcastEnabledTabs.Contains(tabItem)) return;
            if (_currentPane == null) return;
            if (!TryMapBroadcastKey(e, _currentPane.Buffer, out var sequence) || string.IsNullOrEmpty(sequence)) return;

            foreach (var pane in EnumeratePanes(tabItem.Content as Control))
            {
                if (pane == _currentPane) continue;
                pane.Session?.SendInput(sequence);
            }
        }

        private void BroadcastTextToSiblingPanes(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (IsFocusOverlayVisible()) return;
            if (!TryGetSelectedTab(out var tabItem)) return;
            if (!_broadcastEnabledTabs.Contains(tabItem)) return;
            if (_currentPane == null) return;

            foreach (var pane in EnumeratePanes(tabItem.Content as Control))
            {
                if (pane == _currentPane) continue;
                pane.Session?.SendInput(text);
            }
        }

        private void ToggleBroadcastForCurrentTab()
        {
            if (!TryGetSelectedTab(out var tabItem)) return;

            bool enabled;
            if (_broadcastEnabledTabs.Contains(tabItem))
            {
                _broadcastEnabledTabs.Remove(tabItem);
                enabled = false;
            }
            else
            {
                _broadcastEnabledTabs.Add(tabItem);
                enabled = true;
            }

            UpdateBroadcastIndicator();
            RefreshLayoutModelForTab(tabItem);
            PublishPaneEvent(tabItem, ResolvePaneForTab(tabItem), PaneAuditEventKind.BroadcastToggled, enabled ? "on" : "off");
        }

        private void UpdateBroadcastIndicator()
        {
            if (TryGetSelectedTab(out var tabItem) && _broadcastEnabledTabs.Contains(tabItem))
            {
                Title = "NovaTerminal [Broadcast: Tab]";
            }
            else
            {
                Title = "NovaTerminal";
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
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateTabVisuals();
                    UpdateTabHeaderViewport();
                    EnsureSelectedTabHeaderVisible();
                    FocusCurrentTerminal(defer: true);
                }, DispatcherPriority.Input);
            };
            this.Activated += (s, e) => FocusCurrentTerminal(defer: true);
            this.SizeChanged += (_, __) => Dispatcher.UIThread.Post(UpdateTabHeaderViewport, DispatcherPriority.Background);

            var tabs = this.FindControl<TabControl>("Tabs");
            var btnNew = this.FindControl<Button>("BtnNewTab");
            var btnTabList = this.FindControl<Button>("BtnTabList");
            var settingsBtn = this.FindControl<Button>("SettingsBtn");
            var titleBar = this.FindControl<Grid>("TitleBar");
            var dragBorder = this.FindControl<Border>("DragBorder");

            if (btnTabList != null)
            {
                btnTabList.Click += (s, e) => PopulateTabListMenu();
            }

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
                titleBar.SizeChanged += (_, __) => Dispatcher.UIThread.Post(UpdateTabHeaderViewport, DispatcherPriority.Background);
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
                    bool updatedSpecific = false;
                    foreach (var removed in e.RemovedItems.OfType<TabItem>())
                    {
                        UpdateTabVisuals(removed);
                        updatedSpecific = true;
                    }

                    if (tabs.SelectedItem is TabItem ti)
                    {
                        GetTabId(ti);
                        if (_suppressMruTouchOnSelection)
                        {
                            _suppressMruTouchOnSelection = false;
                        }
                        else
                        {
                            TouchTabMru(ti);
                        }
                        GetOrCreateTabState(ti);
                        ClearTabAttention(ti);
                        var pane = ResolvePaneForTab(ti);
                        if (pane != null)
                        {
                            UpdateActivePane(pane);
                            FocusPaneTerminal(pane, defer: true);
                        }
                        UpdateTabVisuals(ti);
                        updatedSpecific = true;
                    }
                    if (!updatedSpecific) UpdateTabVisuals();
                    UpdatePaneAutomationLabels();
                    UpdateTabAutomationLabels();
                    UpdateBroadcastIndicator();
                    PopulateTabListMenu();
                    UpdateTabHeaderViewport();
                    Dispatcher.UIThread.Post(EnsureSelectedTabHeaderVisible, DispatcherPriority.Background);
                };
            }

            ApplyThemeToUI();

            PopulateNewTabMenu();

            var menuManage = this.FindControl<MenuItem>("MenuManageProfiles");
            if (menuManage != null) menuManage.Click += async (s, e) =>
            {
                await OpenSettings(1); // Open Tab 1 (Profiles)
            };

            var btnOpenRec = this.FindControl<Button>("BtnOpenRec");
            if (btnOpenRec != null) btnOpenRec.Click += async (s, e) =>
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

            var btnRecord = this.FindControl<Button>("BtnRecord");
            if (btnRecord != null)
            {
                btnRecord.Click += (s, e) => _currentPane?.ToggleRecording();
            }

            // Global Focus Tracking
            this.AddHandler(GotFocusEvent, (s, e) =>
            {
                var pane = (e.Source as Control)?.FindAncestorOfType<TerminalPane>();
                if (pane != null)
                {
                    UpdateActivePane(pane);
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
                else
                {
                    InitializeRestoredTabs(tabs);
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

                if (IsShortcut(e, "command_palette", "Ctrl+Shift+P"))
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

                if (IsShortcut(e, "font_increase", "Ctrl+OemPlus") || IsShortcut(e, "font_increase_alt", "Ctrl+Add"))
                {
                    _settings.FontSize++;
                    ApplySettingsToAllTabs();
                    _settings.Save();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "font_decrease", "Ctrl+OemMinus") || IsShortcut(e, "font_decrease_alt", "Ctrl+Subtract"))
                {
                    _settings.FontSize = Math.Max(6, _settings.FontSize - 1);
                    ApplySettingsToAllTabs();
                    _settings.Save();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "new_tab", "Ctrl+Shift+T"))
                {
                    AddTab();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "close_tab", "Ctrl+W"))
                {
                    _ = CloseSelectedTabAsync();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "close_pane", "Ctrl+Shift+W"))
                {
                    CloseActivePane();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "find", "Ctrl+F") || IsShortcut(e, "find_alt", "Ctrl+Shift+F"))
                {
                    _currentPane?.ToggleSearch();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "split_vertical", "Ctrl+Shift+D"))
                {
                    // "Split Vertical" means a vertical divider (side-by-side panes).
                    SplitPane(Avalonia.Layout.Orientation.Horizontal);
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "split_horizontal", "Ctrl+Shift+E"))
                {
                    // "Split Horizontal" means a horizontal divider (stacked panes).
                    SplitPane(Avalonia.Layout.Orientation.Vertical);
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "equalize_panes", "Ctrl+Shift+G"))
                {
                    EqualizeCurrentSplit();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "toggle_pane_zoom", "Ctrl+Shift+Z"))
                {
                    TogglePaneZoomForCurrentTab();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "toggle_broadcast_input", "Ctrl+Shift+B"))
                {
                    ToggleBroadcastForCurrentTab();
                    e.Handled = true;
                    return;
                }
                bool nextTabShortcut = IsShortcut(e, "next_tab", "Ctrl+Tab");
                bool prevTabShortcut = IsShortcut(e, "prev_tab", "Ctrl+Shift+Tab");
                if (nextTabShortcut || prevTabShortcut)
                {
                    bool switched = SwitchTabByMru(reverse: prevTabShortcut);
                    if (switched)
                    {
                        e.Handled = true;
                        return;
                    }
                }
                if (IsShortcut(e, "open_tab_list", "Ctrl+Shift+O"))
                {
                    PopulateTabListMenu(showFlyout: true);
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "paste", "Ctrl+V"))
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

                BroadcastKeyToSiblingPanes(e);
            }, RoutingStrategies.Tunnel);

            this.AddHandler(TextInputEvent, (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Text))
                {
                    BroadcastTextToSiblingPanes(e.Text);
                }
            }, RoutingStrategies.Tunnel);

            try { Vault = new VaultService(); } catch { }
        }

        private void InitializeRestoredTabs(TabControl tabs)
        {
            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                GetTabId(item);
                GetOrCreateTabState(item);
                if (item.Content is Control c) WireControlTree(c);
            }

            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                if (item.Content is not Control c) continue;

                TerminalPane? active = null;
                if (item.Tag is TabSession saved &&
                    !string.IsNullOrWhiteSpace(saved.ActivePaneId) &&
                    Guid.TryParse(saved.ActivePaneId, out var activeId))
                {
                    active = FindPaneById(c, activeId);
                }

                active ??= FindFirstPane(c);
                if (active != null) _activePaneByTab[item] = active;

                if (item.Tag is TabSession savedSession && savedSession.BroadcastInputEnabled)
                {
                    _broadcastEnabledTabs.Add(item);
                }
            }

            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                if (item.Tag is not TabSession saved ||
                    string.IsNullOrWhiteSpace(saved.ZoomedPaneId) ||
                    !Guid.TryParse(saved.ZoomedPaneId, out var zoomedPaneId) ||
                    item.Content is not Control c)
                {
                    continue;
                }

                var zoomPane = FindPaneById(c, zoomedPaneId);
                if (zoomPane != null)
                {
                    EnterPaneZoom(item, zoomPane, publishEvent: false);
                }
            }

            if (tabs.SelectedItem is TabItem selected)
            {
                var selectedPane = ResolvePaneForTab(selected);
                if (selectedPane != null)
                {
                    UpdateActivePane(selectedPane);
                    FocusPaneTerminal(selectedPane, defer: true);
                }
            }

            UpdateTabVisuals();
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            RefreshAllLayoutModels();
            CleanupTabMru(tabs);
            PopulateTabListMenu();
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
            else if (control is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Control c)
                    {
                        ApplySettingsRecursive(c, settings);
                    }
                }
            }
            else if (control is ContentControl cc)
            {
                ApplySettingsRecursive(cc.Content as Control, settings);
            }
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

        private void WireControlTree(Control control)
        {
            if (control is TerminalPane pane)
            {
                WirePane(pane);
                return;
            }

            if (control is Grid grid)
            {
                foreach (var child in grid.Children)
                {
                    if (child is GridSplitter splitter)
                    {
                        WireSplitter(splitter, grid);
                    }
                    else if (child is Control cc)
                    {
                        WireControlTree(cc);
                    }
                }

                return;
            }

            if (control is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Control cc) WireControlTree(cc);
                }

                return;
            }

            if (control is ContentControl contentControl && contentControl.Content is Control content)
            {
                WireControlTree(content);
            }
        }

        private void WirePane(TerminalPane pane)
        {
            pane.RequestSftpTransfer -= OnPaneRequestSftpTransfer;
            pane.WorkingDirectoryChanged -= OnPaneWorkingDirectoryChanged;
            pane.TitleChanged -= OnPaneTitleChanged;
            pane.PaneActionRequested -= OnPaneActionRequested;
            pane.OutputReceived -= OnPaneOutputReceived;
            pane.BellReceived -= OnPaneBellReceived;
            pane.CommandStarted -= OnPaneCommandStarted;
            pane.CommandFinished -= OnPaneCommandFinished;
            pane.ProcessExited -= OnPaneProcessExited;

            pane.RequestSftpTransfer += OnPaneRequestSftpTransfer;
            pane.WorkingDirectoryChanged += OnPaneWorkingDirectoryChanged;
            pane.TitleChanged += OnPaneTitleChanged;
            pane.PaneActionRequested += OnPaneActionRequested;
            pane.OutputReceived += OnPaneOutputReceived;
            pane.BellReceived += OnPaneBellReceived;
            pane.CommandStarted += OnPaneCommandStarted;
            pane.CommandFinished += OnPaneCommandFinished;
            pane.ProcessExited += OnPaneProcessExited;
        }

        private void UnwirePane(TerminalPane pane)
        {
            pane.RequestSftpTransfer -= OnPaneRequestSftpTransfer;
            pane.WorkingDirectoryChanged -= OnPaneWorkingDirectoryChanged;
            pane.TitleChanged -= OnPaneTitleChanged;
            pane.PaneActionRequested -= OnPaneActionRequested;
            pane.OutputReceived -= OnPaneOutputReceived;
            pane.BellReceived -= OnPaneBellReceived;
            pane.CommandStarted -= OnPaneCommandStarted;
            pane.CommandFinished -= OnPaneCommandFinished;
            pane.ProcessExited -= OnPaneProcessExited;
        }

        private void OnPaneRequestSftpTransfer(TerminalPane srcPane, TransferDirection direction, TransferKind kind)
        {
            _ = InitiateSftpTransfer(srcPane, direction, kind);
        }

        private void OnPaneWorkingDirectoryChanged(TerminalPane srcPane, string cwd)
        {
            _ = srcPane;
            _ = cwd;
            Dispatcher.UIThread.Post(() => UpdateTabVisuals());
        }

        private void OnPaneTitleChanged(TerminalPane srcPane, string title)
        {
            _ = srcPane;
            _ = title;
            Dispatcher.UIThread.Post(() => UpdateTabVisuals());
        }

        private void OnPaneOutputReceived(TerminalPane pane)
        {
            var tab = pane.FindAncestorOfType<TabItem>();
            if (tab == null) return;

            if (!TryGetSelectedTab(out var selectedTab) || selectedTab != tab)
            {
                var state = GetOrCreateTabState(tab);
                state.HasActivity = true;
                QueueTabVisualRefresh(tab);
            }
        }

        private void OnPaneBellReceived(TerminalPane pane)
        {
            var tab = pane.FindAncestorOfType<TabItem>();
            if (tab == null) return;

            if (!TryGetSelectedTab(out var selectedTab) || selectedTab != tab)
            {
                var state = GetOrCreateTabState(tab);
                var now = DateTime.UtcNow;
                if ((now - state.LastBellUtc) < BellDebounceWindow)
                {
                    return;
                }

                state.LastBellUtc = now;
                state.HasBell = true;
                QueueTabVisualRefresh(tab);
            }
        }

        private void OnPaneCommandStarted(TerminalPane pane)
        {
            var tab = pane.FindAncestorOfType<TabItem>();
            if (tab == null) return;

            var state = GetOrCreateTabState(tab);
            state.LastExitCode = null;
            QueueTabVisualRefresh(tab);
        }

        private void OnPaneCommandFinished(TerminalPane pane, int? exitCode)
        {
            if (!exitCode.HasValue) return;

            var tab = pane.FindAncestorOfType<TabItem>();
            if (tab == null) return;

            var state = GetOrCreateTabState(tab);
            state.LastExitCode = exitCode.Value;
            QueueTabVisualRefresh(tab);
        }

        private void OnPaneProcessExited(TerminalPane pane, int exitCode)
        {
            var tab = pane.FindAncestorOfType<TabItem>();
            if (tab == null) return;

            var state = GetOrCreateTabState(tab);
            state.LastExitCode = exitCode;
            QueueTabVisualRefresh(tab);
        }

        private void OnPaneActionRequested(TerminalPane pane, PaneAction action)
        {
            UpdateActivePane(pane);
            FocusPaneTerminal(pane, defer: true);

            switch (action)
            {
                case PaneAction.SplitVertical:
                    SplitPane(Avalonia.Layout.Orientation.Horizontal);
                    break;
                case PaneAction.SplitHorizontal:
                    SplitPane(Avalonia.Layout.Orientation.Vertical);
                    break;
                case PaneAction.Equalize:
                    EqualizeCurrentSplit();
                    break;
                case PaneAction.ToggleZoom:
                    TogglePaneZoomForCurrentTab();
                    break;
                case PaneAction.ToggleBroadcast:
                    ToggleBroadcastForCurrentTab();
                    break;
                case PaneAction.Close:
                    CloseActivePane();
                    break;
            }
        }

        private static bool IsSplitGrid(Grid grid)
        {
            int paneChildren = grid.Children.OfType<Control>().Count(c => c is not GridSplitter);
            return paneChildren >= 2;
        }

        private Grid? FindNearestSplitGrid(Control start)
        {
            Control? current = start;
            while (current != null)
            {
                if (current.Parent is Grid grid && IsSplitGrid(grid))
                {
                    return grid;
                }

                current = current.Parent as Control;
            }

            return null;
        }

        private static bool IsSplitterColumn(Grid grid, int column)
        {
            return grid.Children.OfType<GridSplitter>().Any(s => Grid.GetColumn(s) == column);
        }

        private static bool IsSplitterRow(Grid grid, int row)
        {
            return grid.Children.OfType<GridSplitter>().Any(s => Grid.GetRow(s) == row);
        }

        private void EqualizeCurrentSplit()
        {
            if (_currentPane == null) return;

            var splitGrid = FindNearestSplitGrid(_currentPane);
            if (splitGrid == null) return;

            EqualizeSplitGrid(splitGrid);
            InvalidateMeasure();
            InvalidateArrange();
            if (TryGetSelectedTab(out var selected))
            {
                RefreshLayoutModelForTab(selected);
                PublishPaneEvent(selected, _currentPane, PaneAuditEventKind.Equalized);
            }
        }

        private void EqualizeSplitGrid(Grid splitGrid)
        {
            if (!IsSplitGrid(splitGrid)) return;

            bool byColumns = splitGrid.ColumnDefinitions.Count > 1;
            if (byColumns)
            {
                for (int i = 0; i < splitGrid.ColumnDefinitions.Count; i++)
                {
                    if (IsSplitterColumn(splitGrid, i)) continue;
                    splitGrid.ColumnDefinitions[i].Width = new GridLength(1, GridUnitType.Star);
                }
            }
            else
            {
                for (int i = 0; i < splitGrid.RowDefinitions.Count; i++)
                {
                    if (IsSplitterRow(splitGrid, i)) continue;
                    splitGrid.RowDefinitions[i].Height = new GridLength(1, GridUnitType.Star);
                }
            }
        }

        private void WireSplitter(GridSplitter splitter, Grid ownerGrid)
        {
            splitter.Tag = ownerGrid;
            splitter.DoubleTapped -= OnSplitterDoubleTapped;
            splitter.DoubleTapped += OnSplitterDoubleTapped;
        }

        private void OnSplitterDoubleTapped(object? sender, RoutedEventArgs e)
        {
            if (sender is GridSplitter splitter && splitter.Tag is Grid ownerGrid)
            {
                EqualizeSplitGrid(ownerGrid);
                InvalidateMeasure();
                InvalidateArrange();
                if (TryGetSelectedTab(out var tabItem))
                {
                    RefreshLayoutModelForTab(tabItem);
                    PublishPaneEvent(tabItem, _currentPane, PaneAuditEventKind.Equalized, "splitter-double-tap");
                }
                e.Handled = true;
            }
        }

        private IEnumerable<TerminalPane> EnumeratePanes(Control? control)
        {
            if (control == null) yield break;

            if (control is TerminalPane pane)
            {
                yield return pane;
                yield break;
            }

            if (control is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is not Control cc) continue;
                    foreach (var nested in EnumeratePanes(cc)) yield return nested;
                }

                yield break;
            }

            if (control is ContentControl contentControl && contentControl.Content is Control content)
            {
                foreach (var nested in EnumeratePanes(content)) yield return nested;
            }
        }

        private void UpdatePaneAutomationLabels()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            int tabCount = tabs.Items.Count;
            for (int tabIndex = 0; tabIndex < tabCount; tabIndex++)
            {
                if (tabs.Items[tabIndex] is not TabItem tabItem) continue;
                var panes = EnumeratePanes(tabItem.Content as Control).ToList();
                if (panes.Count == 0) continue;

                var activeForTab = ResolvePaneForTab(tabItem);
                for (int paneIndex = 0; paneIndex < panes.Count; paneIndex++)
                {
                    var pane = panes[paneIndex];
                    bool isActive = pane == activeForTab || pane == _currentPane;
                    string activeText = isActive ? " active" : string.Empty;
                    string label = $"Tab {tabIndex + 1} pane {paneIndex + 1} of {panes.Count}{activeText}";
                    AutomationProperties.SetName(pane, label);
                    AutomationProperties.SetName(pane.ActiveControl, label);
                }
            }
        }

        private void UpdateTabAutomationLabels()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            int count = tabs.Items.Count;
            for (int i = 0; i < count; i++)
            {
                if (tabs.Items[i] is not TabItem tab) continue;
                var state = GetOrCreateTabState(tab);
                bool active = tabs.SelectedItem == tab;
                string attention = state.HasBell ? " bell" : state.HasActivity ? " activity" : string.Empty;
                string label = $"Tab {i + 1} of {count}: {GetTabHeaderText(tab)}{(active ? " active" : "")}{attention}";
                AutomationProperties.SetName(tab, label);
            }
        }

        private enum MoveDirection { Left, Right, Up, Down }
        private bool NavigatePane(MoveDirection dir) => NavigatePaneRecursive(_currentPane, dir);
        private bool NavigatePaneRecursive(Control? start, MoveDirection dir)
        {
            if (start == null || start.Parent is not Grid parentGrid) return false;
            int r = Grid.GetRow(start);
            int c = Grid.GetColumn(start);

            int rowStep = 0;
            int colStep = 0;
            switch (dir)
            {
                case MoveDirection.Left: colStep = -1; break;
                case MoveDirection.Right: colStep = 1; break;
                case MoveDirection.Up: rowStep = -1; break;
                case MoveDirection.Down: rowStep = 1; break;
            }

            int probeR = r + rowStep;
            int probeC = c + colStep;
            while (probeR >= 0 && probeC >= 0)
            {
                Control? sibling = parentGrid.Children
                    .OfType<Control>()
                    .FirstOrDefault(x => Grid.GetRow(x) == probeR && Grid.GetColumn(x) == probeC);
                if (sibling == null) break;
                if (sibling is GridSplitter)
                {
                    probeR += rowStep;
                    probeC += colStep;
                    continue;
                }

                var before = _currentPane;
                FocusFirstPane(sibling);
                return _currentPane != null && _currentPane != before;
            }
            if (parentGrid.Parent is Control grandParent && (grandParent is Grid || grandParent is ContentPresenter || grandParent is TabItem))
                return NavigatePaneRecursive(parentGrid, dir);
            return false;
        }

        private void FocusFirstPane(Control control)
        {
            var pane = FindFirstPane(control);
            if (pane != null)
            {
                UpdateActivePane(pane);
                FocusPaneTerminal(pane, defer: true);
            }
        }

        public static VaultService? Vault { get; private set; }

        private void CloseActiveTab()
        {
            _ = CloseSelectedTabAsync();
        }

        private async Task CloseSelectedTabAsync()
        {
            if (!TryGetSelectedTab(out var selectedTab)) return;
            await CloseTabAsync(selectedTab);
        }

        private async Task<bool> CloseTabAsync(TabItem tab, bool skipProcessChecks = false)
        {
            if (_closeTabInProgress) return false;
            _closeTabInProgress = true;

            try
            {
                var tabState = GetOrCreateTabState(tab);
                if (tabState.IsProtected)
                {
                    return false;
                }

                if (_paneZoomStateByTab.ContainsKey(tab))
                {
                    ExitPaneZoom(tab, publishEvent: true);
                }

                var layoutRoot = GetLayoutRootForTab(tab);
                if (!skipProcessChecks && layoutRoot != null)
                {
                    foreach (var pane in EnumeratePanes(layoutRoot))
                    {
                        if (!await ShouldClosePaneAsync(pane))
                        {
                            UpdateActivePane(pane);
                            FocusPaneTerminal(pane, defer: true);
                            return false;
                        }
                    }
                }

                PublishPaneEvent(tab, ResolvePaneForTab(tab), PaneAuditEventKind.Close, "tab");
                CloseTab(tab);
                return true;
            }
            finally
            {
                _closeTabInProgress = false;
            }
        }

        private void CloseTab(TabItem ti)
        {
            if (_paneZoomStateByTab.ContainsKey(ti))
            {
                ExitPaneZoom(ti, publishEvent: false);
            }

            if (_activePaneByTab.TryGetValue(ti, out var mapped) && _currentPane == mapped)
            {
                _currentPane.RecordingStateChanged -= OnRecordingStateChanged;
                _currentPane = null;
                OnRecordingStateChanged(false);
            }
            _activePaneByTab.Remove(ti);
            _paneZoomStateByTab.Remove(ti);
            _zoomedPaneIdByTab.Remove(ti);
            _broadcastEnabledTabs.Remove(ti);
            _tabIds.Remove(ti);
            _layoutModelByTab.Remove(ti);
            _tabMru.Remove(ti);
            _tabStateByTab.Remove(ti);
            _pendingVisualRefreshTabs.Remove(ti);

            if (ti.Content is Control content) DisposeControlTree(content);
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                tabs.Items.Remove(ti);
                if (tabs.Items.Count == 0) Close();
            }

            UpdateTabVisuals();
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            RefreshAllLayoutModels();
        }

        private void CloseActivePane()
        {
            _ = CloseActivePaneAsync();
        }

        private async Task CloseActivePaneAsync()
        {
            if (_closePaneInProgress || _currentPane == null) return;
            _closePaneInProgress = true;

            try
            {
                var paneToClose = _currentPane;
                if (paneToClose == null) return;
                if (TryGetSelectedTab(out var selectedTab) && _paneZoomStateByTab.ContainsKey(selectedTab))
                {
                    ExitPaneZoom(selectedTab, publishEvent: true);
                    paneToClose = _currentPane;
                    if (paneToClose == null) return;
                }
                if (!await ShouldClosePaneAsync(paneToClose))
                {
                    FocusPaneTerminal(paneToClose, defer: true);
                    return;
                }

                // Check if we are in a split (Parent is Grid with multiple children/splitter)
                if (paneToClose.Parent is Grid parentGrid && parentGrid.Children.Count >= 2)
                {
                    // We are in a split!
                    // 1. Identify Sibling (The non-splitter control that isn't us)
                    var sibling = parentGrid.Children.OfType<Control>()
                                        .FirstOrDefault(c => c != paneToClose && !(c is GridSplitter));

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
                        DisposeControlTree(paneToClose);

                        // 6. Focus Sibling
                        FocusFirstPane(sibling);
                        UpdatePaneAutomationLabels();
                        if (TryGetSelectedTab(out selectedTab))
                        {
                            RefreshLayoutModelForTab(selectedTab);
                            PublishPaneEvent(selectedTab, paneToClose, PaneAuditEventKind.Close);
                        }
                        return;
                    }
                }

                // Fallback: If not in a split, close the tab
                var tabs = this.FindControl<TabControl>("Tabs");
                if (tabs?.SelectedItem is TabItem ti)
                {
                    await CloseTabAsync(ti, skipProcessChecks: true);
                }
            }
            finally
            {
                _closePaneInProgress = false;
            }
        }

        private async Task<bool> ShouldClosePaneAsync(TerminalPane pane)
        {
            if (!pane.IsProcessRunning) return true;
            // Treat an untouched local shell as safe to close without warning.
            // This avoids false warnings when closing a newly opened, idle tab.
            if (!pane.HasUserInteraction &&
                pane.Profile?.Type != ConnectionType.SSH &&
                string.IsNullOrWhiteSpace(pane.ShellArgs))
            {
                return true;
            }

            string policy = (_settings.PaneClosePolicy ?? "Confirm").Trim().ToLowerInvariant();
            switch (policy)
            {
                case "force":
                    return true;
                case "graceful":
                    try
                    {
                        pane.Session?.SendInput("\x03");
                        pane.Session?.SendInput("exit\r");
                    }
                    catch { }

                    await Task.Delay(150);
                    if (!pane.IsProcessRunning) return true;
                    return await ShowRunningProcessCloseConfirmationAsync("Process is still running in this pane.");
                default:
                    return await ShowRunningProcessCloseConfirmationAsync("Closing this pane will terminate the running process.");
            }
        }

        private async Task<bool> ShowRunningProcessCloseConfirmationAsync(string message)
        {
            bool confirmed = false;

            var dialog = new Window
            {
                Title = "Close Running Pane",
                Width = 460,
                Height = 190,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var messageBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 92
            };
            cancelButton.Click += (_, __) =>
            {
                confirmed = false;
                dialog.Close();
            };

            var closeButton = new Button
            {
                Content = "Close Pane",
                Width = 110
            };
            closeButton.Click += (_, __) =>
            {
                confirmed = true;
                dialog.Close();
            };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "A process is still running.",
                            FontWeight = FontWeight.SemiBold
                        },
                        messageBlock,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, closeButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return confirmed;
        }

        private void DisposeControlTree(Control control)
        {
            if (control is TerminalPane pane)
            {
                UnwirePane(pane);
                Task.Run(() => { try { pane.Dispose(); } catch { } });
            }
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
            WirePane(pane);

            pane.ApplySettings(_settings);
            var tabItem = new TabItem
            {
                Header = new TextBlock { Text = profile.Name, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Padding = new Thickness(10, 4) },
                Content = pane
            };
            tabs.Items.Add(tabItem);
            tabs.SelectedItem = tabItem;
            GetTabId(tabItem);
            GetOrCreateTabState(tabItem);
            TouchTabMru(tabItem);
            _currentPane = pane;
            _activePaneByTab[tabItem] = pane;

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
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            RefreshLayoutModelForTab(tabItem);
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

        internal void UpdateTabVisuals(TabItem? specificTab = null)
        {
            _ = specificTab;
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var theme = _settings.ActiveTheme;
            var borderBrush = new SolidColorBrush(theme.Blue.ToAvaloniaColor());

            // Calculate contrasting foreground for tabs
            double luminance = (0.299 * theme.Background.R + 0.587 * theme.Background.G + 0.114 * theme.Background.B) / 255.0;
            var contrastForeground = luminance > 0.5 ? Brushes.Black : Brushes.White;

            var tabItems = tabs.Items.Cast<TabItem>().ToList();
            var labels = BuildTabDisplayLabels(tabItems, 44);

            foreach (TabItem ti in tabItems)
            {
                ti.BorderBrush = ti.IsSelected ? borderBrush : Brushes.Transparent;

                if (ti.Header is TextBlock tb)
                {
                    tb.Foreground = contrastForeground;
                    tb.Text = labels[ti];
                }
            }

            UpdateTabAutomationLabels();
            PopulateTabListMenu();
            UpdateTabHeaderViewport();
        }

        private void SplitPane(Avalonia.Layout.Orientation orientation)
        {
            if (_currentPane == null) return;
            if (TryGetSelectedTab(out var selectedTab) && _paneZoomStateByTab.ContainsKey(selectedTab))
            {
                ExitPaneZoom(selectedTab, publishEvent: true);
            }

            var originalPane = _currentPane;
            var parent = originalPane.Parent as Panel;
            var (minPaneWidth, minPaneHeight) = originalPane.GetMinimumPaneSize();

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
            WirePane(newPane);
            newPane.MinWidth = Math.Max(newPane.MinWidth, minPaneWidth);
            newPane.MinHeight = Math.Max(newPane.MinHeight, minPaneHeight);
            originalPane.MinWidth = Math.Max(originalPane.MinWidth, minPaneWidth);
            originalPane.MinHeight = Math.Max(originalPane.MinHeight, minPaneHeight);
            var grid = new Grid { Background = Brushes.Transparent, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch };

            var dividerBrush = new SolidColorBrush(Color.FromRgb(35, 35, 35)); // Even more subtle

            if (orientation == Avalonia.Layout.Orientation.Horizontal)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = minPaneWidth });
                grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel)); // 3px hit area
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = minPaneWidth });

                var splitter = new GridSplitter
                {
                    Width = 3,
                    Background = dividerBrush,
                    ResizeDirection = GridResizeDirection.Columns,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                    Focusable = false
                };
                WireSplitter(splitter, grid);
                Grid.SetColumn(splitter, 1);
                grid.Children.Add(splitter);

                Grid.SetRow(originalPane, 0); Grid.SetColumn(originalPane, 0);
                Grid.SetRow(newPane, 0); Grid.SetColumn(newPane, 2);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = minPaneHeight });
                grid.RowDefinitions.Add(new RowDefinition(3, GridUnitType.Pixel)); // 3px hit area
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = minPaneHeight });

                var splitter = new GridSplitter
                {
                    Height = 3,
                    Background = dividerBrush,
                    ResizeDirection = GridResizeDirection.Rows,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    Focusable = false
                };
                WireSplitter(splitter, grid);
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
            UpdatePaneAutomationLabels();
            if (TryGetSelectedTab(out selectedTab))
            {
                RefreshLayoutModelForTab(selectedTab);
                PublishPaneEvent(selectedTab, newPane, PaneAuditEventKind.Split,
                    orientation == Avalonia.Layout.Orientation.Horizontal ? "vertical-divider" : "horizontal-divider");
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

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null)
            {
                foreach (var tab in tabs.Items.Cast<TabItem>())
                {
                    var capturedTab = tab;
                    string label = GetTabSwitchCommandLabel(capturedTab);
                    CommandRegistry.Register(label, "Tabs", () =>
                    {
                        tabs.SelectedItem = capturedTab;
                    }, "");
                }
            }

            CommandRegistry.Register("Workspace: Save Current", "Workspace", () => _ = SaveWorkspaceInteractiveAsync(), "");
            CommandRegistry.Register("Workspace: Load...", "Workspace", () => _ = LoadWorkspaceInteractiveAsync(), "");
            foreach (var workspaceName in WorkspaceManager.ListWorkspaceNames())
            {
                string capturedName = workspaceName;
                CommandRegistry.Register($"Workspace: Load {capturedName}", "Workspace", () => LoadWorkspaceByName(capturedName), "");
            }

            CommandRegistry.Register("Close Tab", "General", () => CloseActiveTab(), "Ctrl+W");
            CommandRegistry.Register("Close Pane", "General", () => CloseActivePane(), "Ctrl+Shift+W");
            CommandRegistry.Register("Tab: Next (MRU)", "General", () => SwitchTabByMru(reverse: false), "Ctrl+Tab");
            CommandRegistry.Register("Tab: Previous (MRU)", "General", () => SwitchTabByMru(reverse: true), "Ctrl+Shift+Tab");
            CommandRegistry.Register("Tab: Open Tab List", "General", () => PopulateTabListMenu(showFlyout: true), "Ctrl+Shift+O");
            CommandRegistry.Register("Tab: Rename Current", "General", () => _ = RenameSelectedTabAsync(), "");
            CommandRegistry.Register("Tab: Copy Current Title", "General", () => _ = CopySelectedTabTitleAsync(), "");
            CommandRegistry.Register("Tab: Close Others", "General", () => _ = CloseOtherTabsAsync(), "");
            CommandRegistry.Register("Tab: Toggle Pin", "General", () => TogglePinSelectedTab(), "");
            CommandRegistry.Register("Tab: Toggle Protect", "General", () => ToggleProtectSelectedTab(), "");
            // Keep command naming aligned with common terminal UX:
            // Vertical split => vertical divider => side-by-side panes.
            CommandRegistry.Register("Split Vertical", "View", () => SplitPane(Avalonia.Layout.Orientation.Horizontal), "Ctrl+Shift+D");
            // Horizontal split => horizontal divider => stacked panes.
            CommandRegistry.Register("Split Horizontal", "View", () => SplitPane(Avalonia.Layout.Orientation.Vertical), "Ctrl+Shift+E");
            CommandRegistry.Register("Equalize Panes", "View", () => EqualizeCurrentSplit(), "Ctrl+Shift+G");
            CommandRegistry.Register("Pane: Toggle Zoom", "View", () => TogglePaneZoomForCurrentTab(), "Ctrl+Shift+Z");
            CommandRegistry.Register("Pane: Toggle Broadcast Input (Tab)", "View", () => ToggleBroadcastForCurrentTab(), "Ctrl+Shift+B");
            CommandRegistry.Register("Focus Pane Left", "View", () => NavigatePane(MoveDirection.Left), "Alt+Left");
            CommandRegistry.Register("Focus Pane Right", "View", () => NavigatePane(MoveDirection.Right), "Alt+Right");
            CommandRegistry.Register("Focus Pane Up", "View", () => NavigatePane(MoveDirection.Up), "Alt+Up");
            CommandRegistry.Register("Focus Pane Down", "View", () => NavigatePane(MoveDirection.Down), "Alt+Down");
            CommandRegistry.Register("Find in Terminal", "Edit", () => _currentPane?.ToggleSearch(), "Ctrl+Shift+F");
            CommandRegistry.Register("Paste", "Edit", () => _ = PasteFromClipboardAsync(), "Ctrl+V");
            CommandRegistry.Register("Font: Increase", "View", () => { _settings.FontSize++; ApplySettingsToAllTabs(); _settings.Save(); }, "Ctrl++");
            CommandRegistry.Register("Font: Decrease", "View", () => { _settings.FontSize = Math.Max(6, _settings.FontSize - 1); ApplySettingsToAllTabs(); _settings.Save(); }, "Ctrl+-");
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

            // Cursor UX
            CommandRegistry.Register("Cursor: Block", "View", () => { _settings.CursorStyle = "Block"; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Cursor: Beam", "View", () => { _settings.CursorStyle = "Beam"; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Cursor: Underline", "View", () => { _settings.CursorStyle = "Underline"; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Cursor: Toggle Blink", "View", () => { _settings.CursorBlink = !_settings.CursorBlink; ApplySettingsToAllTabs(); _settings.Save(); }, "");

            // Bell UX
            CommandRegistry.Register("Bell: Toggle Audio", "View", () => { _settings.BellAudioEnabled = !_settings.BellAudioEnabled; ApplySettingsToAllTabs(); _settings.Save(); }, "");
            CommandRegistry.Register("Bell: Toggle Visual Flash", "View", () => { _settings.BellVisualEnabled = !_settings.BellVisualEnabled; ApplySettingsToAllTabs(); _settings.Save(); }, "");

            // Scrolling UX
            CommandRegistry.Register("Scroll: Toggle Smooth", "View", () => { _settings.SmoothScrolling = !_settings.SmoothScrolling; ApplySettingsToAllTabs(); _settings.Save(); }, "");
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
                    SetupCommandPalette();
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
            _settings.ThemeName = theme;
            // Force reload themes to pick up any changes from settings window.
            _settings.ThemeManager.ReloadThemes();
            _settings.RefreshActiveTheme();
            ApplyThemeToUI();
            ApplySettingsToAllTabs();
            UpdateTabVisuals();
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

        private void UpdateActivePane(TerminalPane pane)
        {
            var ownerTab = pane.FindAncestorOfType<TabItem>();
            if (ownerTab != null) _activePaneByTab[ownerTab] = pane;

            if (_currentPane == pane) return;

            // Unsubscribe from old pane
            if (_currentPane != null)
            {
                _currentPane.RecordingStateChanged -= OnRecordingStateChanged;
            }

            _currentPane = pane;

            // Subscribe to new pane
            if (_currentPane != null)
            {
                _currentPane.RecordingStateChanged += OnRecordingStateChanged;
            }

            // Initial UI sync
            OnRecordingStateChanged(_currentPane?.IsRecording ?? false);
            UpdatePaneAutomationLabels();
            if (ownerTab != null)
            {
                RefreshLayoutModelForTab(ownerTab);
                PublishPaneEvent(ownerTab, pane, PaneAuditEventKind.FocusChanged);
            }
        }

        private TerminalPane? FindFirstPane(Control? control)
        {
            if (control == null) return null;
            if (control is TerminalPane pane) return pane;

            if (control is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Control cc)
                    {
                        var found = FindFirstPane(cc);
                        if (found != null) return found;
                    }
                }
            }
            else if (control is ContentControl contentControl)
            {
                return FindFirstPane(contentControl.Content as Control);
            }

            return null;
        }

        private TerminalPane? ResolvePaneForTab(TabItem tabItem)
        {
            if (_activePaneByTab.TryGetValue(tabItem, out var pane))
            {
                // Ignore stale mappings when pane was removed/disposed.
                if (pane.FindAncestorOfType<TabItem>() == tabItem)
                {
                    return pane;
                }
            }

            if (tabItem.Content is Control content)
            {
                var first = FindFirstPane(content);
                if (first != null)
                {
                    _activePaneByTab[tabItem] = first;
                    return first;
                }
            }

            return null;
        }

        private bool IsFocusOverlayVisible()
        {
            var paletteOverlay = this.FindControl<Grid>("CommandPaletteOverlay");
            if (paletteOverlay?.IsVisible == true) return true;

            var connectionOverlay = this.FindControl<Border>("ConnectionOverlay");
            if (connectionOverlay?.IsVisible == true) return true;

            var transferOverlay = this.FindControl<Border>("TransferOverlay");
            if (transferOverlay?.IsVisible == true) return true;

            return false;
        }

        private void FocusPaneTerminal(TerminalPane pane, bool defer)
        {
            if (IsFocusOverlayVisible()) return;

            void FocusNow() => pane.ActiveControl.Focus();

            if (defer)
            {
                Dispatcher.UIThread.Post(FocusNow, DispatcherPriority.Input);
                Dispatcher.UIThread.Post(FocusNow, DispatcherPriority.Loaded);
            }
            else
            {
                FocusNow();
            }
        }

        private void FocusCurrentTerminal(bool defer)
        {
            if (_currentPane == null) return;
            FocusPaneTerminal(_currentPane, defer);
        }

        private void OnRecordingStateChanged(bool isRecording)
        {
            var btnRecord = this.FindControl<Button>("BtnRecord");
            var iconRecord = this.FindControl<PathIcon>("IconRecord");

            if (btnRecord == null || iconRecord == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (isRecording)
                {
                    btnRecord.Foreground = Brushes.Red;
                    // Pulse animation or just red for now
                }
                else
                {
                    btnRecord.Foreground = SolidColorBrush.Parse("#CCCCCC");
                }
            });
        }
    }
}
