using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using NovaTerminal.Shell;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Automation;
using Avalonia.Input.Platform;
using SkiaSharp;
using NovaTerminal.Pty;

using NovaTerminal.Controls;
using NovaTerminal.Services.Ssh;
using NovaTerminal.Platform.Ssh.Launch;
using NovaTerminal.Shell.Shortcuts;
using NovaTerminal.Models;
using NovaTerminal.ViewModels.Ssh;
using NovaTerminal.Views.Ssh;
using NovaTerminal.Pty;

namespace NovaTerminal
{
    public partial class MainWindow : Window
    {
        internal readonly record struct ShellOpenRequest(string FileName, string? Arguments);
        private const string SplitterHoverClass = "splitter-hover";
        private const string SplitterDraggingClass = "splitter-dragging";

        private TerminalPane? _currentPaneValue;
        private TerminalPane? _currentPane
        {
            get => _currentPaneValue;
            set
            {
                if (_currentPaneValue != value)
                {
                    if (_currentPaneValue != null) _currentPaneValue.IsActivePane = false;
                    _currentPaneValue = value;
                    if (_currentPaneValue != null) _currentPaneValue.IsActivePane = true;
                }
            }
        }
        private readonly Dictionary<TabItem, TerminalPane> _activePaneByTab = new();
        private readonly Dictionary<TabItem, PaneZoomState> _paneZoomStateByTab = new();
        private readonly Dictionary<TabItem, Guid> _zoomedPaneIdByTab = new();
        private readonly HashSet<TabItem> _broadcastEnabledTabs = new();
        private readonly Dictionary<TabItem, Guid> _tabIds = new();
        private readonly Dictionary<TerminalPane, TabItem> _paneOwnerTab = new();
        private readonly Dictionary<TabItem, PaneLayoutModel> _layoutModelByTab = new();
        private readonly List<TabItem> _tabMru = new();
        private bool _windowIconLoaded;
        private readonly Dictionary<TabItem, TabRuntimeState> _tabStateByTab = new();
        private readonly HashSet<TabItem> _pendingVisualRefreshTabs = new();
        private bool _suppressMruTouchOnSelection;
        private bool _tabVisualRefreshScheduled;
        private TerminalSettings _settings;
        private GlobalHotkey? _globalHotkey;
        private bool _closePaneInProgress;
        private bool _closeTabInProgress;
        private readonly SshConnectionService _sshConnectionService;
        private readonly ISshInteractionService _sshInteractionService;
        private readonly SshLegacyProfileMigrationService _sshLegacyMigrationService;
        private static readonly TimeSpan BellDebounceWindow = TimeSpan.FromMilliseconds(750);
        internal const double MinimumTabHeaderRightReserve = 440;
        internal const double MacOsTrafficLightReserve = 92;
        internal const double TabHeaderViewportPadding = 16;
        private bool _isDraggingTransferOverlay;
        private Point _transferOverlayDragStart;
        private Point _transferOverlayOffsetStart;
        private TranslateTransform? _transferOverlayTransform;
        private readonly DispatcherTimer _recordingToastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
        private string? _recordingToastFolderPath;
        private string? _recordingToastFilePath;
        private ConnectionManager? _connectionManagerControl;
        private TransferCenter? _transferCenterControl;
        private readonly CommandPaletteUsageStore _commandPaletteUsageStore;
        private Dictionary<string, CommandPaletteUsageEntry> _commandPaletteUsage = new(StringComparer.OrdinalIgnoreCase);
        private readonly StartupOrchestrator _startup;

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

        internal enum TabHeaderPointerAction
        {
            None,
            OpenContextMenu,
            CloseTab
        }

        private sealed class SessionRestoreAbortedException : Exception
        {
            public SessionRestoreAbortedException(string message) : base(message) { }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _startup.Mark(StartupPhase.WindowOpened);
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

            // Reap leftover clipboard-paste temp images from previous runs (best-effort).
            System.Threading.Tasks.Task.Run(() =>
                NovaTerminal.Platform.Input.ClipboardImage.CleanUpOldTempImages(TimeSpan.FromHours(24)));
        }

        private void ToggleConnections()
        {
            var overlay = this.FindControl<Border>("ConnectionOverlay");

            if (overlay != null)
            {
                overlay.IsVisible = !overlay.IsVisible;
                if (overlay.IsVisible)
                {
                    var connManager = EnsureConnectionManagerControl();
                    if (connManager == null)
                    {
                        return;
                    }

                    connManager.LoadProfiles(_sshConnectionService.GetConnectionProfiles());
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

        private void TopLevel_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.GetPosition(this).Y <= 36)
                {
                    BeginMoveDrag(e);
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
            return ShortcutMatcher.Matches(e, GetEffectiveShortcutBinding(id, fallback));
        }

        private string GetEffectiveShortcutBinding(string id, string fallback)
        {
            if (_settings.Keybindings != null &&
                _settings.Keybindings.TryGetValue(id, out var custom) &&
                !string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            return fallback;
        }

        internal static bool TryOpenCommandAssistHelp(TerminalPane? pane)
        {
            return pane?.OpenCommandAssistHelp() == true;
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

        internal static bool CanCloseTab(bool isProtected)
        {
            return !isProtected;
        }

        internal static TabHeaderPointerAction ResolveTabHeaderPointerAction(bool isMiddlePressed, bool isRightPressed)
        {
            if (isMiddlePressed)
            {
                return TabHeaderPointerAction.CloseTab;
            }

            if (isRightPressed)
            {
                return TabHeaderPointerAction.OpenContextMenu;
            }

            return TabHeaderPointerAction.None;
        }

        internal static bool ShouldDeferTabContextMenuOpen(bool wasSelected)
        {
            return !wasSelected;
        }

        internal static bool ShouldSkipTabWhenClosingOthers(bool isPinned, bool isProtected)
        {
            return isPinned || isProtected;
        }

        internal static string GetPinTabActionLabel(bool isPinned)
        {
            return isPinned ? "Unpin Tab" : "Pin Tab";
        }

        internal static string GetProtectTabActionLabel(bool isProtected)
        {
            return isProtected ? "Unprotect Tab" : "Protect Tab";
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

            int targetIndex = GetNextMruIndex(selectedIndex, _tabMru.Count, reverse);
            if (targetIndex < 0) return false;

            var target = _tabMru[targetIndex];
            if (tabs.SelectedItem == target) return false;

            _suppressMruTouchOnSelection = true;
            tabs.SelectedItem = target;
            return true;
        }

        internal static int GetNextMruIndex(int selectedIndex, int mruCount, bool reverse)
        {
            if (mruCount < 2 || selectedIndex < 0 || selectedIndex >= mruCount)
            {
                return -1;
            }

            return reverse
                ? (selectedIndex - 1 + mruCount) % mruCount
                : (selectedIndex + 1) % mruCount;
        }

        private static TextBlock? FindTabHeaderTextBlock(object? header)
        {
            return header switch
            {
                TextBlock tb => tb,
                Border border => FindTabHeaderTextBlock(border.Child),
                ContentControl contentControl => FindTabHeaderTextBlock(contentControl.Content),
                Panel panel => panel.Children.Select(child => FindTabHeaderTextBlock(child)).FirstOrDefault(tb => tb != null),
                Decorator decorator => FindTabHeaderTextBlock(decorator.Child),
                _ => null
            };
        }

        private string GetTabHeaderText(TabItem tab)
        {
            if (FindTabHeaderTextBlock(tab.Header) is TextBlock tb)
            {
                return string.IsNullOrWhiteSpace(tb.Text) ? "Terminal" : tb.Text;
            }

            return "Terminal";
        }

        private Border CreateTabHeaderHost(TabItem tab, string text)
        {
            var headerText = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            var headerHost = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(10, 4),
                Child = headerText
            };

            headerHost.ContextFlyout = new MenuFlyout();
            headerHost.PointerPressed += (_, e) => OnTabHeaderPointerPressed(tab, e);
            ToolTip.SetTip(headerHost, text);
            return headerHost;
        }

        private void ConfigureTabHeader(TabItem tab, string text)
        {
            tab.Header = CreateTabHeaderHost(tab, text);
        }

        private void OnTabHeaderPointerPressed(TabItem tab, PointerPressedEventArgs e)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            bool wasSelected = tabs?.SelectedItem == tab;
            if (tabs != null && tabs.SelectedItem != tab)
            {
                tabs.SelectedItem = tab;
            }

            var properties = e.GetCurrentPoint(this).Properties;
            var action = ResolveTabHeaderPointerAction(
                properties.IsMiddleButtonPressed,
                properties.IsRightButtonPressed);

            if (action == TabHeaderPointerAction.CloseTab)
            {
                e.Handled = true;
                _ = CloseTabAsync(tab);
                return;
            }

            if (action == TabHeaderPointerAction.OpenContextMenu)
            {
                if (FindTabHeaderHost(tab) is Control headerHost &&
                    headerHost.ContextFlyout is MenuFlyout flyout)
                {
                    e.Handled = true;
                    ShowTabContextMenu(tab, headerHost, flyout, ShouldDeferTabContextMenuOpen(wasSelected));
                }
            }
        }

        private void ShowTabContextMenu(TabItem tab, Control headerHost, MenuFlyout flyout, bool defer)
        {
            void open()
            {
                PopulateTabContextMenu(flyout, tab);
                flyout.ShowAt(headerHost);
            }

            if (defer)
            {
                Dispatcher.UIThread.Post(open, DispatcherPriority.Input);
            }
            else
            {
                open();
            }
        }

        private void PopulateTabContextMenu(MenuFlyout flyout, TabItem tab)
        {
            flyout.Items.Clear();
            if (!_tabStateByTab.ContainsKey(tab) && !TryEnsureLiveTab(tab))
            {
                return;
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs != null && tabs.SelectedItem != tab)
            {
                tabs.SelectedItem = tab;
            }

            var state = GetOrCreateTabState(tab);
            bool hasClosableOthers = tabs?.Items
                .Cast<TabItem>()
                .Any(other => other != tab && !ShouldSkipTabWhenClosingOthers(IsTabPinned(other), IsTabProtected(other))) == true;

            var closeItem = new MenuItem
            {
                Header = "Close",
                IsEnabled = CanCloseTab(state.IsProtected)
            };
            closeItem.Click += async (_, __) => await CloseTabAsync(tab);
            flyout.Items.Add(closeItem);

            var closeOthersItem = new MenuItem
            {
                Header = "Close Others",
                IsEnabled = hasClosableOthers
            };
            closeOthersItem.Click += async (_, __) => await CloseOtherTabsAsync(tab);
            flyout.Items.Add(closeOthersItem);

            flyout.Items.Add(new Separator());

            var renameItem = new MenuItem { Header = "Rename..." };
            renameItem.Click += async (_, __) => await RenameTabAsync(tab);
            flyout.Items.Add(renameItem);

            var copyTitleItem = new MenuItem { Header = "Copy Title" };
            copyTitleItem.Click += async (_, __) => await CopyTabTitleAsync(tab);
            flyout.Items.Add(copyTitleItem);

            flyout.Items.Add(new Separator());

            var pinItem = new MenuItem { Header = GetPinTabActionLabel(state.IsPinned) };
            pinItem.Click += (_, __) => TogglePinTab(tab);
            flyout.Items.Add(pinItem);

            var protectItem = new MenuItem { Header = GetProtectTabActionLabel(state.IsProtected) };
            protectItem.Click += (_, __) => ToggleProtectTab(tab);
            flyout.Items.Add(protectItem);
        }

        private bool TryEnsureLiveTab(TabItem tab)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            return tabs?.Items.Cast<TabItem>().Contains(tab) == true;
        }

        private static Control? FindTabHeaderHost(TabItem tab)
        {
            return tab.Header as Control;
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

        internal static Thickness GetTabHeaderViewportMargin(
            bool isMacOs,
            double titleBarWidth,
            double titleBarRightMargin,
            double minimumRightReserve = MinimumTabHeaderRightReserve,
            double macLeftReserve = MacOsTrafficLightReserve,
            double viewportPadding = TabHeaderViewportPadding)
        {
            double reservedLeft = isMacOs ? macLeftReserve : 0;

            // Before the title bar has measured, fall back to the static minimum so the first paint
            // doesn't crowd tabs against the buttons. Once we have a real bound, trust it — the floor
            // was sized for Windows (custom buttons + 140px caption reserve) and overshoots on macOS,
            // where the caption lives on the left and titleBarRightMargin is small.
            double reservedRight = titleBarWidth > 0
                ? Math.Ceiling(titleBarWidth + Math.Max(0, titleBarRightMargin) + viewportPadding)
                : minimumRightReserve;

            return new Thickness(reservedLeft, 0, reservedRight, 0);
        }

        private void UpdateTabHeaderViewport()
        {
            var scrollViewer = FindTabHeaderScrollViewer();
            var titleBar = this.FindControl<Grid>("TitleBar");
            if (scrollViewer == null) return;

            scrollViewer.Margin = GetTabHeaderViewportMargin(
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
                titleBar?.Bounds.Width ?? 0,
                titleBar?.Margin.Right ?? 0);
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

            int hiddenCount = CountHiddenTabs(viewportWidth, tabs.Items.Cast<TabItem>().Select(t => t.Bounds.Width));

            badge.IsVisible = hiddenCount > 0;
            badge.Text = hiddenCount > 0 ? $"+{hiddenCount}" : string.Empty;
            ToolTip.SetTip(button, hiddenCount > 0 ? $"Tab List ({hiddenCount} hidden)" : "Tab List");
            button.Foreground = hiddenCount > 0 ? new SolidColorBrush(Color.FromRgb(255, 210, 90)) : Brushes.White;
        }

        internal static int CountHiddenTabs(double viewportWidth, IEnumerable<double> tabWidths, double fallbackTabWidth = 120)
        {
            if (viewportWidth <= 0) return 0;

            double usedWidth = 0;
            int hiddenCount = 0;
            foreach (double width in tabWidths)
            {
                double tabWidth = width > 0 ? width : fallbackTabWidth;
                if (usedWidth + tabWidth <= viewportWidth + 0.5)
                {
                    usedWidth += tabWidth;
                }
                else
                {
                    hiddenCount++;
                }
            }

            return hiddenCount;
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

                bool canCloseCurrent = tabs.SelectedItem is not TabItem currentTab || CanCloseTab(IsTabProtected(currentTab));
                var closeCurrentItem = new MenuItem
                {
                    Header = "Close Current Tab",
                    IsEnabled = canCloseCurrent
                };
                closeCurrentItem.Click += async (_, __) => await CloseSelectedTabAsync();
                flyout.Items.Add(closeCurrentItem);

                bool hasClosableOthers = tabs.SelectedItem is TabItem selectedForCloseOthers &&
                    tabs.Items.Cast<TabItem>().Any(t => t != selectedForCloseOthers && !ShouldSkipTabWhenClosingOthers(IsTabPinned(t), IsTabProtected(t)));
                var closeOthersItem = new MenuItem
                {
                    Header = "Close Other Tabs",
                    IsEnabled = hasClosableOthers
                };
                closeOthersItem.Click += async (_, __) => await CloseOtherTabsAsync();
                flyout.Items.Add(closeOthersItem);

                if (tabs.SelectedItem is TabItem selectedTab)
                {
                    var selectedState = GetOrCreateTabState(selectedTab);

                    var pinItem = new MenuItem { Header = GetPinTabActionLabel(selectedState.IsPinned) };
                    pinItem.Click += (_, __) => TogglePinSelectedTab();
                    flyout.Items.Add(pinItem);

                    var protectItem = new MenuItem { Header = GetProtectTabActionLabel(selectedState.IsProtected) };
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

        internal static string TruncateTabLabel(string value, int maxLength = 40)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            if (maxLength < 5) return value.Substring(0, maxLength);
            return value.Substring(0, maxLength - 1) + "…";
        }

        internal static string TruncateTabLabelWithSuffix(string value, int maxLength, string suffix)
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
            return ResolveTabPrimaryTitle(state.UserTitle, pane?.GetBaseTabTitle(), null);
        }

        internal static string ResolveTabPrimaryTitle(string? userTitle, string? paneBaseTitle, string? fallbackHeader)
        {
            if (!string.IsNullOrWhiteSpace(userTitle))
            {
                return userTitle;
            }

            if (!string.IsNullOrWhiteSpace(paneBaseTitle))
            {
                return paneBaseTitle;
            }

            if (!string.IsNullOrWhiteSpace(fallbackHeader))
            {
                return fallbackHeader;
            }

            return "Terminal";
        }

        internal string GetTabPersistedTitle(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            var pane = ResolvePaneForTab(tab);
            return ResolveTabPrimaryTitle(state.UserTitle, pane?.GetBaseTabTitle(), GetTabHeaderText(tab));
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
            await CopyTabTitleAsync(tab);
        }

        private async Task CopyTabTitleAsync(TabItem tab)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null) return;

            string title = GetTabPrimaryTitle(tab);
            await topLevel.Clipboard.SetTextAsync(title);
        }

        private async Task<string?> ShowTextPromptAsync(string title, string prompt, string defaultValue)
        {
            string? result = null;
            var dialog = CreateThemedDialogWindow(title, 520, 190, canResize: false);

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

        private Window CreateThemedDialogWindow(string title, double width, double height, bool canResize)
        {
            var dialog = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                CanResize = canResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            ApplyThemeToDialogWindow(dialog);
            return dialog;
        }

        private void ApplyThemeToDialogWindow(Window dialog)
        {
            var theme = _settings.ActiveTheme;
            var contrast = theme.GetContrastForeground();
            dialog.Background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
            dialog.Foreground = new SolidColorBrush(contrast.ToAvaloniaColor());
            dialog.RequestedThemeVariant = contrast == TermColor.Black ? ThemeVariant.Light : ThemeVariant.Dark;
        }

        private async Task RenameSelectedTabAsync()
        {
            if (!TryGetSelectedTab(out var tab)) return;
            await RenameTabAsync(tab);
        }

        private async Task RenameTabAsync(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            string current = state.UserTitle ?? GetTabPrimaryTitle(tab);
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
            if (!TryGetSelectedTab(out var selected)) return;
            await CloseOtherTabsAsync(selected);
        }

        private async Task CloseOtherTabsAsync(TabItem selected)
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var others = tabs.Items.Cast<TabItem>().Where(t => t != selected).ToList();
            foreach (var tab in others)
            {
                if (ShouldSkipTabWhenClosingOthers(IsTabPinned(tab), IsTabProtected(tab))) continue;
                await CloseTabAsync(tab);
            }
        }

        private void TogglePinSelectedTab()
        {
            if (!TryGetSelectedTab(out var tab)) return;
            TogglePinTab(tab);
        }

        private void ToggleProtectSelectedTab()
        {
            if (!TryGetSelectedTab(out var tab)) return;
            ToggleProtectTab(tab);
        }

        private void TogglePinTab(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            state.IsPinned = !state.IsPinned;
            UpdateTabVisuals(tab);
            PopulateTabListMenu();
        }

        private void ToggleProtectTab(TabItem tab)
        {
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

        private bool TryRestoreStartupSession(TabControl tabs)
        {
            if (!SessionManager.TryLoadSavedSession(out NovaSession? session) ||
                session == null ||
                session.Tabs.Count == 0)
            {
                return false;
            }
            _startup.Checkpoint("StartupRestore.AfterSessionLoad");

            try
            {
                _startup.BeginSessionRestore(session, immediate =>
                {
                    tabs.Items.Clear();

                    for (int index = 0; index < session.Tabs.Count; index++)
                    {
                        TabSession tabSession = session.Tabs[index];
                        TabItem? tabItem = index == immediate.OriginalIndex
                            ? SessionManager.CreateRestoredTabItem(tabSession, _settings)
                            : CreateStartupPlaceholderTab(tabSession);

                        if (tabItem != null)
                        {
                            tabs.Items.Add(tabItem);
                        }
                    }
                    _startup.Checkpoint("StartupRestore.AfterTabMaterialization");

                    if (tabs.Items.Count == 0)
                    {
                        throw new SessionRestoreAbortedException(
                            "Session restore produced no tab items; aborting restore.");
                    }

                    if (immediate.OriginalIndex >= 0 && immediate.OriginalIndex < tabs.Items.Count)
                    {
                        tabs.SelectedIndex = immediate.OriginalIndex;
                    }

                    InitializeRestoredTabs(tabs);
                    _startup.Checkpoint("StartupRestore.AfterInitializeRestoredTabs");
                });
            }
            catch (SessionRestoreAbortedException ex)
            {
                TerminalLogger.Log($"TryRestoreStartupSession: aborted ({ex.Message})");
                return false;
            }

            return true;
        }

        private TabItem CreateStartupPlaceholderTab(TabSession tabSession)
        {
            return new TabItem
            {
                Header = new TextBlock
                {
                    Text = tabSession.Title,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(10, 4)
                },
                Content = new Border { Background = Brushes.Transparent },
                Tag = tabSession
            };
        }

        private void HydrateDeferredStartupTab(TabControl tabs, StartupRestoreTab deferredTab)
        {
            if (deferredTab.OriginalIndex < 0 || deferredTab.OriginalIndex >= tabs.Items.Count)
            {
                return;
            }

            if (tabs.Items[deferredTab.OriginalIndex] is not TabItem tabItem)
            {
                return;
            }

            Control? content = SessionManager.CreateRestoredTabContent(deferredTab.Tab, _settings);
            if (content == null)
            {
                return;
            }

            tabItem.Content = content;
            tabItem.Tag = deferredTab.Tab;
            InitializeRestoredTabs(tabs);
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

        private async Task SaveWorkspaceTemplateInteractiveAsync()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            string suggested = $"template-{DateTime.Now:yyyyMMdd-HHmm}";
            var name = await ShowTextPromptAsync("Save Workspace Template", "Template name", suggested);
            if (string.IsNullOrWhiteSpace(name)) return;

            var snapshot = SessionManager.CaptureSession(this, tabs);
            if (WorkspaceManager.SaveWorkspaceTemplate(name.Trim(), snapshot))
            {
                SetupCommandPalette();
            }
        }

        private async Task LoadWorkspaceTemplateInteractiveAsync()
        {
            var names = WorkspaceManager.ListWorkspaceTemplateNames();
            if (names.Count == 0) return;

            var first = names[0];
            var name = await ShowTextPromptAsync("Apply Workspace Template", "Template name", first);
            if (string.IsNullOrWhiteSpace(name)) return;
            ApplyWorkspaceTemplateByName(name.Trim());
        }

        private async Task ExportWorkspaceBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleExport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export bundle blocked by policy.");
                return;
            }

            var names = WorkspaceManager.ListWorkspaceNames();
            if (names.Count == 0) return;

            string defaultName = names[0];
            string? name = await ShowTextPromptAsync("Export Workspace Bundle", "Workspace name", defaultName);
            if (string.IsNullOrWhiteSpace(name)) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            string suggestedFileName = $"{name.Trim()}.novaws.json";
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Workspace Bundle",
                SuggestedFileName = suggestedFileName,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Nova Workspace Bundle") { Patterns = new[] { "*.novaws.json", "*.json" } }
                }
            });

            if (file == null) return;

            bool ok = WorkspaceManager.ExportWorkspaceBundle(name.Trim(), file.Path.LocalPath, Environment.UserName);
            if (!ok)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export bundle failed.");
            }
        }

        private async Task ExportCurrentSessionBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleExport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export bundle blocked by policy.");
                return;
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            string suggested = $"session-{DateTime.Now:yyyyMMdd-HHmm}";
            string? label = await ShowTextPromptAsync("Export Session Bundle", "Bundle name", suggested);
            if (string.IsNullOrWhiteSpace(label)) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            string suggestedFileName = $"{label.Trim()}.novaws.json";
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Session Bundle",
                SuggestedFileName = suggestedFileName,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Nova Workspace Bundle") { Patterns = new[] { "*.novaws.json", "*.json" } }
                }
            });

            if (file == null) return;

            var snapshot = SessionManager.CaptureSession(this, tabs);
            bool ok = WorkspaceManager.ExportWorkspaceBundle(label.Trim(), snapshot, file.Path.LocalPath, Environment.UserName);
            if (!ok)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Export current session bundle failed.");
            }
        }

        private async Task ImportWorkspaceBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleImport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Import bundle blocked by policy.");
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Workspace Bundle",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Nova Workspace Bundle") { Patterns = new[] { "*.novaws.json", "*.json" } }
                }
            });

            if (files.Count == 0) return;

            string bundlePath = files[0].Path.LocalPath;
            string suggestedName = Path.GetFileNameWithoutExtension(bundlePath);
            if (suggestedName.EndsWith(".novaws", StringComparison.OrdinalIgnoreCase))
            {
                suggestedName = Path.GetFileNameWithoutExtension(suggestedName);
            }

            string? name = await ShowTextPromptAsync("Import Workspace Bundle", "Workspace name", suggestedName);
            if (string.IsNullOrWhiteSpace(name)) return;

            // Import only STORES the bundle; its commands are confirmed at execution time
            // in LoadWorkspaceByName. Confirming here would be a TOCTOU (the file could
            // change between this read and ImportWorkspaceBundle's re-read) and wouldn't
            // cover later loads — so the single confirmation gate lives at execution (#171).
            if (WorkspaceManager.ImportWorkspaceBundle(bundlePath, name.Trim(), out var error))
            {
                SetupCommandPalette();
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Workspace] Import bundle failed: {error}");
        }

        private async Task OpenWorkspaceBundleInteractiveAsync()
        {
            if (!WorkspacePolicyManager.Current.AllowWorkspaceBundleImport)
            {
                System.Diagnostics.Debug.WriteLine("[Workspace] Open bundle blocked by policy.");
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Workspace Bundle",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Nova Workspace Bundle") { Patterns = new[] { "*.novaws.json", "*.json" } }
                }
            });

            if (files.Count == 0) return;

            string bundlePath = files[0].Path.LocalPath;
            bool ok = WorkspaceManager.LoadWorkspaceBundleSession(bundlePath, out var _workspaceName, out var snapshot, out var error);
            if (ok && snapshot != null)
            {
                // This spawns the bundle's stored commands immediately — confirm first
                // for a foreign .novaws.json opened from disk (#171).
                if (!await ConfirmBundleCommandsAsync(snapshot, _workspaceName ?? "workspace"))
                {
                    return;
                }
                ApplySessionSnapshot(snapshot);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Workspace] Open bundle failed: {error}");
        }

        private async void LoadWorkspaceByName(string name)
        {
            var snapshot = WorkspaceManager.LoadWorkspace(name);
            if (snapshot == null) return;

            // Confirm ad-hoc commands against the exact stored snapshot that is about to
            // run — this is the single execution-time gate (no TOCTOU), and it covers
            // imported bundles whichever way they're loaded (#171). Profile-only
            // workspaces collect no commands and skip the prompt.
            if (!await ConfirmBundleCommandsAsync(snapshot, name))
            {
                return;
            }
            ApplySessionSnapshot(snapshot);
        }

        private void ApplyWorkspaceTemplateByName(string name)
        {
            var snapshot = WorkspaceManager.LoadWorkspaceTemplate(name);
            if (snapshot == null) return;
            ApplySessionSnapshot(snapshot);
        }

        internal static TabTemplateRule? FindTabTemplateRule(IEnumerable<TabTemplateRule>? rules, Guid profileId)
        {
            if (rules == null) return null;
            return rules.FirstOrDefault(r =>
                r != null &&
                r.Enabled &&
                r.ProfileId == profileId &&
                !string.IsNullOrWhiteSpace(r.TemplateName));
        }

        internal static bool UpsertTabTemplateRule(List<TabTemplateRule> rules, Guid profileId, string templateName)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (string.IsNullOrWhiteSpace(templateName)) return false;

            var existing = rules.FirstOrDefault(r => r.ProfileId == profileId);
            if (existing == null)
            {
                rules.Add(new TabTemplateRule
                {
                    ProfileId = profileId,
                    TemplateName = templateName.Trim(),
                    Enabled = true
                });
                return true;
            }

            existing.TemplateName = templateName.Trim();
            existing.Enabled = true;
            return true;
        }

        internal static bool RemoveTabTemplateRule(List<TabTemplateRule> rules, Guid profileId)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            int before = rules.Count;
            rules.RemoveAll(r => r.ProfileId == profileId);
            return rules.Count != before;
        }

        private async Task SetTemplateRuleForCurrentPaneProfileAsync()
        {
            var profile = _currentPane?.Profile;
            if (profile == null) return;

            var templateNames = WorkspaceManager.ListWorkspaceTemplateNames();
            if (templateNames.Count == 0) return;

            string suggested = FindTabTemplateRule(_settings.TabTemplateRules, profile.Id)?.TemplateName ?? templateNames[0];
            string? name = await ShowTextPromptAsync(
                "Set Tab Template Rule",
                $"Template for profile '{profile.Name}'",
                suggested);
            if (string.IsNullOrWhiteSpace(name)) return;

            string trimmed = name.Trim();
            if (WorkspaceManager.LoadWorkspaceTemplate(trimmed) == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Workspace] Template rule not set: missing template '{trimmed}'.");
                return;
            }

            if (UpsertTabTemplateRule(_settings.TabTemplateRules, profile.Id, trimmed))
            {
                _settings.Save();
                SetupCommandPalette();
            }
        }

        private void ClearTemplateRuleForCurrentPaneProfile()
        {
            var profile = _currentPane?.Profile;
            if (profile == null) return;

            if (RemoveTabTemplateRule(_settings.TabTemplateRules, profile.Id))
            {
                _settings.Save();
                SetupCommandPalette();
            }
        }

        private bool TryApplyTemplateRuleForProfile(TerminalProfile profile)
        {
            var rule = FindTabTemplateRule(_settings.TabTemplateRules, profile.Id);
            if (rule == null) return false;

            var template = WorkspaceManager.LoadWorkspaceTemplate(rule.TemplateName);
            if (template == null || template.Tabs.Count == 0) return false;

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return false;

            var current = SessionManager.CaptureSession(this, tabs);
            var templateTab = CloneTabSession(template.Tabs[0]);
            current.Tabs.Add(templateTab);
            current.ActiveTabIndex = current.Tabs.Count - 1;
            ApplySessionSnapshot(current);
            return true;
        }

        private static TabSession CloneTabSession(TabSession source)
        {
            return new TabSession
            {
                TabId = source.TabId,
                Title = source.Title,
                UserTitle = source.UserTitle,
                IsPinned = source.IsPinned,
                IsProtected = source.IsProtected,
                ActivePaneId = source.ActivePaneId,
                ZoomedPaneId = source.ZoomedPaneId,
                BroadcastInputEnabled = source.BroadcastInputEnabled,
                Root = ClonePaneNode(source.Root)
            };
        }

        private static PaneNode? ClonePaneNode(PaneNode? source)
        {
            if (source == null) return null;

            return new PaneNode
            {
                Type = source.Type,
                SplitOrientation = source.SplitOrientation,
                ProfileId = source.ProfileId,
                PaneId = source.PaneId,
                Command = source.Command,
                Arguments = source.Arguments,
                Sizes = source.Sizes.ToList(),
                Children = source.Children.Select(ClonePaneNode).Where(c => c != null).Select(c => c!).ToList()
            };
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
                case Key.Tab: sequence = isShift ? "\x1b[Z" : "\t"; return true;
                case Key.Escape: sequence = "\x1b"; return true;
            }

            sequence = TerminalInputModeEncoder.EncodeSpecialKey(e.Key, buffer?.Modes);
            if (sequence != null)
            {
                return true;
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

        // Designer + legacy-test forwarder. Production callers must use the
        // typed ctor via App.OnFrameworkInitializationCompleted.
        public MainWindow() : this(AppServices.BuildForDesigner())
        {
        }

        public MainWindow(AppServiceBundle services)
        {
            ArgumentNullException.ThrowIfNull(services);
            _startup = services.Startup;
            InitializeComponent();
            _startup.Checkpoint("MainWindow.AfterInitializeComponent");
            _settings = TerminalSettings.Load();
            _startup.Checkpoint("MainWindow.AfterSettingsLoad");
            _commandPaletteUsageStore = new CommandPaletteUsageStore(AppPaths.CommandPaletteUsageFilePath);
            _commandPaletteUsage = new Dictionary<string, CommandPaletteUsageEntry>(_commandPaletteUsageStore.Load(), StringComparer.OrdinalIgnoreCase);
            _sshConnectionService = new SshConnectionService();
            _sshInteractionService = new SshInteractionService(() => this, ApplyThemeToDialogWindow);
            _sshLegacyMigrationService = new SshLegacyProfileMigrationService();

            if (_sshLegacyMigrationService.MigrateLegacyProfiles(_settings))
            {
                _settings.Save();
            }
            _startup.Checkpoint("MainWindow.AfterLegacyMigration");

            // Agent-host observe endpoint (docs/agent-host/DIRECTION.md, A1):
            // strictly no-op unless the user opted in via Settings.
            AgentHost.AgentHostService.Instance.Apply(_settings.AgentAccessObserveEnabled);

            // Ensure visual tree is ready for initial tab border
            this.Loaded += (s, e) =>
            {
                // Give layout one more tick to settle
                Dispatcher.UIThread.Post(() =>
                {
                    _startup.Checkpoint("MainWindow.LoadedPostStart");
                    UpdateTabVisuals();
                    UpdateTabHeaderViewport();
                    EnsureSelectedTabHeaderVisible();
                    FocusCurrentTerminal(defer: true);
                    SyncRecordingButtonState();
                    PopulateNewTabMenu();
                    InitializeCommandPaletteUI();
                    InitializeTransferCenterUI();
                    _startup.Checkpoint("MainWindow.LoadedPostUiReady");
                    _startup.Mark(StartupPhase.DeferredWorkComplete);
                    Dispatcher.UIThread.Post(EnsureWindowIconLoaded, DispatcherPriority.Background);
                }, DispatcherPriority.Input);
            };
            this.Activated += (s, e) => FocusCurrentTerminal(defer: true);
            this.SizeChanged += (_, __) => Dispatcher.UIThread.Post(UpdateTabHeaderViewport, DispatcherPriority.Background);
            _recordingToastTimer.Tick += (_, __) =>
            {
                _recordingToastTimer.Stop();
                HideRecordingToast();
            };

            var tabs = this.FindControl<TabControl>("Tabs");
            var btnNew = this.FindControl<Button>("BtnNewTab");
            var btnTabList = this.FindControl<Button>("BtnTabList");
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

                // XAML sets Margin="0,4,140,0" to reserve space for Windows-style caption buttons on the right.
                // On macOS the system traffic lights are on the left, so collapse the right reservation
                // so the custom buttons (+, tab list, record, …, settings) sit flush against the edge.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var m = titleBar.Margin;
                    titleBar.Margin = new Thickness(m.Left, m.Top, 8, m.Bottom);
                }
            }


            var btnConnections = this.FindControl<Button>("BtnConnections");
            var btnCloseConn = this.FindControl<Button>("BtnCloseConnections");

            if (btnConnections != null) btnConnections.Click += (s, e) => ToggleConnections();
            if (btnCloseConn != null) btnCloseConn.Click += (s, e) => ToggleConnections();

            if (tabs != null)
            {
                tabs.SelectionChanged += (s, e) =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
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
                    sw.Stop();
                    RendererStatistics.RecordTabSwitchTime(sw.ElapsedMilliseconds);
                };
            }
            _startup.Checkpoint("MainWindow.AfterCoreUiWireup");

            ApplyThemeToUI();
            _startup.Checkpoint("MainWindow.AfterApplyTheme");

            var menuManage = this.FindControl<MenuItem>("MenuManageProfiles");
            if (menuManage != null) menuManage.Click += async (s, e) =>
            {
                await OpenSettings(1);
            };

            var menuNewSsh = this.FindControl<MenuItem>("MenuNewSshConnection");
            if (menuNewSsh != null) menuNewSsh.Click += async (s, e) =>
            {
                await ShowNewSshConnectionDialogAsync(null);
            };

            var btnRecord = this.FindControl<Button>("BtnRecord");
            if (btnRecord != null)
            {
                btnRecord.Click += (s, e) => _currentPane?.ToggleRecording();
            }

            var recordingToastClose = this.FindControl<Button>("RecordingToastClose");
            if (recordingToastClose != null)
            {
                recordingToastClose.Click += (_, __) => HideRecordingToast();
            }

            var recordingToastOpenFolder = this.FindControl<Button>("RecordingToastOpenFolder");
            if (recordingToastOpenFolder != null)
            {
                recordingToastOpenFolder.Click += (_, __) => OpenRecordingToastFolder();
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
                if (!TryRestoreStartupSession(tabs))
                {
                    AddTab(defaultProfile);
                    _startup.CompleteWithoutRestore();
                }
            }
            else
            {
                AddTab(defaultProfile);
                _startup.CompleteWithoutRestore();
            }

            if (_startup.HasPendingDeferredRestore)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var tabsControl = this.FindControl<TabControl>("Tabs");
                    if (tabsControl == null)
                    {
                        return;
                    }
                    _startup.DrainDeferred(deferredTab => HydrateDeferredStartupTab(tabsControl, deferredTab));
                }, DispatcherPriority.Background);
            }
            _startup.Checkpoint("MainWindow.AfterInitialTabs");

            // SetupCommandPalette() is lazy (runs on palette open / settings save), so
            // prime the toolbar shortcut tooltips here for the initial window state.
            UpdateShortcutTooltips();

            // Keyboard Shortcuts
            this.AddHandler(KeyDownEvent, (s, e) =>
            {
                var modifiers = e.KeyModifiers;
                bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
                bool isShift = (modifiers & KeyModifiers.Shift) != 0;

                if (IsShortcut(e, "command_palette", "Ctrl+Shift+P"))
                {
                    if (_currentPane?.TryToggleCommandAssistPinShortcut() == true)
                    {
                        e.Handled = true;
                        return;
                    }

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

                if (IsShortcut(e, "command_assist_toggle", "Ctrl+Space"))
                {
                    RecordCommandUsage("command_assist_toggle");
                    _currentPane?.ToggleCommandAssist();
                    e.Handled = true;
                    return;
                }

                if (IsShortcut(e, "command_assist_help", "Ctrl+Shift+H"))
                {
                    if (TryOpenCommandAssistHelp(_currentPane))
                    {
                        RecordCommandUsage("command_assist_help");
                        e.Handled = true;
                        return;
                    }
                }

                if (IsShortcut(e, "command_assist_history", "Ctrl+R"))
                {
                    if (_currentPane?.OpenCommandAssistHistorySearch() == true)
                    {
                        RecordCommandUsage("command_assist_history");
                        e.Handled = true;
                        return;
                    }
                }

                if (IsShortcut(e, "settings", "Ctrl+,"))
                {
                    RecordCommandUsage("settings");
                    _ = OpenSettings(0);
                    e.Handled = true;
                    return;
                }

                if (IsShortcut(e, "connections", "Ctrl+Shift+K"))
                {
                    RecordCommandUsage("connections");
                    ToggleConnections();
                    e.Handled = true;
                    return;
                }

                if (IsShortcut(e, "toggle_recording", "Ctrl+Shift+R"))
                {
                    RecordCommandUsage("toggle_recording");
                    _currentPane?.ToggleRecording();
                    e.Handled = true;
                    return;
                }

                if (IsShortcut(e, "font_increase", "Ctrl+OemPlus") || IsShortcut(e, "font_increase_alt", "Ctrl+Add"))
                {
                    RecordCommandUsage("font_increase");
                    _settings.FontSize++;
                    ApplySettingsToAllTabs();
                    _settings.Save();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "font_decrease", "Ctrl+OemMinus") || IsShortcut(e, "font_decrease_alt", "Ctrl+Subtract"))
                {
                    RecordCommandUsage("font_decrease");
                    _settings.FontSize = Math.Max(6, _settings.FontSize - 1);
                    ApplySettingsToAllTabs();
                    _settings.Save();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "new_tab", "Ctrl+Shift+T"))
                {
                    RecordCommandUsage("new_tab");
                    AddTab();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "close_tab", "Ctrl+W"))
                {
                    RecordCommandUsage("close_tab");
                    _ = CloseSelectedTabAsync();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "close_pane", "Ctrl+Shift+W"))
                {
                    RecordCommandUsage("close_pane");
                    CloseActivePane();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "find", "Ctrl+F") || IsShortcut(e, "find_alt", "Ctrl+Shift+F"))
                {
                    RecordCommandUsage("find");
                    _currentPane?.ToggleSearch();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "split_vertical", "Ctrl+Shift+D"))
                {
                    RecordCommandUsage("split_vertical");
                    // "Split Vertical" means a vertical divider (side-by-side panes).
                    SplitPane(Avalonia.Layout.Orientation.Horizontal);
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "split_horizontal", "Ctrl+Shift+E"))
                {
                    RecordCommandUsage("split_horizontal");
                    // "Split Horizontal" means a horizontal divider (stacked panes).
                    SplitPane(Avalonia.Layout.Orientation.Vertical);
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "equalize_panes", "Ctrl+Shift+G"))
                {
                    RecordCommandUsage("equalize_panes");
                    EqualizeCurrentSplit();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "toggle_pane_zoom", "Ctrl+Shift+Z"))
                {
                    RecordCommandUsage("toggle_pane_zoom");
                    TogglePaneZoomForCurrentTab();
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "toggle_broadcast_input", "Ctrl+Shift+B"))
                {
                    RecordCommandUsage("toggle_broadcast_input");
                    ToggleBroadcastForCurrentTab();
                    e.Handled = true;
                    return;
                }
                bool nextTabShortcut = IsShortcut(e, "next_tab", "Ctrl+Tab");
                bool prevTabShortcut = IsShortcut(e, "prev_tab", "Ctrl+Shift+Tab");
                if (nextTabShortcut || prevTabShortcut)
                {
                    RecordCommandUsage(prevTabShortcut ? "prev_tab" : "next_tab");
                    bool switched = SwitchTabByMru(reverse: prevTabShortcut);
                    if (switched)
                    {
                        e.Handled = true;
                        return;
                    }
                }
                if (IsShortcut(e, "open_tab_list", "Ctrl+Shift+O"))
                {
                    RecordCommandUsage("open_tab_list");
                    PopulateTabListMenu(showFlyout: true);
                    e.Handled = true;
                    return;
                }
                if (IsShortcut(e, "paste", "Ctrl+V"))
                {
                    RecordCommandUsage("paste");
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
            _startup.Checkpoint("MainWindow.CtorComplete");
        }

        private void InitializeRestoredTabs(TabControl tabs)
        {
            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                GetTabId(item);
                GetOrCreateTabState(item);
                ConfigureTabHeader(item, GetTabHeaderText(item));
                if (item.Content is Control c)
                {
                    WireControlTree(c);
                    RegisterPaneOwners(item, c);
                }
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
                var importedProfiles = NovaTerminal.Shell.ProfileImporter.ImportSshConfig();
                int changed = _sshConnectionService.MergeImportedProfiles(importedProfiles);
                if (changed > 0)
                {
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
                    TerminalProfile? updatedProfile = pane.Profile.Type == ConnectionType.SSH
                        ? _sshConnectionService.GetConnectionProfile(pane.Profile.Id)
                        : settings.Profiles.Find(p => p.Id == pane.Profile.Id);
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
            pane.SshInteractionHandler = _sshInteractionService;
            pane.RequestRemoteFilesSidebarTransfer -= OnPaneRequestRemoteFilesSidebarTransfer;
            pane.WorkingDirectoryChanged -= OnPaneWorkingDirectoryChanged;
            pane.TitleChanged -= OnPaneTitleChanged;
            pane.PaneActionRequested -= OnPaneActionRequested;
            pane.OutputReceived -= OnPaneOutputReceived;
            pane.BellReceived -= OnPaneBellReceived;
            pane.CommandStarted -= OnPaneCommandStarted;
            pane.CommandFinished -= OnPaneCommandFinished;
            pane.ProcessExited -= OnPaneProcessExited;
            pane.LongCommandCompleted -= OnPaneLongCommandCompleted;

            pane.RequestRemoteFilesSidebarTransfer += OnPaneRequestRemoteFilesSidebarTransfer;
            pane.WorkingDirectoryChanged += OnPaneWorkingDirectoryChanged;
            pane.TitleChanged += OnPaneTitleChanged;
            pane.PaneActionRequested += OnPaneActionRequested;
            pane.OutputReceived += OnPaneOutputReceived;
            pane.BellReceived += OnPaneBellReceived;
            pane.CommandStarted += OnPaneCommandStarted;
            pane.CommandFinished += OnPaneCommandFinished;
            pane.ProcessExited += OnPaneProcessExited;
            pane.LongCommandCompleted += OnPaneLongCommandCompleted;
        }

        private void UnwirePane(TerminalPane pane)
        {
            _paneOwnerTab.Remove(pane);
            pane.RequestRemoteFilesSidebarTransfer -= OnPaneRequestRemoteFilesSidebarTransfer;
            pane.WorkingDirectoryChanged -= OnPaneWorkingDirectoryChanged;
            pane.TitleChanged -= OnPaneTitleChanged;
            pane.PaneActionRequested -= OnPaneActionRequested;
            pane.OutputReceived -= OnPaneOutputReceived;
            pane.BellReceived -= OnPaneBellReceived;
            pane.CommandStarted -= OnPaneCommandStarted;
            pane.CommandFinished -= OnPaneCommandFinished;
            pane.ProcessExited -= OnPaneProcessExited;
            pane.LongCommandCompleted -= OnPaneLongCommandCompleted;
        }

        private void OnPaneRequestRemoteFilesSidebarTransfer(TerminalPane srcPane, SidebarTransferRequest request)
        {
            _ = InitiateSidebarSftpTransfer(srcPane, request.Direction, request.Kind, request.RemotePath);
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
            var tab = ResolveOwningTabForPane(pane);
            if (tab == null) return;

            if (TryGetSelectedTab(out var selectedTabForStartup) && selectedTabForStartup == tab && ResolvePaneForTab(selectedTabForStartup) == pane)
            {
                _startup.Mark(StartupPhase.FirstTerminalReady);
            }

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

        private void OnPaneLongCommandCompleted(TerminalPane pane, string? commandText, int? exitCode, TimeSpan duration)
        {
            // Policy: opt-in, and only when the user isn't already looking at
            // this pane (a different pane is current, or the window is in the
            // background). The pane already applied the duration threshold.
            if (!LongCommandNotificationPolicy.ShouldNotify(
                    _settings.LongCommandNotificationsEnabled,
                    windowActive: IsActive,
                    isCurrentPane: ReferenceEquals(pane, _currentPane)))
            {
                return;
            }

            ShowRecordingToast(
                "Command finished",
                LongCommandNotificationPolicy.BuildMessage(commandText, exitCode, duration, pane.GetBaseTabTitle()),
                filePath: null,
                folderPath: null,
                autoHide: true);
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
            splitter.PointerEntered -= OnSplitterPointerEntered;
            splitter.PointerExited -= OnSplitterPointerExited;
            splitter.PointerPressed -= OnSplitterPointerPressed;
            splitter.PointerReleased -= OnSplitterPointerReleased;
            splitter.PointerCaptureLost -= OnSplitterPointerCaptureLost;
            splitter.DoubleTapped -= OnSplitterDoubleTapped;
            splitter.PointerEntered += OnSplitterPointerEntered;
            splitter.PointerExited += OnSplitterPointerExited;
            splitter.PointerPressed += OnSplitterPointerPressed;
            splitter.PointerReleased += OnSplitterPointerReleased;
            splitter.PointerCaptureLost += OnSplitterPointerCaptureLost;
            splitter.DoubleTapped += OnSplitterDoubleTapped;
            ApplySplitterVisualState(splitter);
        }

        private void OnSplitterPointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is GridSplitter splitter)
            {
                SetSplitterStateClass(splitter, SplitterHoverClass, true);
                ApplySplitterVisualState(splitter);
            }
        }

        private void OnSplitterPointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is GridSplitter splitter && !splitter.Classes.Contains(SplitterDraggingClass))
            {
                SetSplitterStateClass(splitter, SplitterHoverClass, false);
                ApplySplitterVisualState(splitter);
            }
        }

        private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is GridSplitter splitter && e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed)
            {
                SetSplitterStateClass(splitter, SplitterHoverClass, true);
                SetSplitterStateClass(splitter, SplitterDraggingClass, true);
                ApplySplitterVisualState(splitter);
            }
        }

        private void OnSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is GridSplitter splitter)
            {
                SetSplitterStateClass(splitter, SplitterDraggingClass, false);
                SetSplitterStateClass(splitter, SplitterHoverClass, splitter.IsPointerOver);
                ApplySplitterVisualState(splitter);
            }
        }

        private void OnSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (sender is GridSplitter splitter)
            {
                SetSplitterStateClass(splitter, SplitterDraggingClass, false);
                SetSplitterStateClass(splitter, SplitterHoverClass, splitter.IsPointerOver);
                ApplySplitterVisualState(splitter);
            }
        }

        private void ApplySplitterVisualState(GridSplitter splitter)
        {
            var contrast = _settings.ActiveTheme.GetContrastForeground().ToAvaloniaColor();
            var alpha = splitter.Classes.Contains(SplitterDraggingClass)
                ? (byte)0x66
                : splitter.Classes.Contains(SplitterHoverClass)
                    ? (byte)0x44
                    : (byte)0x24;

            splitter.Background = new SolidColorBrush(Color.FromArgb(alpha, contrast.R, contrast.G, contrast.B));
        }

        private static void SetSplitterStateClass(GridSplitter splitter, string className, bool isActive)
        {
            if (isActive)
            {
                splitter.Classes.Add(className);
                return;
            }

            splitter.Classes.Remove(className);
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null)
            {
                sw.Stop();
                RendererStatistics.RecordTabAutomationUpdateTime(sw.ElapsedMilliseconds);
                return;
            }

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
            sw.Stop();
            RendererStatistics.RecordTabAutomationUpdateTime(sw.ElapsedMilliseconds);
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
                _currentPane.RecordingNotification -= OnRecordingNotification;
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
            if (ShouldAutoAcceptRunningPaneClose(
                pane.IsProcessRunning,
                pane.HasActiveChildProcesses,
                pane.HasUserInteraction,
                pane.Profile?.Type,
                pane.Profile?.Command,
                pane.ShellArgs,
                _settings.PaneClosePolicy))
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

        internal static bool ShouldAutoAcceptRunningPaneClose(
            bool isProcessRunning,
            bool hasActiveChildProcesses,
            bool hasUserInteraction,
            ConnectionType? profileType,
            string? shellCommand,
            string? shellArgs,
            string? paneClosePolicy)
        {
            if (!isProcessRunning) return true;

            if (string.Equals(paneClosePolicy?.Trim(), "force", StringComparison.OrdinalIgnoreCase))
                return true;

            bool isWsl = shellCommand?.Contains("wsl", StringComparison.OrdinalIgnoreCase) == true;
            bool isSsh = profileType == ConnectionType.SSH;

            bool isSafeArgs = string.IsNullOrWhiteSpace(shellArgs);
            if (!isSafeArgs && isWsl)
            {
                // wsl profiles often have "-d Ubuntu" or similar as default args.
                // We treat these as safe to close without prompting.
                isSafeArgs = true;
            }

            if (isSsh)
            {
                // SSH connections are always considered precious and should warn on close
                // unless forced or process is dead.
                return false;
            }
            else if (isWsl)
            {
                // WSL is an opaque shell. We cannot see its Linux child processes.
                if (!hasUserInteraction && isSafeArgs)
                {
                    return true;
                }
            }
            else
            {
                // For native/transparent shells, we can trust explicit tracking.
                // If there are no active child processes, it's safe to close silently,
                // even if the user has been interacting with it.
                if (!hasActiveChildProcesses && isSafeArgs)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<bool> ShowRunningProcessCloseConfirmationAsync(string message)
        {
            bool confirmed = false;

            var dialog = CreateThemedDialogWindow("Close Running Pane", 460, 190, canResize: false);

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

        // Collects the ad-hoc shell commands a bundle would actually spawn on restore.
        // Mirrors SessionManager.RestorePaneTree: a leaf runs its raw Command/Arguments
        // ONLY when its profile doesn't resolve, so panes whose profile resolves are
        // skipped — that both matches what runs and avoids prompting for locally-saved
        // workspaces that store a ShellCommand alongside a resolvable ProfileId (#171).
        internal static List<string> CollectBundleCommands(NovaSession? session, TerminalSettings settings)
        {
            var commands = new List<string>();
            if (session?.Tabs == null) return commands;

            void Walk(PaneNode? node)
            {
                if (node == null) return;
                if (node.Type == NodeType.Leaf)
                {
                    // Skip panes that resolve a known local/SSH profile — RestorePaneTree
                    // uses the profile and ignores Command/Arguments for those.
                    if (SessionManager.TryResolvePaneProfile(node, settings) != null)
                    {
                        return;
                    }

                    // Otherwise the fallback runs `cmd.exe`/Command with Arguments, so an
                    // argument-only leaf still runs `cmd.exe <args>` and can smuggle cmd
                    // metacharacters (#171 review).
                    bool hasCommand = !string.IsNullOrWhiteSpace(node.Command);
                    bool hasArgs = !string.IsNullOrWhiteSpace(node.Arguments);
                    if (hasCommand || hasArgs)
                    {
                        string effectiveCommand = hasCommand ? node.Command! : "cmd.exe";
                        string args = hasArgs ? " " + node.Arguments : "";
                        commands.Add((effectiveCommand + args).Trim());
                    }
                }
                else if (node.Children != null)
                {
                    foreach (var child in node.Children) Walk(child);
                }
            }

            foreach (var tab in session.Tabs)
            {
                if (tab != null) Walk(tab.Root);
            }
            return commands;
        }

        // Confirms the commands a foreign bundle will run before it spawns anything.
        // Returns true when there is nothing to run or the user approves.
        private async Task<bool> ConfirmBundleCommandsAsync(NovaSession? session, string bundleName)
        {
            var commands = CollectBundleCommands(session, _settings);
            if (commands.Count == 0) return true; // profile-only bundles run known targets

            bool confirmed = false;
            var dialog = CreateThemedDialogWindow("Run Workspace Commands?", 520, 320, canResize: false);

            var listText = string.Join("\n", commands.ConvertAll(c => "•  " + c));

            var cancelButton = new Button { Content = "Cancel", Width = 92 };
            cancelButton.Click += (_, __) => { confirmed = false; dialog.Close(); };

            var runButton = new Button { Content = "Run Commands", Width = 130 };
            runButton.Click += (_, __) => { confirmed = true; dialog.Close(); };

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
                            Text = $"The workspace \"{bundleName}\" will run these commands:",
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new ScrollViewer
                        {
                            MaxHeight = 180,
                            Content = new TextBlock
                            {
                                Text = listText,
                                FontFamily = new Avalonia.Media.FontFamily("Consolas, monospace"),
                                TextWrapping = TextWrapping.Wrap
                            }
                        },
                        new TextBlock
                        {
                            Text = "Only run commands from a workspace you trust.",
                            Opacity = 0.8,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, runButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return confirmed;
        }

        private void DisposeControlTree(Control control)
        {
            // All call sites are UI event paths, but marshal defensively: the UI-affine
            // detach below throws VerifyAccess off the UI thread.
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => DisposeControlTree(control));
                return;
            }

            if (control is TerminalPane pane)
            {
                UnwirePane(pane);

                // Two-phase teardown (#154): UI-affine detach runs here on the UI thread;
                // only the potentially blocking session teardown moves to the pool.
                // Previously the whole pane.Dispose() ran in Task.Run with a swallowed
                // catch, so a VerifyAccess throw aborted teardown before the session was
                // disposed, leaking the PTY and its child shell.
                var session = pane.DetachFromUiThread();
                if (session != null)
                {
                    Task.Run(() =>
                    {
                        try { session.Dispose(); }
                        catch (Exception ex)
                        {
                            // Debug.WriteLine is compiled out of Release builds; use the
                            // logger so production dispose failures leave a trace.
                            TerminalLogger.Log($"[MainWindow] Session dispose failed: {ex.Message}");
                        }
                    });
                }
            }
            else if (control is Panel panel) { foreach (var child in panel.Children) if (child is Control c) DisposeControlTree(c); }
            else if (control is ContentPresenter cp && cp.Content is Control childContent) DisposeControlTree(childContent);
        }

        private void HandleSshQuickOpen(TerminalProfile profile, SshQuickOpenTarget target, SshDiagnosticsLevel diagnosticsLevel)
        {
            switch (target)
            {
                case SshQuickOpenTarget.CurrentPane:
                    OpenProfileInCurrentPane(profile, diagnosticsLevel);
                    return;
                case SshQuickOpenTarget.NewTab:
                    AddTab(profile, diagnosticsLevel);
                    return;
                case SshQuickOpenTarget.SplitHorizontal:
                    OpenProfileInSplitPane(profile, Avalonia.Layout.Orientation.Vertical, diagnosticsLevel);
                    return;
                case SshQuickOpenTarget.SplitVertical:
                    OpenProfileInSplitPane(profile, Avalonia.Layout.Orientation.Horizontal, diagnosticsLevel);
                    return;
                default:
                    AddTab(profile, diagnosticsLevel);
                    return;
            }
        }

        private void OpenProfileInSplitPane(TerminalProfile profile, Avalonia.Layout.Orientation splitOrientation, SshDiagnosticsLevel diagnosticsLevel)
        {
            if (_currentPane == null)
            {
                AddTab(profile, diagnosticsLevel);
                return;
            }

            SplitPane(splitOrientation);
            OpenProfileInCurrentPane(profile, diagnosticsLevel);
        }

        private void OpenProfileInCurrentPane(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel)
        {
            if (_currentPane == null)
            {
                AddTab(profile, diagnosticsLevel);
                return;
            }

            TerminalProfile resolvedProfile = profile.Type == ConnectionType.SSH
                ? _sshConnectionService.GetConnectionProfile(profile.Id) ?? profile
                : _settings.Profiles.Find(p => p.Id == profile.Id) ?? profile;
            var paneToReplace = _currentPane;
            var replacementPane = new TerminalPane(resolvedProfile, diagnosticsLevel);
            replacementPane.ApplySettings(_settings);
            WirePane(replacementPane);

            if (!ReplacePaneInVisualTree(paneToReplace, replacementPane))
            {
                DisposeControlTree(replacementPane);
                AddTab(resolvedProfile, diagnosticsLevel);
                return;
            }

            _currentPane = replacementPane;
            if (TryGetSelectedTab(out var selectedTab))
            {
                _activePaneByTab[selectedTab] = replacementPane;
                AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(replacementPane.PaneId, GetPersistentTabId(selectedTab));
                if (FindTabHeaderTextBlock(selectedTab.Header) is TextBlock tabHeader)
                {
                    tabHeader.Text = resolvedProfile.Name;
                }
            }

            DisposeControlTree(paneToReplace);
            Dispatcher.UIThread.Post(() => replacementPane.ActiveControl.Focus(), DispatcherPriority.Loaded);
            UpdateTabVisuals();
            UpdatePaneAutomationLabels();
            UpdateBroadcastIndicator();
            if (TryGetSelectedTab(out selectedTab))
            {
                RefreshLayoutModelForTab(selectedTab);
            }
        }

        private static bool ReplacePaneInVisualTree(TerminalPane sourcePane, TerminalPane replacementPane)
        {
            switch (sourcePane.Parent)
            {
                case Grid grid:
                    CopyGridPlacement(sourcePane, replacementPane);
                    int gridIndex = grid.Children.IndexOf(sourcePane);
                    if (gridIndex < 0)
                    {
                        return false;
                    }

                    grid.Children.RemoveAt(gridIndex);
                    grid.Children.Insert(gridIndex, replacementPane);
                    return true;

                case ContentPresenter presenter:
                    presenter.Content = replacementPane;
                    return true;

                case TabItem tabItem:
                    tabItem.Content = replacementPane;
                    return true;

                case Panel panel:
                    CopyGridPlacement(sourcePane, replacementPane);
                    int panelIndex = panel.Children.IndexOf(sourcePane);
                    if (panelIndex < 0)
                    {
                        return false;
                    }

                    panel.Children.RemoveAt(panelIndex);
                    panel.Children.Insert(panelIndex, replacementPane);
                    return true;

                default:
                    return false;
            }
        }

        void AddTab(TerminalProfile? profile = null, SshDiagnosticsLevel sshDiagnostics = SshDiagnosticsLevel.None)
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
                if (profile.Type == ConnectionType.SSH)
                {
                    // SSH profiles are store-backed and independent from TerminalSettings.Profiles.
                    TerminalProfile? sshProfile = _sshConnectionService.GetConnectionProfile(profile.Id);
                    if (sshProfile != null)
                    {
                        profile = sshProfile;
                    }
                }
                else
                {
                    // Refresh local profile from settings to pick up latest overrides.
                    var freshProfile = _settings.Profiles.Find(p => p.Id == profile.Id);
                    if (freshProfile != null) profile = freshProfile;
                }
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
                profile.Arguments = string.Empty;
            }

            if (TryApplyTemplateRuleForProfile(profile))
            {
                return;
            }

            var pane = new TerminalPane(profile, sshDiagnostics);
            WirePane(pane);

            pane.ApplySettings(_settings);
            var tabItem = new TabItem { Content = pane };
            ConfigureTabHeader(tabItem, profile.Name);
            tabs.Items.Add(tabItem);
            tabs.SelectedItem = tabItem;
            GetTabId(tabItem);
            GetOrCreateTabState(tabItem);
            TouchTabMru(tabItem);
            _currentPane = pane;
            _activePaneByTab[tabItem] = pane;
            _paneOwnerTab[pane] = tabItem;
            AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(pane.PaneId, GetPersistentTabId(tabItem));

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
            foreach (var profile in _settings.Profiles.Where(p => p.Type == ConnectionType.Local))
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

        private void EnsureWindowIconLoaded()
        {
            if (_windowIconLoaded)
            {
                return;
            }

            using var iconStream = AssetLoader.Open(new Uri("avares://NovaTerminal/Assets/nova_icon.ico"));
            Icon = new WindowIcon(iconStream);
            _windowIconLoaded = true;
        }

        internal void UpdateTabVisuals(TabItem? specificTab = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = specificTab;
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null)
            {
                sw.Stop();
                RendererStatistics.RecordTabVisualUpdateTime(sw.ElapsedMilliseconds);
                return;
            }

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

                if (FindTabHeaderTextBlock(ti.Header) is TextBlock tb)
                {
                    tb.Foreground = contrastForeground;
                    tb.Text = labels[ti];
                }

                if (ti.Header is Control headerControl)
                {
                    ToolTip.SetTip(headerControl, BuildFullTabLabel(ti));
                }
            }

            UpdateTabAutomationLabels();
            PopulateTabListMenu();
            UpdateTabHeaderViewport();
            sw.Stop();
            RendererStatistics.RecordTabVisualUpdateTime(sw.ElapsedMilliseconds);
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
                newPane = new TerminalPane(profile, SshDiagnosticsLevel.None);
            }
            else
            {
                newPane = new TerminalPane(originalPane.ShellCommand);
            }

            newPane.ApplySettings(_settings);
            WirePane(newPane);
            if (TryGetSelectedTab(out var splitOwnerTab))
            {
                AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(newPane.PaneId, GetPersistentTabId(splitOwnerTab));
            }
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
                if (topLevel?.Clipboard == null || _currentPane?.Session == null)
                {
                    return;
                }

                var clipboard = topLevel.Clipboard;
                var text = await clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    // Normalize line endings to avoid double newlines on paste
                    _currentPane.NotifyCommandAssistPaste(text);
                    text = NovaTerminal.Platform.Input.TerminalInputSender.PreparePaste(
                        text,
                        _currentPane.Buffer?.Modes.IsBracketedPasteMode == true);

                    _currentPane.Session.SendInput(text);
                    return;
                }

                // No text on the clipboard. If it holds an image (e.g. a screenshot), save it
                // to a temp PNG and send the path so a running CLI such as Claude Code can read
                // the image. This mirrors NovaTerminal's existing file-drop behavior.
                var bitmap = await clipboard.TryGetBitmapAsync();
                if (bitmap != null)
                {
                    try
                    {
                        string path = NovaTerminal.Platform.Input.ClipboardImage.GetTempImagePath(".png");
                        bitmap.Save(path);

                        // In a WSL session the Linux CLI can't resolve a C:\ path, so map it to
                        // its /mnt/<drive> form — mirroring the file-drop path handling.
                        bool isWsl = _currentPane.Session.ShellCommand?.Contains("wsl", StringComparison.OrdinalIgnoreCase) ?? false;
                        string sendPath = isWsl
                            ? NovaTerminal.Platform.Input.ClipboardImage.ToWslMountPath(path)
                            : path;
                        _currentPane.Session.SendInput(NovaTerminal.Platform.Input.ClipboardImage.QuotePathForInput(sendPath));
                    }
                    finally
                    {
                        bitmap.Dispose();
                    }
                }
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
            var btnTabList = this.FindControl<Button>("BtnTabList");
            var iconTabList = this.FindControl<PathIcon>("IconTabList");
            var btnRecord = this.FindControl<Button>("BtnRecord");
            var btnConns = this.FindControl<Button>("BtnConnections");
            var commandSearchBox = this.FindControl<TextBox>("CommandSearchBox");

            if (btnNew != null) btnNew.Foreground = contrastForeground;
            if (btnTabList != null) btnTabList.Foreground = contrastForeground;
            if (iconTabList != null) iconTabList.Foreground = contrastForeground;
            if (btnConns != null) btnConns.Foreground = contrastForeground;
            if (btnRecord != null) btnRecord.Foreground = contrastForeground;
            if (commandSearchBox != null) commandSearchBox.Foreground = contrastForeground;
            foreach (var splitter in this.GetVisualDescendants().OfType<GridSplitter>())
            {
                ApplySplitterVisualState(splitter);
            }

            SyncRecordingButtonState();

            // Force update of tab borders (blue line) since theme color changed
            UpdateTabVisuals();

            var titleBar = this.FindControl<Grid>("TitleBar");
            if (titleBar != null) titleBar.Background = Brushes.Transparent;

            var dragBorder = this.FindControl<Border>("DragBorder");
            if (dragBorder != null)
            {
                dragBorder.Background = headerBrush;
            }

            _connectionManagerControl?.ApplyTheme(theme);

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
            CommandRegistry.Register("New Tab", "General", () => AddTab(), GetEffectiveShortcutBinding("new_tab", "Ctrl+Shift+T"), "new_tab");

            // Dynamic Profile Tabs
            if (_settings.Profiles != null)
            {
                foreach (var profile in _settings.Profiles.Where(p => p.Type == ConnectionType.Local))
                {
                    bool exists = File.Exists(profile.Command) || ShellHelper.InPath(profile.Command);
                    if (!exists) continue;
                    CommandRegistry.Register($"New Tab: {profile.Name}", "Shell", () => AddTab(profile), "");
                }
            }

            if (_sshConnectionService != null)
            {
                foreach (var profile in _sshConnectionService.GetConnectionProfiles())
                {
                    var capturedProfile = profile;
                    CommandRegistry.Register($"New Connection (SSH): {profile.Name}", "SSH", () => AddTab(capturedProfile), "");
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
            CommandRegistry.Register("Workspace Template: Save Current", "Workspace", () => _ = SaveWorkspaceTemplateInteractiveAsync(), "");
            CommandRegistry.Register("Workspace Template: Apply...", "Workspace", () => _ = LoadWorkspaceTemplateInteractiveAsync(), "");
            CommandRegistry.Register("Tab Rule: Set Template for Current Profile...", "Workspace", () => _ = SetTemplateRuleForCurrentPaneProfileAsync(), "");
            CommandRegistry.Register("Tab Rule: Clear Template for Current Profile", "Workspace", () => ClearTemplateRuleForCurrentPaneProfile(), "");
            var workspacePolicy = WorkspacePolicyManager.Current;
            if (workspacePolicy.AllowWorkspaceBundleExport)
            {
                CommandRegistry.Register("Workspace: Export Bundle...", "Workspace", () => _ = ExportWorkspaceBundleInteractiveAsync(), "");
                CommandRegistry.Register("Workspace: Export Current Session Bundle...", "Workspace", () => _ = ExportCurrentSessionBundleInteractiveAsync(), "");
            }
            if (workspacePolicy.AllowWorkspaceBundleImport)
            {
                CommandRegistry.Register("Workspace: Import Bundle...", "Workspace", () => _ = ImportWorkspaceBundleInteractiveAsync(), "");
                CommandRegistry.Register("Workspace: Open Bundle...", "Workspace", () => _ = OpenWorkspaceBundleInteractiveAsync(), "");
            }
            foreach (var workspaceName in WorkspaceManager.ListWorkspaceNames())
            {
                string capturedName = workspaceName;
                CommandRegistry.Register($"Workspace: Load {capturedName}", "Workspace", () => LoadWorkspaceByName(capturedName), "");
            }
            foreach (var templateName in WorkspaceManager.ListWorkspaceTemplateNames())
            {
                string capturedTemplate = templateName;
                CommandRegistry.Register($"Workspace Template: Apply {capturedTemplate}", "Workspace", () => ApplyWorkspaceTemplateByName(capturedTemplate), "");
            }

            CommandRegistry.Register("Close Tab", "General", () => CloseActiveTab(), GetEffectiveShortcutBinding("close_tab", "Ctrl+W"), "close_tab");
            CommandRegistry.Register("Close Pane", "General", () => CloseActivePane(), GetEffectiveShortcutBinding("close_pane", "Ctrl+Shift+W"), "close_pane");
            CommandRegistry.Register("Tab: Next (MRU)", "General", () => SwitchTabByMru(reverse: false), GetEffectiveShortcutBinding("next_tab", "Ctrl+Tab"), "next_tab");
            CommandRegistry.Register("Tab: Previous (MRU)", "General", () => SwitchTabByMru(reverse: true), GetEffectiveShortcutBinding("prev_tab", "Ctrl+Shift+Tab"), "prev_tab");
            CommandRegistry.Register("Tab: Open Tab List", "General", () => PopulateTabListMenu(showFlyout: true), GetEffectiveShortcutBinding("open_tab_list", "Ctrl+Shift+O"), "open_tab_list");
            CommandRegistry.Register("Tab: Rename Current", "General", () => _ = RenameSelectedTabAsync(), "");
            CommandRegistry.Register("Tab: Copy Current Title", "General", () => _ = CopySelectedTabTitleAsync(), "");
            CommandRegistry.Register("Tab: Close Others", "General", () => _ = CloseOtherTabsAsync(), "");
            CommandRegistry.Register("Tab: Toggle Pin", "General", () => TogglePinSelectedTab(), "");
            CommandRegistry.Register("Tab: Toggle Protect", "General", () => ToggleProtectSelectedTab(), "");
            // Keep command naming aligned with common terminal UX:
            // Vertical split => vertical divider => side-by-side panes.
            CommandRegistry.Register("Split Vertical", "View", () => SplitPane(Avalonia.Layout.Orientation.Horizontal), GetEffectiveShortcutBinding("split_vertical", "Ctrl+Shift+D"), "split_vertical");
            // Horizontal split => horizontal divider => stacked panes.
            CommandRegistry.Register("Split Horizontal", "View", () => SplitPane(Avalonia.Layout.Orientation.Vertical), GetEffectiveShortcutBinding("split_horizontal", "Ctrl+Shift+E"), "split_horizontal");
            CommandRegistry.Register("Equalize Panes", "View", () => EqualizeCurrentSplit(), GetEffectiveShortcutBinding("equalize_panes", "Ctrl+Shift+G"), "equalize_panes");
            CommandRegistry.Register("Pane: Toggle Zoom", "View", () => TogglePaneZoomForCurrentTab(), GetEffectiveShortcutBinding("toggle_pane_zoom", "Ctrl+Shift+Z"), "toggle_pane_zoom");
            CommandRegistry.Register("Pane: Toggle Broadcast Input (Tab)", "View", () => ToggleBroadcastForCurrentTab(), GetEffectiveShortcutBinding("toggle_broadcast_input", "Ctrl+Shift+B"), "toggle_broadcast_input");
            CommandRegistry.Register("Pane: Reconnect", "View", () => _currentPane?.Reconnect(), "");
            CommandRegistry.Register("Focus Pane Left", "View", () => NavigatePane(MoveDirection.Left), "Alt+Left");
            CommandRegistry.Register("Focus Pane Right", "View", () => NavigatePane(MoveDirection.Right), "Alt+Right");
            CommandRegistry.Register("Focus Pane Up", "View", () => NavigatePane(MoveDirection.Up), "Alt+Up");
            CommandRegistry.Register("Focus Pane Down", "View", () => NavigatePane(MoveDirection.Down), "Alt+Down");
            CommandRegistry.Register("Find in Terminal", "Edit", () => _currentPane?.ToggleSearch(), GetEffectiveShortcutBinding("find", "Ctrl+F"), "find");
            CommandRegistry.Register("Pane: Export Snapshot (Plain Text)", "View", () => _currentPane?.ExportSnapshotAsync("txt"), "");
            CommandRegistry.Register("Pane: Export Snapshot (ANSI)", "View", () => _currentPane?.ExportSnapshotAsync("ansi"), "");
            CommandRegistry.Register("Pane: Export Snapshot (PNG)", "View", () => _currentPane?.ExportSnapshotAsync("png"), "");
            CommandRegistry.Register("Pane: Toggle Render HUD", "View", () => _currentPane?.ToggleRenderHud(), "");
            CommandRegistry.Register("Paste", "Edit", () => _ = PasteFromClipboardAsync(), GetEffectiveShortcutBinding("paste", "Ctrl+V"), "paste");
            CommandRegistry.Register("Font: Increase", "View", () => { _settings.FontSize++; ApplySettingsToAllTabs(); _settings.Save(); }, GetEffectiveShortcutBinding("font_increase", "Ctrl++"), "font_increase");
            CommandRegistry.Register("Font: Decrease", "View", () => { _settings.FontSize = Math.Max(6, _settings.FontSize - 1); ApplySettingsToAllTabs(); _settings.Save(); }, GetEffectiveShortcutBinding("font_decrease", "Ctrl+-"), "font_decrease");
            CommandRegistry.Register("Settings", "General", async () =>
            {
                await OpenSettings(0);
            }, GetEffectiveShortcutBinding("settings", "Ctrl+,"), "settings");
            CommandRegistry.Register("Connections", "General", () => ToggleConnections(), GetEffectiveShortcutBinding("connections", "Ctrl+Shift+K"), "connections");
            CommandRegistry.Register("Toggle Recording", "General", () => _currentPane?.ToggleRecording(), GetEffectiveShortcutBinding("toggle_recording", "Ctrl+Shift+R"), "toggle_recording");
            CommandRegistry.Register("Open Recording...", "General", () => _ = ExecuteUiCommandAsync(ExecuteOpenRecordingCommandAsync, "Open Recording..."), "");
            CommandRegistry.Register("Open Recordings Folder", "General", () => OpenRecordingsFolder(), "");

            // SFTP Actions
            CommandRegistry.Register("SFTP: Toggle Remote Files", "Remote", () => _currentPane?.ToggleRemoteFilesSidebar(), "");
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
            CommandRegistry.Register("Debug: Box Drawing Test Screen", "Debug", () => ShowBoxDrawingTestScreen(), "");

            // Keep toolbar tooltips in sync with the (possibly rebound) shortcuts.
            UpdateShortcutTooltips();
        }

        private void UpdateShortcutTooltips()
        {
            var btnConnections = this.FindControl<Button>("BtnConnections");
            if (btnConnections != null)
            {
                ToolTip.SetTip(btnConnections, $"Connections ({GetEffectiveShortcutBinding("connections", "Ctrl+Shift+K")})");
            }

            // The record button's tooltip embeds the recording shortcut and is
            // produced by UpdateRecordButtonUi; re-sync it to pick up rebindings.
            SyncRecordingButtonState();
        }

        private void ShowBoxDrawingTestScreen()
        {
            var pane = _currentPane;
            var buffer = pane?.Buffer;
            if (buffer == null) return;

            int cols = Math.Max(20, buffer.Cols);
            int inner = Math.Max(2, cols - 2);
            string horizontal = new string('─', inner);
            string middle = "│" + new string('.', inner) + "│";

            var ruler = new System.Text.StringBuilder(cols);
            for (int i = 1; i <= cols; i++)
            {
                ruler.Append((char)('0' + (i % 10)));
            }

            var screen = new System.Text.StringBuilder();
            screen.AppendLine("[Nova] Box Drawing Repro");
            screen.AppendLine(ruler.ToString());
            screen.AppendLine("┌" + horizontal + "┐");
            screen.AppendLine(middle);
            screen.AppendLine("└" + horizontal + "┘");
            screen.AppendLine("┼┼┼┼┼  │││││  ─────");

            buffer.Clear(resetCursor: true);
            buffer.SetCursorPosition(0, 0);
            buffer.WriteContent(screen.ToString(), false);
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
            await InitiateSftpTransferAsync(profile, sessionId, direction, kind);
        }

        internal Task InitiateSftpTransferForTest(
            TerminalProfile profile,
            Guid sessionId,
            TransferDirection direction,
            TransferKind kind)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return InitiateSftpTransferAsync(profile, sessionId, direction, kind);
        }

        internal Task StartSidebarDownloadForTest(
            TerminalProfile profile,
            Guid sessionId,
            string selectedRemotePath,
            TransferKind kind)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return InitiateSidebarSftpTransferAsync(
                profile,
                sessionId,
                TransferDirection.Download,
                kind,
                selectedRemotePath);
        }

        internal Task StartSidebarUploadForTest(
            TerminalProfile profile,
            Guid sessionId,
            string remoteDirectory,
            TransferKind kind)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return InitiateSidebarSftpTransferAsync(
                profile,
                sessionId,
                TransferDirection.Upload,
                kind,
                remoteDirectory);
        }

        private async Task InitiateSftpTransferAsync(
            TerminalProfile profile,
            Guid sessionId,
            TransferDirection direction,
            TransferKind kind)
        {
            var request = TransferDialogRequest.ForAction(
                direction,
                kind,
                profile.DefaultRemoteDir ?? "~",
                profile.Id,
                sessionId);

            TransferDialogResult? result = await ShowTransferDialogAsync(request);
            if (result is not { IsConfirmed: true })
            {
                return;
            }

            var job = new TransferJob
            {
                SessionId = sessionId,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Direction = direction,
                Kind = kind,
                LocalPath = result.LocalPath,
                RemotePath = result.RemotePath
            };

            EnqueueTransferJob(job);
        }

        private Task InitiateSidebarSftpTransfer(
            TerminalPane srcPane,
            TransferDirection direction,
            TransferKind kind,
            string remotePath)
        {
            if (srcPane.Profile == null || srcPane.Session == null)
            {
                return Task.CompletedTask;
            }

            return InitiateSidebarSftpTransferAsync(
                srcPane.Profile,
                srcPane.Session.Id,
                direction,
                kind,
                remotePath);
        }

        private async Task InitiateSidebarSftpTransferAsync(
            TerminalProfile profile,
            Guid sessionId,
            TransferDirection direction,
            TransferKind kind,
            string remotePath)
        {
            if (direction == TransferDirection.Upload)
            {
                string? localPath = kind == TransferKind.File
                    ? await PickLocalUploadFilePathAsync()
                    : await PickLocalUploadFolderPathAsync();
                if (string.IsNullOrWhiteSpace(localPath))
                {
                    return;
                }

                var uploadJob = new TransferJob
                {
                    SessionId = sessionId,
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    Direction = direction,
                    Kind = kind,
                    LocalPath = localPath,
                    RemotePath = remotePath
                };

                EnqueueTransferJob(uploadJob);
                return;
            }

            string? localDownloadPath = kind == TransferKind.File
                ? await PickLocalDownloadFilePathAsync(ResolveSuggestedDownloadFileName(remotePath))
                : await PickLocalDownloadFolderPathAsync();
            if (string.IsNullOrWhiteSpace(localDownloadPath))
            {
                return;
            }

            var job = new TransferJob
            {
                SessionId = sessionId,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Direction = direction,
                Kind = kind,
                LocalPath = localDownloadPath,
                RemotePath = remotePath
            };

            EnqueueTransferJob(job);
        }

        internal virtual async Task<TransferDialogResult?> ShowTransferDialogAsync(TransferDialogRequest request)
        {
            var dialog = new TransferDialog(request);
            return await dialog.ShowDialog<TransferDialogResult?>(this);
        }

        internal virtual async Task<string?> PickLocalUploadFilePathAsync()
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select File to Upload",
                AllowMultiple = false
            });

            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }

        internal virtual async Task<string?> PickLocalUploadFolderPathAsync()
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Folder to Upload",
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }

        internal virtual async Task<string?> PickLocalDownloadFilePathAsync(string suggestedFileName)
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Select Local Destination",
                SuggestedFileName = suggestedFileName
            });

            return file?.Path.LocalPath;
        }

        internal virtual async Task<string?> PickLocalDownloadFolderPathAsync()
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                return null;
            }

            IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Local Destination Folder",
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }

        internal virtual void EnqueueTransferJob(TransferJob job)
        {
            SftpService.Instance.AddJob(job);
            ShowTransferCenter();
        }

        private static string ResolveSuggestedDownloadFileName(string remotePath)
        {
            string trimmed = remotePath.Trim().TrimEnd('/', '\\');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return "download";
            }

            string fileName = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(fileName)
                ? "download"
                : fileName;
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
                        var results = GetPaletteCommands(box.Text ?? "");
                        list.ItemsSource = results;
                        if (results.Count > 0) list.SelectedIndex = 0;
                    }
                };

                // Filter on text changed too for smoother feel
                box.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == "Text")
                    {
                        var results = GetPaletteCommands(box.Text ?? "");
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
                    list.ItemsSource = GetPaletteCommands("");
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

        private ConnectionManager? EnsureConnectionManagerControl()
        {
            if (_connectionManagerControl != null)
            {
                return _connectionManagerControl;
            }

            var host = this.FindControl<ContentControl>("ConnectionManagerHost");
            if (host == null)
            {
                return null;
            }

            var connManager = new ConnectionManager();
            connManager.ApplyTheme(_settings.ActiveTheme);
            connManager.OnQuickOpenRequested += (profile, target, diagnosticsLevel) =>
            {
                HandleSshQuickOpen(profile, target, diagnosticsLevel);
                ToggleConnections();
            };
            connManager.OnCopyLaunchCommandRequested += (profile, diagnosticsLevel) =>
            {
                _ = CopySshLaunchCommandAsync(profile, diagnosticsLevel);
            };
            connManager.OnConnectionDetailsRequested += (profile, diagnosticsLevel) =>
            {
                _ = ShowSshConnectionDetailsAsync(profile, diagnosticsLevel);
            };
            connManager.OnProfilesChanged += () =>
            {
                _sshConnectionService.SaveConnectionProfiles(connManager.GetAllProfiles());
            };
            connManager.OnSyncRequested += HandleSshSync;
            connManager.OnNewConnectionRequested += async () =>
            {
                await ShowNewSshConnectionDialogAsync(null);
            };
            connManager.OnEditProfile += async (profile) =>
            {
                await ShowNewSshConnectionDialogAsync(profile);
            };

            host.Content = connManager;
            _connectionManagerControl = connManager;
            return connManager;
        }

        private TransferCenter? EnsureTransferCenterControl()
        {
            if (_transferCenterControl != null)
            {
                return _transferCenterControl;
            }

            var host = this.FindControl<ContentControl>("TransferCenterHost");
            if (host == null)
            {
                return null;
            }

            _transferCenterControl = new TransferCenter();
            host.Content = _transferCenterControl;
            return _transferCenterControl;
        }

        private void ToggleTransferCenter()
        {
            var overlay = this.FindControl<Border>("TransferOverlay");
            if (overlay != null)
            {
                if (!overlay.IsVisible)
                {
                    _ = EnsureTransferCenterControl();
                }

                overlay.IsVisible = !overlay.IsVisible;
            }
        }

        private void ShowTransferCenter()
        {
            void show()
            {
                var overlay = this.FindControl<Border>("TransferOverlay");
                if (overlay != null)
                {
                    _ = EnsureTransferCenterControl();
                    overlay.IsVisible = true;
                }
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                show();
            }
            else
            {
                Dispatcher.UIThread
                    .InvokeAsync(show, DispatcherPriority.Send)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private void InitializeTransferCenterUI()
        {
            var btnClose = this.FindControl<Button>("BtnCloseTransfers");
            var overlay = this.FindControl<Border>("TransferOverlay");
            var titleBar = this.FindControl<Grid>("TransferTitleBar");

            if (btnClose != null) btnClose.Click += (s, e) => ToggleTransferCenter();

            if (overlay != null)
            {
                _transferOverlayTransform = overlay.RenderTransform as TranslateTransform;
                if (_transferOverlayTransform == null)
                {
                    _transferOverlayTransform = new TranslateTransform();
                    overlay.RenderTransform = _transferOverlayTransform;
                }
            }

            if (titleBar != null)
            {
                titleBar.PointerPressed += OnTransferTitleBarPointerPressed;
                titleBar.PointerMoved += OnTransferTitleBarPointerMoved;
                titleBar.PointerReleased += OnTransferTitleBarPointerReleased;
                titleBar.PointerCaptureLost += OnTransferTitleBarPointerCaptureLost;
            }
        }

        private void OnTransferTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control titleBar || e.Source is Button)
            {
                return;
            }

            if (!e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _isDraggingTransferOverlay = true;
            _transferOverlayDragStart = e.GetPosition(this);
            _transferOverlayOffsetStart = new Point(
                _transferOverlayTransform?.X ?? 0,
                _transferOverlayTransform?.Y ?? 0);
            e.Pointer.Capture(titleBar);
            e.Handled = true;
        }

        private void OnTransferTitleBarPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isDraggingTransferOverlay || _transferOverlayTransform == null)
            {
                return;
            }

            Point current = e.GetPosition(this);
            Vector delta = current - _transferOverlayDragStart;
            _transferOverlayTransform.X = _transferOverlayOffsetStart.X + delta.X;
            _transferOverlayTransform.Y = _transferOverlayOffsetStart.Y + delta.Y;
        }

        private void OnTransferTitleBarPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            StopTransferTitleBarDrag(sender, e);
        }

        private void OnTransferTitleBarPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            _isDraggingTransferOverlay = false;
        }

        private void StopTransferTitleBarDrag(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is Control titleBar)
            {
                e.Pointer.Capture(null);
            }

            _isDraggingTransferOverlay = false;
        }

        private async Task OpenSettings(int tabIndex, Guid? profileId = null)
        {
            var sw = new SettingsWindow(tabIndex, profileId);

            // Snapshot the live-previewed values so Cancel can restore them (#167).
            // The preview handlers below mutate _settings directly; without this,
            // closing the dialog without saving left the preview values live, and any
            // later unrelated Save() persisted them to disk.
            var previewSnapshot = new
            {
                _settings.WindowOpacity,
                _settings.BlurEffect,
                _settings.BackgroundImagePath,
                _settings.BackgroundImageOpacity,
                _settings.BackgroundImageStretch,
                _settings.FontFamily,
                _settings.FontSize,
                _settings.ThemeName
            };

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

                if (_sshLegacyMigrationService.MigrateLegacyProfiles(_settings))
                {
                    _settings.Save();
                }

                RefreshProfileUIs();
                ApplyThemeToUI();
                ApplySettingsToAllTabs();
                UpdateTransparencyHints();
                // Live-apply the agent-host observe endpoint (no restart needed).
                AgentHost.AgentHostService.Instance.Apply(_settings.AgentAccessObserveEnabled);

                // Refresh Connection Manager if open (or just always update it)
                _connectionManagerControl?.LoadProfiles(_sshConnectionService.GetConnectionProfiles());
            }
            else
            {
                // Cancel: revert the live preview (#167).
                _settings.WindowOpacity = previewSnapshot.WindowOpacity;
                _settings.BlurEffect = previewSnapshot.BlurEffect;
                _settings.BackgroundImagePath = previewSnapshot.BackgroundImagePath;
                _settings.BackgroundImageOpacity = previewSnapshot.BackgroundImageOpacity;
                _settings.BackgroundImageStretch = previewSnapshot.BackgroundImageStretch;
                _settings.FontFamily = previewSnapshot.FontFamily;
                _settings.FontSize = previewSnapshot.FontSize;
                _settings.ThemeName = previewSnapshot.ThemeName;
                _settings.RefreshActiveTheme();

                ApplyThemeToUI();
                ApplySettingsToAllTabs();
                UpdateTransparencyHints();
                UpdateTabVisuals();
            }
        }

        private async Task ShowNewSshConnectionDialogAsync(TerminalProfile? existingProfile)
        {
            var vm = _sshConnectionService.CreateEditorViewModel(existingProfile);
            vm.BackendKind ??= NovaTerminal.Platform.Ssh.Models.SshBackendKind.OpenSsh;
            vm.ExperimentalNativeSshEnabled = _settings.ExperimentalNativeSshEnabled;
            var dialog = new NewSshConnectionView(vm);
            ApplyThemeToDialogWindow(dialog);
            bool saved = await dialog.ShowDialog<bool>(this);

            if (!saved)
            {
                return;
            }

            try
            {
                var savedProfile = _sshConnectionService.SaveProfile(vm);
                TerminalProfile profile = _sshConnectionService.GetConnectionProfile(savedProfile.Id)
                    ?? SshConnectionService.ToRuntimeProfile(savedProfile);
                RefreshProfileUIs();

                if (vm.ConnectAfterSave)
                {
                    if (profile.SshBackendKind == NovaTerminal.Platform.Ssh.Models.SshBackendKind.Native &&
                        !_settings.ExperimentalNativeSshEnabled)
                    {
                        await ShowSimpleMessageDialogAsync(
                            "Native SSH disabled",
                            "This profile was saved with the Native backend, but native SSH is disabled globally. Enable ExperimentalNativeSshEnabled in settings or switch the profile back to OpenSSH.");
                        return;
                    }

                    AddTab(profile, SshDiagnosticsLevel.None);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to save SSH connection: {ex.Message}");
            }
        }

        private async Task CopySshLaunchCommandAsync(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
            {
                await ShowSimpleMessageDialogAsync("Copy launch command", "Clipboard is not available.");
                return;
            }

            try
            {
                string command = _sshConnectionService.BuildLaunchCommand(profile, diagnosticsLevel);
                await topLevel.Clipboard.SetTextAsync(command);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to copy SSH command: {ex.Message}");
                await ShowSimpleMessageDialogAsync("Copy launch command", ex.Message);
            }
        }

        private async Task ShowSshConnectionDetailsAsync(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel)
        {
            try
            {
                var details = _sshConnectionService.BuildLaunchDetails(profile, diagnosticsLevel);
                await ShowConnectionDetailsDialogAsync(details, diagnosticsLevel);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to show SSH connection details: {ex.Message}");
                await ShowSimpleMessageDialogAsync("Connection details", ex.Message);
            }
        }

        private async Task ShowSimpleMessageDialogAsync(string title, string message)
        {
            var dialog = CreateThemedDialogWindow(title, 520, 220, canResize: false);

            var closeButton = new Button { Content = "Close", Width = 92 };
            closeButton.Click += (_, __) => dialog.Close();

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
                            Text = message,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { closeButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
        }

        protected virtual Task ExecuteOpenRecordingCommandAsync()
        {
            return OpenRecordingAsync();
        }

        private async Task OpenRecordingAsync()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                await ShowSimpleMessageDialogAsync("Open Recording", "File picker is not available in the current window.");
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Replay File",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Nova Recordings") { Patterns = new[] { "*.rec", "*.cast" } } }
            });

            if (files.Count < 1)
            {
                return;
            }

            var path = files[0].Path.LocalPath;
            var replayWin = new NovaTerminal.UI.Replay.ReplayWindow(path);
            replayWin.Show();
        }

        private async Task ExecuteUiCommandAsync(Func<Task> action, string commandTitle)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing command {commandTitle}: {ex.Message}");
                await ShowSimpleMessageDialogAsync(commandTitle, ex.Message);
            }
        }

        private void OpenRecordingsFolder()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.RecordingsDirectory);
                OpenPathInShell(new ShellOpenRequest(AppPaths.RecordingsDirectory, null));
            }
            catch
            {
                ShowRecordingToast(
                    "Unable to open recordings folder",
                    AppPaths.RecordingsDirectory,
                    null,
                    AppPaths.RecordingsDirectory,
                    autoHide: true);
            }
        }

        private void OpenRecordingToastFolder()
        {
            if (string.IsNullOrWhiteSpace(_recordingToastFolderPath))
            {
                OpenRecordingsFolder();
                return;
            }

            try
            {
                Directory.CreateDirectory(_recordingToastFolderPath);
                var request = ResolveRecordingRevealRequest(_recordingToastFilePath, _recordingToastFolderPath, OperatingSystem.IsWindows());
                OpenPathInShell(request);
            }
            catch
            {
                ShowRecordingToast(
                    "Unable to open recordings folder",
                    _recordingToastFolderPath,
                    _recordingToastFilePath,
                    _recordingToastFolderPath,
                    autoHide: true);
            }
        }

        private static void OpenPathInShell(ShellOpenRequest request)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = request.FileName,
                Arguments = request.Arguments,
                UseShellExecute = true
            });
        }

        internal static ShellOpenRequest ResolveRecordingRevealRequest(string? filePath, string recordingsDirectory, bool isWindows)
        {
            if (isWindows && !string.IsNullOrWhiteSpace(filePath))
            {
                return new ShellOpenRequest("explorer.exe", $"/select,\"{filePath}\"");
            }

            return new ShellOpenRequest(recordingsDirectory, null);
        }

        private async Task ShowConnectionDetailsDialogAsync(SshLaunchDetails details, SshDiagnosticsLevel diagnosticsLevel)
        {
            var dialog = CreateThemedDialogWindow("Connection details", 760, 340, canResize: false);

            var sshPathBox = new TextBox { Text = details.SshPath, IsReadOnly = true };
            var configPathBox = new TextBox { Text = details.ConfigPath, IsReadOnly = true };
            var commandBox = new TextBox
            {
                Text = details.CommandLine,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                Height = 72
            };

            var closeButton = new Button { Content = "Close", Width = 92 };
            closeButton.Click += (_, __) => dialog.Close();

            var copyButton = new Button { Content = "Copy command", Width = 120 };
            copyButton.Click += async (_, __) =>
            {
                var topLevel = TopLevel.GetTopLevel(dialog);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(details.CommandLine);
                }
            };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = $"Diagnostics: {diagnosticsLevel}" },
                        new TextBlock { Text = $"Alias: {details.Alias}" },
                        new TextBlock { Text = "Resolved SSH path" },
                        sshPathBox,
                        new TextBlock { Text = "Generated config path" },
                        configPathBox,
                        new TextBlock { Text = "Repro command" },
                        commandBox,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { copyButton, closeButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
        }

        private void RefreshProfileUIs()
        {
            PopulateNewTabMenu();
            SetupCommandPalette();

            // Refresh Connection Manager if open (or just always update it)
            _connectionManagerControl?.LoadProfiles(_sshConnectionService.GetConnectionProfiles());
        }

        private void ExecuteCommand(TerminalCommand cmd)
        {
            ToggleCommandPalette(); // Close first
            RecordCommandUsage(cmd.Id);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    cmd.Action?.Invoke();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error executing command {cmd.Title}: {ex.Message}");
                }
            }, DispatcherPriority.Background);
        }

        private List<TerminalCommand> GetPaletteCommands(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return CommandPaletteOrdering.OrderForEmptyQuery(CommandRegistry.GetCommands(), _commandPaletteUsage).ToList();
            }

            return CommandPaletteOrdering.OrderSearchResults(CommandRegistry.Search(query), _commandPaletteUsage).ToList();
        }

        private void RecordCommandUsage(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
            {
                return;
            }

            try
            {
                _commandPaletteUsageStore.RecordUse(commandId, DateTimeOffset.UtcNow);
                _commandPaletteUsageStore.Save();
                _commandPaletteUsage = new Dictionary<string, CommandPaletteUsageEntry>(_commandPaletteUsageStore.Load(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // Keep command execution resilient if usage persistence is unavailable.
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
            _recordingToastTimer.Stop();
            _globalHotkey?.Dispose();
            AgentHost.AgentHostService.Instance.Stop();
        }

        private void RegisterPaneOwners(TabItem tabItem, Control control)
        {
            if (control is TerminalPane pane)
            {
                _paneOwnerTab[pane] = tabItem;
                AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(pane.PaneId, GetPersistentTabId(tabItem));
                return;
            }

            if (control is Panel panel)
            {
                foreach (var child in panel.Children.OfType<Control>())
                {
                    RegisterPaneOwners(tabItem, child);
                }

                return;
            }

            if (control is Decorator decorator && decorator.Child is Control childControl)
            {
                RegisterPaneOwners(tabItem, childControl);
                return;
            }

            if (control is ContentControl contentControl && contentControl.Content is Control content)
            {
                RegisterPaneOwners(tabItem, content);
            }
        }

        private TabItem? ResolveOwningTabForPane(TerminalPane pane)
        {
            if (_paneOwnerTab.TryGetValue(pane, out var cachedTab))
            {
                return cachedTab;
            }

            var visualTab = pane.FindAncestorOfType<TabItem>();
            if (visualTab != null)
            {
                _paneOwnerTab[pane] = visualTab;
                AgentHost.AgentSessionRegistry.Instance.SetTabAssociation(pane.PaneId, GetPersistentTabId(visualTab));
                return visualTab;
            }

            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null)
            {
                return null;
            }

            foreach (var item in tabs.Items.Cast<TabItem>())
            {
                if (item.Content is Control content)
                {
                    RegisterPaneOwners(item, content);
                    if (_paneOwnerTab.TryGetValue(pane, out cachedTab))
                    {
                        return cachedTab;
                    }
                }
            }

            return null;
        }

        private void UpdateActivePane(TerminalPane pane)
        {
            var ownerTab = ResolveOwningTabForPane(pane);
            if (ownerTab != null) _activePaneByTab[ownerTab] = pane;

            if (_currentPane == pane) return;

            // Unsubscribe from old pane
            if (_currentPane != null)
            {
                _currentPane.RecordingStateChanged -= OnRecordingStateChanged;
                _currentPane.RecordingNotification -= OnRecordingNotification;
            }

            _currentPane = pane;

            // Subscribe to new pane
            if (_currentPane != null)
            {
                _currentPane.RecordingStateChanged += OnRecordingStateChanged;
                _currentPane.RecordingNotification += OnRecordingNotification;
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
            Dispatcher.UIThread.Post(() =>
            {
                UpdateRecordButtonUi(isRecording);
            });
        }

        private void OnRecordingNotification(RecordingNotificationEventArgs notification)
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (notification.Kind)
                {
                    case RecordingNotificationKind.Started:
                        ShowRecordingToast(
                            "Recording started",
                            BuildRecordingToastMessage(notification),
                            notification.FilePath,
                            notification.RecordingsDirectory,
                            autoHide: true);
                        break;
                    case RecordingNotificationKind.Stopped:
                        ShowRecordingToast(
                            "Recording saved",
                            BuildRecordingToastMessage(notification),
                            notification.FilePath,
                            notification.RecordingsDirectory,
                            autoHide: true);
                        break;
                    case RecordingNotificationKind.Failed:
                        ShowRecordingToast(
                            "Recording failed",
                            notification.ErrorMessage ?? "Unable to start recording.",
                            notification.FilePath,
                            notification.RecordingsDirectory,
                            autoHide: true);
                        break;
                }
            });
        }

        private static string BuildRecordingToastMessage(RecordingNotificationEventArgs notification)
        {
            if (!string.IsNullOrWhiteSpace(notification.FilePath))
            {
                return notification.FilePath!;
            }

            return notification.RecordingsDirectory;
        }

        private void ShowRecordingToast(string title, string message, string? filePath, string? folderPath, bool autoHide)
        {
            var toast = this.FindControl<Border>("RecordingToast");
            var titleBlock = this.FindControl<TextBlock>("RecordingToastTitle");
            var messageBlock = this.FindControl<TextBlock>("RecordingToastMessage");
            if (toast == null || titleBlock == null || messageBlock == null)
            {
                return;
            }

            _recordingToastFilePath = filePath;
            _recordingToastFolderPath = folderPath;
            titleBlock.Text = title;
            messageBlock.Text = message;

            // The folder button only makes sense for toasts that have one
            // (recordings). Non-file toasts (long-command completion) would
            // otherwise offer a button that opens an unrelated folder.
            var openFolderButton = this.FindControl<Button>("RecordingToastOpenFolder");
            if (openFolderButton != null)
            {
                openFolderButton.IsVisible = folderPath != null || filePath != null;
            }

            toast.IsVisible = true;

            _recordingToastTimer.Stop();
            if (autoHide)
            {
                _recordingToastTimer.Start();
            }
        }

        private void HideRecordingToast()
        {
            _recordingToastTimer.Stop();
            var toast = this.FindControl<Border>("RecordingToast");
            if (toast != null)
            {
                toast.IsVisible = false;
            }
        }

        private void SyncRecordingButtonState()
        {
            UpdateRecordButtonUi(_currentPane?.IsRecording ?? false);
        }

        private void UpdateRecordButtonUi(bool isRecording)
        {
            var btnRecord = this.FindControl<Button>("BtnRecord");
            var iconRecord = this.FindControl<PathIcon>("IconRecord");

            if (btnRecord == null || iconRecord == null)
            {
                return;
            }

            var activeBrush = new SolidColorBrush(Color.Parse("#F1636B"));
            var inactiveBrush = new SolidColorBrush(_settings.ActiveTheme.GetContrastForeground().ToAvaloniaColor());

            btnRecord.Foreground = isRecording ? activeBrush : inactiveBrush;
            iconRecord.Foreground = isRecording ? activeBrush : inactiveBrush;
            btnRecord.Background = isRecording ? new SolidColorBrush(Color.Parse("#30F1636B")) : Brushes.Transparent;
            var recordingShortcut = GetEffectiveShortcutBinding("toggle_recording", "Ctrl+Shift+R");
            ToolTip.SetTip(btnRecord, isRecording
                ? $"Stop Recording ({recordingShortcut})"
                : $"Record Session ({recordingShortcut})");
        }
    }
}
