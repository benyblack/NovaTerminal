using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Core;

public sealed class VerticalTabStripTests
{
    private static TerminalSettings GetSettings(NovaTerminal.MainWindow window)
        => (TerminalSettings)typeof(NovaTerminal.MainWindow)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static ScrollViewer? InvokeFindTabHeaderScrollViewer(NovaTerminal.MainWindow window)
        => (ScrollViewer?)typeof(NovaTerminal.MainWindow)
            .GetMethod("FindTabHeaderScrollViewer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

    private static Orientation? InvokeGetTabItemsPanelOrientation(NovaTerminal.MainWindow window)
    {
        var presenter = (ItemsPresenter?)typeof(NovaTerminal.MainWindow)
            .GetMethod("FindTabItemsPresenter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        return (presenter?.Panel as StackPanel)?.Orientation;
    }

    private static NovaTerminal.MainWindow CreateShownWindow()
    {
        var window = TestMainWindowFactory.Create();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void ApplyTabLayout_Vertical_SidebarSizedFromSettings_AndOverflowMathSkipped()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        settings.VerticalTabStripWidth = 260;

        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVerticalTabStripActive);

        var scrollViewer = InvokeFindTabHeaderScrollViewer(window);
        Assert.NotNull(scrollViewer);
        Assert.Equal(260, scrollViewer!.Width);
        Assert.True(double.IsNaN(scrollViewer.Height), "vertical strip must not keep the 36px horizontal height");
        Assert.Equal(new Avalonia.Thickness(0), scrollViewer.Margin);

        var tabs = window.FindControl<TabControl>("Tabs");
        Assert.NotNull(tabs);
        Assert.Contains("vertical-tabs", tabs!.Classes);

        var badge = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(badge);
        Assert.False(badge!.IsVisible);

        Assert.Equal(Orientation.Vertical, InvokeGetTabItemsPanelOrientation(window));
    }

    [AvaloniaFact]
    public void ApplyTabLayout_GarbageWidth_FallsBackToClampedDefault()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        settings.VerticalTabStripWidth = -1;

        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TabStripLayout.DefaultSidebarWidth, InvokeFindTabHeaderScrollViewer(window)!.Width);
    }

    [AvaloniaFact]
    public void ApplyTabLayout_RoundTrip_RestoresHorizontalStrip_WithoutTouchingTabItems()
    {
        var window = CreateShownWindow();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tabItemsBefore = tabs.Items.Cast<TabItem>().ToList();
        var contentBefore = tabItemsBefore.Select(t => t.Content).ToList();

        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        settings.TabStripOrientation = "Horizontal";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVerticalTabStripActive);
        Assert.DoesNotContain("vertical-tabs", tabs.Classes);

        var scrollViewer = InvokeFindTabHeaderScrollViewer(window)!;
        Assert.Equal(36, scrollViewer.Height);
        Assert.True(double.IsNaN(scrollViewer.Width), "horizontal strip must not keep the sidebar width");
        Assert.True(scrollViewer.Margin.Right > 0, "horizontal strip must reserve title-bar space again");
        Assert.Equal(Orientation.Horizontal, InvokeGetTabItemsPanelOrientation(window));

        // The same TabItem instances with the same Content must survive both swaps —
        // panes/sessions are never disposed or recreated by a layout change.
        Assert.Equal(tabItemsBefore, tabs.Items.Cast<TabItem>().ToList());
        Assert.Equal(contentBefore, tabs.Items.Cast<TabItem>().Select(t => t.Content).ToList());
    }

    [AvaloniaFact]
    public void VerticalHeader_TitleIsFirstTextBlock_SoExistingLabelPlumbingStillWorks()
    {
        var window = CreateShownWindow();
        GetSettings(window).TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();

        // The status dot and preview line exist...
        Assert.NotNull(NovaTerminal.MainWindow.FindTabHeaderDescendant<Avalonia.Controls.Shapes.Ellipse>(tab.Header, "TabStatusDot"));
        var preview = NovaTerminal.MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine");
        Assert.NotNull(preview);

        // ...and the title plumbing (first-TextBlock contract) still resolves the TITLE, not the preview.
        preview!.Text = "PREVIEW_SENTINEL";
        Assert.NotEqual("PREVIEW_SENTINEL", GetTabHeaderTextOf(window, tab));
    }

    private static string GetTabHeaderTextOf(NovaTerminal.MainWindow window, TabItem tab)
        => (string)typeof(NovaTerminal.MainWindow)
            .GetMethod("GetTabHeaderText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tab })!;

    [AvaloniaFact]
    public void HorizontalMode_KeepsPlainHeaders()
    {
        var window = CreateShownWindow();
        // Force horizontal explicitly rather than trusting the loaded settings: window-creating
        // tests read the developer's real TerminalSettings, and a dev machine left in vertical
        // mode would otherwise make this test's premise false.
        GetSettings(window).TabStripOrientation = "Horizontal";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();
        Assert.Null(NovaTerminal.MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine"));
    }

    // ---- Agent-aware vertical headers: marker chips, dot precedence, accent bar ----

    private static TabItem AddPlainVerticalTab(NovaTerminal.MainWindow window)
    {
        var tabs = window.FindControl<TabControl>("Tabs")!;
        // Fresh, unselected tab (selection clears attention; these tests control state exactly).
        var tab = new TabItem { Content = new Border() };
        tabs.Items.Add(tab);
        window.ApplyTabLayout(); // rebuild headers so the new tab gets a vertical row
        Dispatcher.UIThread.RunJobs();
        return tab;
    }

    private static TextBlock ChipOf(TabItem tab, string name)
    {
        var chip = NovaTerminal.MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, name);
        Assert.NotNull(chip);
        return chip!;
    }

    private static Avalonia.Media.Color? DotColorOf(TabItem tab)
    {
        var dot = NovaTerminal.MainWindow.FindTabHeaderDescendant<Avalonia.Controls.Shapes.Ellipse>(tab.Header, "TabStatusDot");
        return (dot?.Fill as Avalonia.Media.ISolidColorBrush)?.Color;
    }

    [AvaloniaFact]
    public void VerticalHeader_MarkerChips_ExistHiddenByDefault_DotTransparent()
    {
        var window = CreateShownWindow();
        try
        {
            GetSettings(window).TabStripOrientation = "Vertical";
            window.ApplyTabLayout();
            Dispatcher.UIThread.RunJobs();
            var tab = AddPlainVerticalTab(window);

            // All four chips exist, hidden until a marker says otherwise...
            foreach (string name in new[] { "TabBellChip", "TabActivityChip", "TabAgentWroteChip", "TabAgentWatchedChip" })
            {
                var chip = ChipOf(tab, name);
                Assert.False(chip.IsVisible, $"{name} must start hidden");
                Assert.False(chip.IsHitTestVisible, $"{name} must not steal header presses");
            }

            // ...and a fresh tab paints no dot.
            Assert.Equal(Avalonia.Media.Colors.Transparent, DotColorOf(tab));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void UpdateTabVisuals_Vertical_AccentBarOnEveryTab_HorizontalClearsIt()
    {
        var window = CreateShownWindow();
        try
        {
            var settings = GetSettings(window);
            settings.TabStripOrientation = "Vertical";
            window.ApplyTabLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateTabVisuals();

            var tabs = window.FindControl<TabControl>("Tabs")!;
            var allTabs = tabs.Items.Cast<TabItem>().ToList();
            Assert.NotEmpty(allTabs);

            // Constant 3px left thickness on every tab (selected AND not): the border brush
            // paints the bar only on selection, but the thickness never toggles, so the
            // title cannot shift 3px when selection changes.
            foreach (var tab in allTabs)
            {
                Assert.Equal(new Avalonia.Thickness(3, 0, 0, 0), tab.BorderThickness);
            }

            // The selected tab's template border must actually render the left bar, not the
            // App.axaml horizontal underline (0 0 0 2) — the local thickness must survive the
            // template binding end to end.
            var selected = Assert.IsType<TabItem>(tabs.SelectedItem);
            var selectedBorder = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(selected)
                .OfType<Border>()
                .FirstOrDefault(b => b.Name == "PART_Border");
            Assert.NotNull(selectedBorder);
            Assert.Equal(new Avalonia.Thickness(3, 0, 0, 0), selectedBorder!.BorderThickness);

            // Round-trip to horizontal: the local value must be cleared (not Thickness(0)),
            // so the App.axaml selected style (0 0 0 2) keeps driving the underline.
            settings.TabStripOrientation = "Horizontal";
            window.ApplyTabLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateTabVisuals();

            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                Assert.Equal(default(Avalonia.Thickness), tab.BorderThickness);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void UpdateVerticalTabExtras_BellState_BellChipVisible_ActivityChipSuppressed_DotAmber()
    {
        var window = CreateShownWindow();
        try
        {
            GetSettings(window).TabStripOrientation = "Vertical";
            window.ApplyTabLayout();
            Dispatcher.UIThread.RunJobs();
            var tab = AddPlainVerticalTab(window);

            // Bell + activity together: bell wins both the chip and the dot.
            window.SetTabMarkerStateForTest(tab, hasBell: true, hasActivity: true, NovaTerminal.AgentHost.AgentAttentionTier.Idle);
            window.UpdateTabVisuals();
            Dispatcher.UIThread.RunJobs();

            Assert.True(ChipOf(tab, "TabBellChip").IsVisible);
            Assert.False(ChipOf(tab, "TabActivityChip").IsVisible);
            Assert.Equal(new Avalonia.Media.Color(0xFF, 0xFF, 0xD2, 0x5A), DotColorOf(tab)); // #FFD25A
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void UpdateVerticalTabExtras_AgentWrote_WroteChipVisible_DotAmberAgent()
    {
        var window = CreateShownWindow();
        try
        {
            GetSettings(window).TabStripOrientation = "Vertical";
            window.ApplyTabLayout();
            Dispatcher.UIThread.RunJobs();
            var tab = AddPlainVerticalTab(window);

            window.SetTabMarkerStateForTest(tab, hasBell: false, hasActivity: false, NovaTerminal.AgentHost.AgentAttentionTier.Wrote);
            window.UpdateTabVisuals();
            Dispatcher.UIThread.RunJobs();

            Assert.True(ChipOf(tab, "TabAgentWroteChip").IsVisible);
            Assert.False(ChipOf(tab, "TabAgentWatchedChip").IsVisible);
            Assert.Equal(new Avalonia.Media.Color(0xFF, 0xE8, 0xA3, 0x3D), DotColorOf(tab)); // #E8A33D
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void UpdateVerticalTabExtras_AgentWatched_VisibleOnlyUnderAllPolicy()
    {
        var window = CreateShownWindow();
        try
        {
            GetSettings(window).TabStripOrientation = "Vertical";
            window.ApplyTabLayout();
            Dispatcher.UIThread.RunJobs();
            var tab = AddPlainVerticalTab(window);
            var settings = GetSettings(window);

            // Default/WritesOnly policy: a watched tier shows neither chip nor dot.
            settings.AgentIndicatorTabRollup = "WritesOnly";
            window.SetTabMarkerStateForTest(tab, hasBell: false, hasActivity: false, NovaTerminal.AgentHost.AgentAttentionTier.Watched);
            window.UpdateTabVisuals();
            Dispatcher.UIThread.RunJobs();
            Assert.False(ChipOf(tab, "TabAgentWatchedChip").IsVisible);
            Assert.Equal(Avalonia.Media.Colors.Transparent, DotColorOf(tab));

            // "All" rollup: chip visible, blue agent dot.
            settings.AgentIndicatorTabRollup = "All";
            window.UpdateTabVisuals();
            Dispatcher.UIThread.RunJobs();
            Assert.True(ChipOf(tab, "TabAgentWatchedChip").IsVisible);
            Assert.Equal(new Avalonia.Media.Color(0xFF, 0x4F, 0xB0, 0xD4), DotColorOf(tab)); // #4FB0D4
        }
        finally
        {
            window.Close();
        }
    }

    // Vertical headers replaced the in-title attention markers with trailing chips
    // (UpdateVerticalTabExtras), which is wired in UpdateTabVisuals as
    // BuildTabDisplayLabels(..., includeMarkers: !_isVerticalTabStrip). This pins both
    // halves of that split end-to-end through a bell: the title TextBlock must EXCLUDE
    // the marker suffix (otherwise the bell is double-reported as chip AND title text),
    // while the header host's tooltip - BuildFullTabLabel, marker-suffixed in both
    // modes - must still INCLUDE it (otherwise the marker is lost entirely on hover).
    [AvaloniaFact]
    public void UpdateTabVisuals_Vertical_BellMarkerExcludedFromTitle_ButPresentInTooltip()
    {
        var window = CreateShownWindow();
        try
        {
            GetSettings(window).TabStripOrientation = "Vertical";
            window.ApplyTabLayout();
            Dispatcher.UIThread.RunJobs();
            var tab = AddPlainVerticalTab(window);

            window.SetTabMarkerStateForTest(tab, hasBell: true, hasActivity: false, NovaTerminal.AgentHost.AgentAttentionTier.Idle);
            window.UpdateTabVisuals();
            Dispatcher.UIThread.RunJobs();

            // The chip is the vertical surface for the bell...
            Assert.True(ChipOf(tab, "TabBellChip").IsVisible);
            // ...so the title text must not ALSO carry the marker suffix.
            Assert.DoesNotContain("🔔", GetTabHeaderTextOf(window, tab));

            // The tooltip reads the untruncated full label, which keeps its markers
            // regardless of strip orientation.
            var host = Assert.IsType<Border>(tab.Header);
            Assert.Contains("🔔", Assert.IsType<string>(Avalonia.Controls.ToolTip.GetTip(host)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RefreshTabStatuses_WorkingTracker_PaintsThemeDot_AndIdleClearsIt()
    {
        var window = CreateShownWindow();
        GetSettings(window).TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;
        // Use an UNSELECTED tab: selection clears attention and this test must control state exactly.
        var tab = new TabItem { Content = new Border() };
        tabs.Items.Add(tab);
        window.ApplyTabLayout(); // rebuild headers so the new tab gets a vertical row
        Dispatcher.UIThread.RunJobs();

        window.GetTabStatusTracker(tab).NoteOutput(DateTime.UtcNow);
        window.RefreshTabStatuses();
        Dispatcher.UIThread.RunJobs();

        var dot = NovaTerminal.MainWindow.FindTabHeaderDescendant<Avalonia.Controls.Shapes.Ellipse>(tab.Header, "TabStatusDot");
        Assert.NotNull(dot);
        Assert.NotEqual(Avalonia.Media.Brushes.Transparent, dot!.Fill);

        // 2s later with no output the burst is over (too short for Attention) → dot clears.
        window.GetTabStatusTracker(tab).NoteSelected(); // belt-and-braces: no stale attention
        System.Threading.Thread.Sleep(2100);
        window.RefreshTabStatuses();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Avalonia.Media.Brushes.Transparent, dot.Fill);
    }

    // The grip control is a permanent part of the single inline template (Task 6) - it is
    // never re-templated in/out. What differs by mode is IsVisible, toggled by
    // UpdateTabHeaderViewport. So: horizontal keeps the grip present but hidden; vertical
    // (after a layout pass) makes it visible.
    [AvaloniaFact]
    public void VerticalMode_ShowsResizeGrip_HorizontalHidesIt()
    {
        var window = CreateShownWindow();
        // Force horizontal explicitly rather than trusting the loaded settings: window-creating
        // tests read the developer's real TerminalSettings, and a dev machine left in vertical
        // mode would otherwise make this test's premise false.
        GetSettings(window).TabStripOrientation = "Horizontal";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var gripHorizontal = FindResizeGrip(window);
        Assert.NotNull(gripHorizontal);
        Assert.False(gripHorizontal!.IsVisible);

        GetSettings(window).TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var gripVertical = FindResizeGrip(window);
        Assert.NotNull(gripVertical);
        Assert.True(gripVertical!.IsVisible);
    }

    // Regression for the review finding: a viewport pass firing mid-drag (activity-driven -
    // QueueTabVisualRefresh on pane output/bell, the 1s tab-status timer) must not stomp
    // scrollViewer.Width back to the stale persisted setting, or an in-progress grip drag is
    // silently discarded. Real pointer-event simulation isn't practical in the headless test
    // host, so this drives the mid-drag state through the internal
    // IsTabStripGripDraggingForTest seam instead of PointerPressed.
    [AvaloniaFact]
    public void UpdateTabVisuals_DuringGripDrag_DoesNotStompWidth_ThenRevertsOnceDragEnds()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        settings.VerticalTabStripWidth = 260;
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var scrollViewer = InvokeFindTabHeaderScrollViewer(window)!;
        Assert.Equal(260, scrollViewer.Width);

        // Simulate mid-drag: PointerMoved would have set a non-persisted width like this.
        window.IsTabStripGripDraggingForTest = true;
        scrollViewer.Width = 400;

        window.UpdateTabVisuals();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(400, scrollViewer.Width); // pass must not have reverted the live drag value

        // Drag ends (PointerReleased/PointerCaptureLost clears the flag) - the next pass is free
        // to resync from the persisted setting again.
        window.IsTabStripGripDraggingForTest = false;
        window.UpdateTabVisuals();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(260, scrollViewer.Width);
    }

    private static Border? FindResizeGrip(NovaTerminal.MainWindow window)
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "PART_TabStripResizeGrip");

    // Regression for the review finding: UpdateTabOverflowIndicator had no vertical guard, so
    // viewportWidth (≈ sidebar width, NOT "space for N tabs") ran through the same horizontal
    // clipping math used for the scrolling horizontal strip. With several tabs in the sidebar
    // that falsely set the overflow badge, turned the tab-list title-bar button amber, and wrote
    // a "— N hidden" tooltip - even though the vertical sidebar scrolls its own overflow and has
    // nothing actually hidden. Opening the tab-list flyout (PopulateTabListMenu) calls
    // UpdateTabOverflowIndicator too, so this drives it through that real caller rather than the
    // private method directly.
    [AvaloniaFact]
    public void PopulateTabListMenu_Vertical_NeverShowsFalseOverflowBadge()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        settings.VerticalTabStripWidth = 200;
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        AddWidePlainTabs(window, count: 8);

        InvokePopulateTabListMenu(window);
        Dispatcher.UIThread.RunJobs();

        var badge = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(badge);
        Assert.False(badge!.IsVisible, "vertical mode scrolls its own overflow - nothing should ever be reported as hidden");
    }

    // Same pattern as MainWindowTitleBarTests.AddWidePlainTabs: plain TabItems with wide
    // TextBlock headers added directly to the "Tabs" TabControl's Items, bypassing AddTab (which
    // spawns a real PTY per tab). UpdateTabOverflowIndicator only reads TabItem.Bounds.Width and
    // the header ScrollViewer's Bounds.Width, so a real, headless-measured header is all this
    // needs to have historically tripped the (now-guarded) horizontal clipping math.
    private static void AddWidePlainTabs(NovaTerminal.MainWindow window, int count)
    {
        var tabs = window.FindControl<TabControl>("Tabs");
        Assert.NotNull(tabs);

        for (int i = 0; i < count; i++)
        {
            tabs!.Items.Add(new TabItem
            {
                Header = new TextBlock { Text = new string('W', 60) + i }
            });
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void InvokePopulateTabListMenu(NovaTerminal.MainWindow window)
    {
        var method = typeof(NovaTerminal.MainWindow).GetMethod("PopulateTabListMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        // Reflection Invoke fills no optional parameters - the anchor override must be
        // passed explicitly (null = title-bar anchor chain, the pre-pill behavior).
        method!.Invoke(window, new object[] { false, null });
    }

    // Regression coverage for the perf fix: recomputing the preview line on every batched
    // visual refresh is an O(rows*cols) grapheme walk under the buffer's read lock per tab per
    // pass, which gets expensive with several streaming tabs. The fix gates the recompute behind
    // a PreviewDirty flag + a 250ms throttle (PreviewRefreshInterval) - these three tests drive
    // that gate directly via the GetTabPreviewDirtyForTest/SetTabPreviewDirtyForTest seam
    // (mirroring the GetTabStatusTracker seam already used above) since real pane output isn't
    // practical to simulate in the headless test host.

    [AvaloniaFact]
    public void UpdateTabVisuals_PreviewNotDirty_LeavesExistingTextUntouched()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs(); // let the initial dirty-from-mode-switch pass settle

        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();
        var preview = NovaTerminal.MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine");
        Assert.NotNull(preview);

        window.SetTabPreviewDirtyForTest(tab, false);
        preview!.Text = "SENTINEL_UNCHANGED";

        window.UpdateTabVisuals();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("SENTINEL_UNCHANGED", preview.Text);
    }

    [AvaloniaFact]
    public void UpdateTabVisuals_PreviewDirtyPastThrottleWindow_RecomputesAndReplacesSentinel()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs(); // let the initial dirty-from-mode-switch pass settle

        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();
        var preview = NovaTerminal.MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine");
        Assert.NotNull(preview);

        preview!.Text = "SENTINEL_STALE";
        window.SetTabPreviewDirtyForTest(tab, true);
        // Clear the 250ms throttle window left over from the initial settle pass above, so this
        // recompute isn't itself throttled.
        System.Threading.Thread.Sleep(300);

        window.UpdateTabVisuals();
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual("SENTINEL_STALE", preview.Text);
    }

    [AvaloniaFact]
    public void ApplyTabLayout_ModeSwitch_MarksPreviewDirty_SoFirstPassRepopulatesIt()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Horizontal";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();
        // Clear it so the upcoming mode switch is the only possible source of "dirty" below.
        window.SetTabPreviewDirtyForTest(tab, false);
        // Window startup's own initial tab-visuals pass may have armed the 250ms preview
        // throttle - wait it out so the mode switch below isn't itself throttled into
        // leaving the dirty flag set. (Startup uses deterministic default settings via
        // AppServiceBundle.Settings, so that pass is always horizontal now.)
        System.Threading.Thread.Sleep(300);

        settings.TabStripOrientation = "Vertical";
        window.ApplyTabLayout();

        Assert.True(window.GetTabPreviewDirtyForTest(tab),
            "mode switch must mark the preview dirty so the first vertical pass repopulates it");

        Dispatcher.UIThread.RunJobs(); // the deferred UpdateTabVisuals pass consumes the dirty flag

        var preview = NovaTerminal.MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine");
        Assert.NotNull(preview);
        Assert.False(window.GetTabPreviewDirtyForTest(tab), "the first vertical pass should have recomputed and cleared dirty");
    }

    // ---- Drag-to-reorder (MainWindow.WireTabHeaderReorderDrag + Shell/TabDragModel) ----
    //
    // Real pointer-event routing exists in the headless host (unlike the grip-drag tests
    // further up, which had to fake state through a seam): each test drives the actual
    // routed PointerPressed/Moved/Released/KeyDown events through a real Pointer instance
    // with window-space positions translated from the laid-out header bounds.

    /// <summary>Raises left-button press/move/release routed events through one real
    /// pointer, mirroring how the platform input manager would deliver them (window-space
    /// rootVisualPosition; the event args translate on demand via GetPosition).</summary>
    private sealed class PointerDriver
    {
        private readonly Avalonia.Input.Pointer _pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        private ulong _timestamp;

        public IPointer Pointer => _pointer;

        public void Press(Control target, Window window, Point windowPosition)
            => target.RaiseEvent(new PointerPressedEventArgs(
                target, _pointer, window, windowPosition, ++_timestamp,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None));

        public void Move(Control target, Window window, Point windowPosition)
            => target.RaiseEvent(new PointerEventArgs(
                InputElement.PointerMovedEvent, target, _pointer, window, windowPosition, ++_timestamp,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
                KeyModifiers.None));

        public void Release(Control target, Window window, Point windowPosition)
            => target.RaiseEvent(new PointerReleasedEventArgs(
                target, _pointer, window, windowPosition, ++_timestamp,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));
    }

    // Strip (either orientation) with extra plain tabs to reorder. Plain TabItems bypass
    // AddTab (which would spawn a real PTY per tab - same trick as AddWidePlainTabs
    // above); re-applying the layout rebuilds every header through the wired factories,
    // so the added tabs get drag-wired header hosts (vertical rows or plain horizontal
    // headers, per <paramref name="orientation"/>).
    private static NovaTerminal.MainWindow CreateShownStripWindowWithPlainTabs(int plainTabCount, string orientation)
    {
        var window = CreateShownWindow();
        GetSettings(window).TabStripOrientation = orientation;
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs");
        Assert.NotNull(tabs);
        for (int i = 0; i < plainTabCount; i++)
        {
            tabs!.Items.Add(new TabItem
            {
                Header = new TextBlock { Text = $"DragPlain{i}" },
                Content = new Border()
            });
        }

        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static NovaTerminal.MainWindow CreateShownVerticalWindowWithPlainTabs(int plainTabCount)
        => CreateShownStripWindowWithPlainTabs(plainTabCount, "Vertical");

    private static Point HeaderCenterInWindow(Control headerHost, Window window)
    {
        var center = headerHost.TranslatePoint(
            new Point(headerHost.Bounds.Width / 2, headerHost.Bounds.Height / 2), window);
        Assert.NotNull(center); // the header must be attached and laid out for the drag math
        return center!.Value;
    }

    private static Border HeaderHostOf(TabItem tab)
    {
        var host = tab.Header as Border;
        Assert.NotNull(host);
        return host!;
    }

    private static Border? FindInsertIndicator(NovaTerminal.MainWindow window)
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "PART_TabInsertIndicator");

    [AvaloniaFact]
    public void DragReorder_Commit_MovesDraggedTabLast_AndRestoresSelection()
    {
        var window = CreateShownVerticalWindowWithPlainTabs(plainTabCount: 2);
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var before = tabs.Items.Cast<TabItem>().ToList();
        Assert.True(before.Count >= 3, "reorder needs at least three tabs");
        var dragged = before[0];
        var host0 = HeaderHostOf(dragged);
        var host2 = HeaderHostOf(before[2]);

        var driver = new PointerDriver();
        var pressPos = HeaderCenterInWindow(host0, window);
        driver.Press(host0, window, pressPos);

        // Cross the start threshold (>= 5 DIP along the strip axis), still over tab 0.
        driver.Move(host0, window, new Point(pressPos.X, pressPos.Y + 6));
        Assert.True(window.IsTabReorderDraggingForTest, "past-threshold move must begin the drag");
        Assert.Equal(0.5, host0.Opacity, 5);

        // Drop below tab 2's center: insert index = count, so the dragged tab lands last.
        var belowTab2 = new Point(pressPos.X, HeaderCenterInWindow(host2, window).Y + 4);
        driver.Move(host0, window, belowTab2);
        driver.Release(host0, window, belowTab2);

        Assert.False(window.IsTabReorderDraggingForTest);
        Assert.Equal(1, host0.Opacity, 5);

        var after = tabs.Items.Cast<TabItem>().ToList();
        Assert.Equal(before.Count, after.Count);
        // Pointer below tab 2's center -> insert index 3 -> dragged tab (from slot 0) lands
        // at index 2; anything below the drop point (startup-restore tabs included) keeps
        // its relative order.
        var expected = new List<TabItem> { before[1], before[2], dragged };
        expected.AddRange(before.Skip(3));
        Assert.Equal(expected, after);
        Assert.Same(dragged, tabs.SelectedItem);
    }

    // The drag math is axis-generic (TabDragModel has no orientation concept), but the
    // commit path above only exercised it vertically. This mirrors it along X through a
    // real horizontal strip (orientation via _settings.TabStripOrientation +
    // ApplyTabLayout, headers rebuilt through the wired horizontal factory) so the
    // horizontal wiring - X axis selection, Height-side indicator thickness - is pinned
    // end-to-end too.
    [AvaloniaFact]
    public void DragReorder_Commit_HorizontalStrip_ReordersAlongX_AndRestoresSelection()
    {
        var window = CreateShownStripWindowWithPlainTabs(plainTabCount: 2, orientation: "Horizontal");
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var before = tabs.Items.Cast<TabItem>().ToList();
        Assert.True(before.Count >= 3, "reorder needs at least three tabs");
        var dragged = before[0];
        var host0 = HeaderHostOf(dragged);
        var host2 = HeaderHostOf(before[2]);

        var driver = new PointerDriver();
        var pressPos = HeaderCenterInWindow(host0, window);
        driver.Press(host0, window, pressPos);

        // Cross the start threshold (>= 5 DIP along the strip axis - X here), still over tab 0.
        driver.Move(host0, window, new Point(pressPos.X + 6, pressPos.Y));
        Assert.True(window.IsTabReorderDraggingForTest, "past-threshold move must begin the drag");
        Assert.Equal(0.5, host0.Opacity, 5);

        // Drop past tab 2's center: insert index = count, so the dragged tab lands last.
        var pastTab2 = new Point(HeaderCenterInWindow(host2, window).X + 4, pressPos.Y);
        driver.Move(host0, window, pastTab2);
        driver.Release(host0, window, pastTab2);

        Assert.False(window.IsTabReorderDraggingForTest);
        Assert.Equal(1, host0.Opacity, 5);

        var after = tabs.Items.Cast<TabItem>().ToList();
        Assert.Equal(before.Count, after.Count);
        var expected = new List<TabItem> { before[1], before[2], dragged };
        expected.AddRange(before.Skip(3));
        Assert.Equal(expected, after);
        Assert.Same(dragged, tabs.SelectedItem);
    }

    [AvaloniaFact]
    public void DragReorder_SubThresholdMove_IsAPlainClick_NoDragNoReorder()
    {
        var window = CreateShownVerticalWindowWithPlainTabs(plainTabCount: 2);
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var before = tabs.Items.Cast<TabItem>().ToList();
        var dragged = before[0];
        var host0 = HeaderHostOf(dragged);

        var driver = new PointerDriver();
        var pressPos = HeaderCenterInWindow(host0, window);
        driver.Press(host0, window, pressPos);

        // 2 DIP is under the 5 DIP start threshold.
        driver.Move(host0, window, new Point(pressPos.X, pressPos.Y + 2));
        Assert.False(window.IsTabReorderDraggingForTest);
        Assert.Equal(1, host0.Opacity, 5);
        Assert.False(FindInsertIndicator(window)!.IsVisible);

        driver.Release(host0, window, pressPos);

        Assert.False(window.IsTabReorderDraggingForTest);
        Assert.Equal(before, tabs.Items.Cast<TabItem>().ToList());
        Assert.Same(dragged, tabs.SelectedItem);
    }

    [AvaloniaFact]
    public void DragReorder_Indicator_VisibleMidDrag_HiddenAfterRelease()
    {
        var window = CreateShownVerticalWindowWithPlainTabs(plainTabCount: 2);
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var before = tabs.Items.Cast<TabItem>().ToList();
        var indicator = FindInsertIndicator(window);
        Assert.NotNull(indicator);
        Assert.False(indicator!.IsVisible);

        // Drag tab 1 (the middle tab) just past its own center so the drop lands back in
        // its own slot - the indicator must appear mid-drag even when no reorder results.
        var dragged = before[1];
        var host1 = HeaderHostOf(dragged);
        var pressPos = HeaderCenterInWindow(host1, window);

        var driver = new PointerDriver();
        driver.Press(host1, window, pressPos);
        driver.Move(host1, window, new Point(pressPos.X, pressPos.Y + 6));

        Assert.True(window.IsTabReorderDraggingForTest);
        Assert.True(indicator.IsVisible, "insert indicator must be visible mid-drag");

        driver.Release(host1, window, new Point(pressPos.X, pressPos.Y + 6));

        Assert.False(indicator.IsVisible, "insert indicator must hide after release");
        Assert.False(window.IsTabReorderDraggingForTest);
        Assert.Equal(before, tabs.Items.Cast<TabItem>().ToList());
    }

    [AvaloniaFact]
    public void DragReorder_Escape_CancelsWithoutReorder_AndLaterReleaseIsHarmless()
    {
        var window = CreateShownVerticalWindowWithPlainTabs(plainTabCount: 2);
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var before = tabs.Items.Cast<TabItem>().ToList();
        var dragged = before[0];
        var host0 = HeaderHostOf(dragged);
        var indicator = FindInsertIndicator(window)!;

        var driver = new PointerDriver();
        var pressPos = HeaderCenterInWindow(host0, window);
        driver.Press(host0, window, pressPos);
        driver.Move(host0, window, new Point(pressPos.X, pressPos.Y + 6));
        Assert.True(window.IsTabReorderDraggingForTest);

        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Escape,
            Source = window
        });

        Assert.False(window.IsTabReorderDraggingForTest, "Escape must cancel the drag");
        Assert.False(indicator.IsVisible);
        Assert.Equal(1, host0.Opacity, 5);
        Assert.Equal(before, tabs.Items.Cast<TabItem>().ToList());

        // A stray release (and the capture cleanup that already ran) must be a no-op.
        driver.Release(host0, window, new Point(pressPos.X, pressPos.Y + 6));
        Assert.Equal(before, tabs.Items.Cast<TabItem>().ToList());
        Assert.Same(dragged, tabs.SelectedItem);
    }

    [AvaloniaFact]
    public void DragReorder_DuringGripDrag_HeaderPressMustNotStartReorder()
    {
        var window = CreateShownVerticalWindowWithPlainTabs(plainTabCount: 2);
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var before = tabs.Items.Cast<TabItem>().ToList();
        var host0 = HeaderHostOf(before[0]);

        // Mid-grip-drag state (the grip owns the pointer): a header press + threshold move
        // arriving meanwhile must be ignored, not turned into a second concurrent drag.
        window.IsTabStripGripDraggingForTest = true;
        try
        {
            var driver = new PointerDriver();
            var pressPos = HeaderCenterInWindow(host0, window);
            driver.Press(host0, window, pressPos);
            driver.Move(host0, window, new Point(pressPos.X, pressPos.Y + 6));

            Assert.False(window.IsTabReorderDraggingForTest);
            Assert.False(FindInsertIndicator(window)!.IsVisible);
            driver.Release(host0, window, pressPos);
        }
        finally
        {
            window.IsTabStripGripDraggingForTest = false;
        }

        Assert.Equal(before, tabs.Items.Cast<TabItem>().ToList());
    }

    // ---- Keyboard move-tab (MoveSelectedTab, sharing the drag commit path) ----
    //
    // MoveSelectedTab commits through the same ReorderTabInItems primitive as
    // CommitTabReorder, so these pin the keyboard-specific half: clamped ±1 movement,
    // selection restoration on the moved tab, and no MRU churn beyond the selection's own
    // TouchTabMru (which is a no-op re-insert when the moved tab is already the MRU head).

    private static List<TabItem> GetTabMru(NovaTerminal.MainWindow window)
        => ((List<TabItem>)typeof(NovaTerminal.MainWindow)
            .GetField("_tabMru", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!).ToList();

    /// <summary>Puts the strip in a deterministic selection state: two real selection
    /// changes so MRU head is provably <paramref name="selected"/> before the move.</summary>
    private static void SelectViaRealEvents(TabControl tabs, TabItem selected)
    {
        var other = tabs.Items.Cast<TabItem>().First(t => t != selected);
        tabs.SelectedItem = other;
        tabs.SelectedItem = selected;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void MoveSelectedTab_PlusOne_SwapsWithNext_KeepsSelection_MruUntouched()
    {
        var window = CreateShownVerticalWindowWithPlainTabs(plainTabCount: 2);
        try
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var before = tabs.Items.Cast<TabItem>().ToList();
            Assert.True(before.Count >= 3, "move needs at least three tabs");

            SelectViaRealEvents(tabs, before[0]);
            var mruBefore = GetTabMru(window);

            window.MoveSelectedTab(+1);

            var after = tabs.Items.Cast<TabItem>().ToList();
            Assert.Equal(before.Count, after.Count);
            Assert.Equal(new[] { before[1], before[0] }.Concat(before.Skip(2)).ToList(), after);
            Assert.Same(before[0], tabs.SelectedItem);
            Assert.Equal(mruBefore, GetTabMru(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MoveSelectedTab_MinusOneAtTopIndex_IsNoOp()
    {
        var window = CreateShownVerticalWindowWithPlainTabs(plainTabCount: 2);
        try
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var before = tabs.Items.Cast<TabItem>().ToList();
            SelectViaRealEvents(tabs, before[0]);
            var mruBefore = GetTabMru(window);

            window.MoveSelectedTab(-1);

            Assert.Equal(before, tabs.Items.Cast<TabItem>().ToList());
            Assert.Same(before[0], tabs.SelectedItem);
            Assert.Equal(mruBefore, GetTabMru(window));
        }
        finally
        {
            window.Close();
        }
    }

    // Same tunneled-KeyDown path the Escape-cancel drag test uses: raising KeyDown on the
    // window reaches the tunneled shortcut handler, which must recognize the catalogued
    // Ctrl+Shift+PageDown chord and route it to MoveSelectedTab.
    [AvaloniaFact]
    public void KeyDown_CtrlShiftPageDown_MovesSelectedTabDown()
    {
        var window = CreateShownVerticalWindowWithPlainTabs(plainTabCount: 2);
        try
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var before = tabs.Items.Cast<TabItem>().ToList();
            Assert.True(before.Count >= 3, "move needs at least three tabs");
            SelectViaRealEvents(tabs, before[0]);

            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.PageDown,
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
                Source = window
            });
            Dispatcher.UIThread.RunJobs();

            var after = tabs.Items.Cast<TabItem>().ToList();
            Assert.Equal(new[] { before[1], before[0] }.Concat(before.Skip(2)).ToList(), after);
            Assert.Same(before[0], tabs.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- Vertical overflow pill (PART_TabOverflowPill) ----

    private static Button? FindOverflowPill(NovaTerminal.MainWindow window)
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Button>()
            .FirstOrDefault(b => b.Name == "PART_TabOverflowPill");

    /// <summary>The pill's text part. Read from Content rather than the visual tree for
    /// the same reason production does: while the pill is hidden its own template has
    /// never been applied, so the TextBlock has no visual-tree presence yet.</summary>
    private static TextBlock? FindOverflowPillText(Button pill)
        => pill.Content as TextBlock;

    /// <summary>Small window (height set before Show so the first layout already measures
    /// against it) plus enough vertical rows to overflow the sidebar viewport. Plain
    /// TabItems bypass AddTab (which would spawn a real PTY per tab - same trick as
    /// AddWidePlainTabs); re-applying the layout rebuilds their headers as vertical
    /// rows.</summary>
    private static NovaTerminal.MainWindow CreateShownVerticalWindowWithOverflow(
        int plainTabCount, double height)
    {
        var window = TestMainWindowFactory.Create();
        window.Height = height;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        settings.VerticalTabStripWidth = 200;
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs");
        Assert.NotNull(tabs);
        for (int i = 0; i < plainTabCount; i++)
        {
            tabs!.Items.Add(new TabItem
            {
                Header = new TextBlock { Text = $"OverflowRow{i}" },
                Content = new Border()
            });
        }
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void OverflowPill_VerticalWithHiddenTabs_ShowsCount_HorizontalHides()
    {
        var window = CreateShownVerticalWindowWithOverflow(plainTabCount: 6, height: 260);
        try
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            // Second visual pass after the layout settle above: the pill update reads
            // Bounds, which only reflect the added rows after a layout pass has run.
            window.UpdateTabVisuals();
            Dispatcher.UIThread.RunJobs();

            var pill = FindOverflowPill(window);
            Assert.NotNull(pill);
            var pillText = FindOverflowPillText(pill!);
            Assert.NotNull(pillText);
            // XAML part-name contract.
            Assert.Equal("PART_TabOverflowPillText", pillText!.Name);

            // The expected count comes from the same pure math the production update
            // uses, so the assertion pins wiring (visible + text), not arithmetic.
            var scrollViewer = InvokeFindTabHeaderScrollViewer(window)!;
            int expected = NovaTerminal.MainWindow.CountHiddenTabs(
                scrollViewer.Bounds.Height,
                tabs.Items.Cast<TabItem>().Select(t => t.Bounds.Height),
                44);
            Assert.True(expected > 0, "test premise: a 260px viewport cannot fit 7+ 44px rows");

            Assert.True(pill!.IsVisible);
            Assert.Equal($"+{expected} more", pillText!.Text);

            // Horizontal mode: the title-bar TabOverflowBadge owns the affordance - the
            // pill must hide on the mode flip's viewport pass.
            GetSettings(window).TabStripOrientation = "Horizontal";
            window.ApplyTabLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.False(pill.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void OverflowPill_VerticalWithoutOverflow_StaysHidden()
    {
        var window = CreateShownWindow();
        try
        {
            GetSettings(window).TabStripOrientation = "Vertical";
            window.ApplyTabLayout();
            Dispatcher.UIThread.RunJobs();

            // The deterministic startup bundle creates dozens of tabs, so "no overflow"
            // must be reached by enlarging the viewport, not by assuming a small strip:
            // size the window to fit every actual row (with headroom) and let layout
            // settle before the asserting pass.
            var tabs = window.FindControl<TabControl>("Tabs")!;
            int rowCount = tabs.Items.Count;
            Assert.True(rowCount > 0);
            window.Height = rowCount * 60 + 120;
            Dispatcher.UIThread.RunJobs();
            window.UpdateTabVisuals();
            Dispatcher.UIThread.RunJobs();

            var pill = FindOverflowPill(window);
            Assert.NotNull(pill);
            Assert.False(pill!.IsVisible, "a viewport that fits every row must show no pill");
        }
        finally
        {
            window.Close();
        }
    }

    // Click wiring: the pill's Click opens the tab-list menu at the pill through
    // PopulateTabListMenu's anchorOverride path, into the pill-dedicated flyout. Driving
    // the routed ClickEvent directly stands in for a real pointer click (the same
    // substitution the headless host makes for the drag tests' raw pointer events).
    [AvaloniaFact]
    public void OverflowPill_Click_OpensTabListMenuAtPill()
    {
        var window = CreateShownVerticalWindowWithOverflow(plainTabCount: 2, height: 400);
        try
        {
            var pill = FindOverflowPill(window);
            Assert.NotNull(pill);

            pill!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(
                Avalonia.Controls.Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var flyout = (MenuFlyout?)typeof(NovaTerminal.MainWindow)
                .GetField("_tabOverflowPillFlyout", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window);
            Assert.NotNull(flyout);
            Assert.True(flyout!.IsOpen, "click must open the tab-list flyout");
            Assert.NotEmpty(flyout.Items);
        }
        finally
        {
            window.Close();
        }
    }
}
