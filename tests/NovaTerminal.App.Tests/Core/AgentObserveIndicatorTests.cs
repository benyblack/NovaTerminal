using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.AgentHost;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using NovaTerminal.Shell.TitleBar;
// Aliased, not a plain namespace using: Avalonia.Controls.Shapes also exports a Path type that
// would collide with System.IO.Path, which this file uses for its NOVATERM_APPDATA_ROOT scratch dirs.
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// The window-level agent light. It is a permission indicator first — visible
/// exactly while observe is enabled — with two activity states layered on,
/// because it is the only surface at the right scope for them: a waitForEvents
/// long poll names no pane, and a read landing on a pane that carries no agent
/// segment of its own would otherwise be invisible everywhere.
/// </summary>
public class AgentObserveIndicatorTests
{
    [Theory]
    // Observe off: invisible, and nothing else matters.
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, true, false, false)]
    // Observe on, nothing happening: visible but quiet.
    [InlineData(true, false, false, true, false)]
    // A long poll is parked: active, because the subscription names no pane and
    // this is the only surface for it.
    [InlineData(true, true, false, true, true)]
    // A pane with no bar of its own is being read: active. This is the only
    // place that read can appear.
    [InlineData(true, false, true, true, true)]
    // Both at once: still just active.
    [InlineData(true, true, true, true, true)]
    public void Observe_indicator_state(
        bool observeRunning, bool polling, bool anyUnmarkedPaneWatched,
        bool expectedVisible, bool expectedActive)
    {
        var (visible, active) = MainWindow.ComputeObserveIndicatorState(
            observeRunning, polling, anyUnmarkedPaneWatched);

        Assert.Equal(expectedVisible, visible);
        Assert.Equal(expectedActive, active);
    }

    [Fact]
    public void A_read_of_an_unmarked_pane_lights_the_indicator_even_with_act_on()
    {
        // The bug this pins: the condition used to be "act is off", justified by
        // "with act off, no pane carries a bar". But act being *on* does not put
        // a bar on every pane — an SSH pane whose profile lacks AllowAgentAccess
        // is not actable, so it has no bar, no tab glyph under the default
        // WritesOnly rollup, and previously no window light either. Reading such
        // a pane produced no live signal anywhere, and those are precisely the
        // panes the user deliberately excluded from act.
        //
        // The act toggle is no longer an input at all, which is the fix: the
        // decision is about the *watched pane*, not the global permission.
        var (visible, active) = MainWindow.ComputeObserveIndicatorState(
            observeRunning: true, polling: false, anyUnmarkedPaneWatched: true);

        Assert.True(visible);
        Assert.True(active);
    }

    [Fact]
    public void A_read_of_a_pane_that_carries_its_own_bar_does_not_light_the_indicator()
    {
        // The other half: an actable pane already shows "agent reading" on its
        // own status bar, so lighting the window light too would double-report.
        var (visible, active) = MainWindow.ComputeObserveIndicatorState(
            observeRunning: true, polling: false, anyUnmarkedPaneWatched: false);

        Assert.True(visible);
        Assert.False(active);
    }

    // ---- which panes count as "unmarked" -------------------------------
    //
    // "Unmarked" is not "has no segment", it is "has no segment the user can
    // currently *see*". A non-selected tab's content is unrendered, so an
    // actable pane parked there shows its segment to nobody.

    private static readonly Guid SelectedTab = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTab = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void A_non_actable_pane_is_unmarked_wherever_it_lives()
    {
        // No act permission, no segment at all — the tab it sits in is beside
        // the point.
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: false, paneTabId: SelectedTab, selectedTabId: SelectedTab,
            paneHiddenByZoom: false));
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: false, paneTabId: OtherTab, selectedTabId: SelectedTab,
            paneHiddenByZoom: false));
    }

    [Fact]
    public void An_actable_pane_in_the_selected_tab_is_marked()
    {
        // Its segment is on screen and says "agent reading" itself; lighting
        // the window too would double-report one event.
        Assert.False(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: true, paneTabId: SelectedTab, selectedTabId: SelectedTab,
            paneHiddenByZoom: false));
    }

    [Fact]
    public void An_actable_pane_in_a_non_selected_tab_is_unmarked()
    {
        // The hole this pins, and it is the common case rather than an edge
        // one: act on and more than one tab is enough to reach it. The pane has
        // a segment, but the tab is not selected so its content is unrendered
        // and the segment is off screen. The tab glyph is suppressed for reads
        // under the default WritesOnly rollup, and the Watched tier decays in
        // ~3 s — so without the window light, an agent read of that pane
        // appeared nowhere, then and afterwards.
        //
        // Note the direction of the old failure: the *more* permission a pane
        // was granted, the *less* visible reading it became.
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: true, paneTabId: OtherTab, selectedTabId: SelectedTab,
            paneHiddenByZoom: false));
    }

    [Fact]
    public void An_actable_pane_with_no_tab_association_yet_is_unmarked()
    {
        // A registration is created in TerminalPane.SetupCommon and only
        // associated with a tab afterwards by MainWindow, so TabId is briefly
        // null. Unassociated means "cannot be proven on screen", and this light
        // exists so that no read is silent — so the unprovable case reports.
        // Worst case that is one redundant light; the other choice risks a
        // silent read.
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: true, paneTabId: null, selectedTabId: SelectedTab,
            paneHiddenByZoom: false));
    }

    [Fact]
    public void An_actable_pane_zoomed_away_inside_the_selected_tab_is_unmarked()
    {
        // Pane zoom breaks the equivalence the tab test rests on. A tab holding
        // two split panes, both actable; the user zooms pane A. EnterPaneZoom
        // replaces the tab content with A alone, so B is not rendered - yet B
        // still carries the selected tab id, so on the tab test alone it looked
        // "marked". An agent read of B then appeared nowhere: no bar (B is not
        // rendered), no tab glyph (reads reach the tab strip only under the
        // non-default "All" rollup), no window light. The Watched tier decays
        // in ~3 s, so un-zooming a moment later showed nothing either.
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: true, paneTabId: SelectedTab, selectedTabId: SelectedTab,
            paneHiddenByZoom: true));
    }

    [Fact]
    public void The_zoomed_pane_itself_stays_marked()
    {
        // The half that must not regress. The zoomed pane is the one thing on
        // screen, so its segment says "agent reading" itself; lighting the
        // window as well would double-report the same read.
        Assert.False(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: true, paneTabId: SelectedTab, selectedTabId: SelectedTab,
            paneHiddenByZoom: false));
    }

    [Fact]
    public void Zoom_cannot_rescue_a_pane_that_is_invisible_for_another_reason()
    {
        // paneHiddenByZoom is the last word only for a pane that got past the
        // earlier gates. A non-actable pane, an unassociated one, or one in an
        // unselected tab stays unmarked no matter what the zoom flag says -
        // the flag can only ever take visibility away, never grant it.
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: false, paneTabId: SelectedTab, selectedTabId: SelectedTab,
            paneHiddenByZoom: false));
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: true, paneTabId: null, selectedTabId: SelectedTab,
            paneHiddenByZoom: false));
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: true, paneTabId: OtherTab, selectedTabId: SelectedTab,
            paneHiddenByZoom: false));
    }

    [Fact]
    public void With_no_tab_selected_every_actable_pane_is_unmarked()
    {
        // No selected tab means no tab content is rendered, so no segment is
        // visible anywhere.
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: true, paneTabId: OtherTab, selectedTabId: null,
            paneHiddenByZoom: false));
    }

    [AvaloniaFact]
    public void A_panes_stored_tab_id_and_the_selected_tab_id_are_the_same_id_space()
    {
        // The predicate above compares a registration's TabId against the
        // selected tab's persistent id. That comparison is only meaningful if
        // both come from the same source — MainWindow associates panes via
        // SetTabAssociation(pane, GetPersistentTabId(tab)), and
        // RefreshAgentObserveIndicator resolves the selected tab the same way.
        // If those ever drifted apart the predicate would silently answer
        // "unmarked" for every pane forever, and nothing above would notice.
        RunIsolated(window =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var firstTab = tabs.Items.Cast<TabItem>().First();
            var registration = Assert.Single(
                AgentSessionRegistry.Instance.GetRegistrations()
                    .Where(r => r.TabId == window.GetPersistentTabId(firstTab)));

            // Selected tab: the pane's segment is on screen, so it is marked.
            Assert.Equal(tabs.SelectedItem, firstTab);
            Assert.False(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
                isAgentActable: true,
                paneTabId: registration.TabId,
                selectedTabId: window.GetPersistentTabId(firstTab),
                paneHiddenByZoom: false));

            // Select a different tab and the same pane becomes unmarked: its
            // segment is now in unrendered tab content.
            var secondTab = AddBareTab(window, "second");
            tabs.SelectedItem = secondTab;

            Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
                isAgentActable: true,
                paneTabId: registration.TabId,
                selectedTabId: window.GetPersistentTabId(secondTab),
                paneHiddenByZoom: false));
        });
    }

    [AvaloniaFact]
    public void Switching_tabs_refreshes_the_indicator()
    {
        // A tab switch changes the light's answer with no attention event
        // behind it, so SelectionChanged has to re-run the refresh or the light
        // lags the switch: the pane whose segment just came on screen keeps
        // double-reporting, and a pane still being read in the tab just left
        // never starts.
        //
        // Observed via the one write RefreshAgentObserveIndicator always makes:
        // it assigns indicator.IsVisible from AgentHostService.Instance.IsRunning,
        // which is false in this isolated window. Forcing the control visible
        // and watching it snap back is proof the refresh ran.
        RunIsolated(window =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var indicator = window.FindControl<Button>("AgentObserveIndicator")!;
            var secondTab = AddBareTab(window, "second");

            indicator.IsVisible = true;
            tabs.SelectedItem = secondTab;

            Assert.False(indicator.IsVisible);
        });
    }

    /// <summary>
    /// TestMainWindowFactory.Create() runs the real MainWindow constructor,
    /// which loads the on-disk settings.json and calls
    /// AgentHostService.Instance.Apply(...). On a machine with observe
    /// persisted as enabled that would start a real named-pipe/Unix-socket
    /// accept loop inside this shared test process. Point
    /// NOVATERM_APPDATA_ROOT at a fresh empty directory so Load() always yields
    /// defaults and Apply() takes its no-op Stop() path. Same pattern as
    /// AgentIndicatorTabRollupTests.RunIsolated.
    /// </summary>
    private static void RunIsolated(Action<MainWindow> body)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"novaterm_observe_indicator_test_{Guid.NewGuid():N}");
        string? previousRoot = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        Directory.CreateDirectory(tempRoot);

        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", tempRoot);

            var window = TestMainWindowFactory.Create();
            window.Show();
            body(window);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previousRoot);
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [AvaloniaFact]
    public void Zooming_one_split_pane_unmarks_its_siblings_and_leaves_the_zoomed_one_marked()
    {
        // The wiring, not the rule. The pure cases above would pass unchanged
        // if RefreshAgentObserveIndicator never looked at the zoom maps at all,
        // so this drives the real thing: a real split tab, the real
        // EnterPaneZoom the zoom command calls, and the two real registrations
        // the panes created for themselves.
        //
        // It stops at the visibility decision rather than at the lit indicator
        // because the indicator is gated on AgentHostService.Instance.IsRunning,
        // and turning that on would start a real IPC endpoint inside the shared
        // test process. The tier half of the light is covered by the theory at
        // the top of this file.
        RunIsolated(window =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var splitTab = AddSplitTab(window, out var zoomedPane, out var hiddenPane);
            tabs.SelectedItem = splitTab;

            var zoomedReg = RegistrationFor(zoomedPane);
            var hiddenReg = RegistrationFor(hiddenPane);
            zoomedReg.IsAgentActable = true;
            hiddenReg.IsAgentActable = true;

            // Un-zoomed: both panes are rendered side by side, both carry a
            // visible segment, so neither needs the window light.
            Assert.False(window.IsPaneReadInvisibleWithoutWindowLight(zoomedReg));
            Assert.False(window.IsPaneReadInvisibleWithoutWindowLight(hiddenReg));

            EnterZoom(window, splitTab, zoomedPane);

            // The zoomed pane is the only thing on screen: still marked, so the
            // light does not double-report its reads.
            Assert.False(window.IsPaneReadInvisibleWithoutWindowLight(zoomedReg));
            // Its sibling is gone from the screen while still sitting in the
            // selected tab. This is the case the tab test alone got wrong.
            Assert.True(window.IsPaneReadInvisibleWithoutWindowLight(hiddenReg));
        });
    }

    /// <summary>
    /// Builds a tab holding a real two-pane split — the shape
    /// <c>SessionManager.CreateRestoredTabItem</c> produces from a
    /// <c>NodeType.Split</c> root — and runs it through the production
    /// <c>InitializeRestoredTabs</c> so the panes are wired to the window and
    /// their registrations are associated with the tab.
    /// </summary>
    private static TabItem AddSplitTab(MainWindow window, out TerminalPane first, out TerminalPane second)
    {
        var settings = (TerminalSettings)typeof(MainWindow)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

        var session = new TabSession
        {
            Title = "Split",
            Root = new PaneNode
            {
                Type = NodeType.Split,
                SplitOrientation = 0,
                Children =
                {
                    new PaneNode { Type = NodeType.Leaf, Command = "cmd.exe", Arguments = string.Empty, PaneId = Guid.NewGuid().ToString() },
                    new PaneNode { Type = NodeType.Leaf, Command = "cmd.exe", Arguments = string.Empty, PaneId = Guid.NewGuid().ToString() }
                },
                Sizes = { "1*", "1*" }
            }
        };

        var tab = SessionManager.CreateRestoredTabItem(session, settings)!;
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.Items.Add(tab);

        typeof(MainWindow)
            .GetMethod("InitializeRestoredTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tabs });

        // RestorePaneTree builds [child0, GridSplitter, child1]; OfType filters
        // the splitter out and preserves order.
        var panes = ((Grid)tab.Content!).Children.OfType<TerminalPane>().ToList();
        Assert.Equal(2, panes.Count);
        first = panes[0];
        second = panes[1];
        return tab;
    }

    [AvaloniaFact]
    public void Entering_and_leaving_pane_zoom_refreshes_the_indicator()
    {
        // The predicate consults the zoom maps, but consulting them is worth
        // nothing if nothing re-runs it: EnterPaneZoom and ExitPaneZoom change
        // which panes are on screen with no attention event behind them, the
        // same way a tab switch does. Without a refresh here, a sibling read
        // while it is zoomed away has its segment hidden and the light never
        // recomputed — so it stays dark, the tab glyph is suppressed under the
        // default WritesOnly rollup, and the rest of that read appears nowhere.
        //
        // Observed the same way Switching_tabs_refreshes_the_indicator observes
        // it: RefreshAgentObserveIndicator always assigns indicator.IsVisible
        // from AgentHostService.Instance.IsRunning, which is false in this
        // isolated window, so forcing the control visible and watching it snap
        // back is proof the refresh ran.
        RunIsolated(window =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var indicator = window.FindControl<Button>("AgentObserveIndicator")!;
            var splitTab = AddSplitTab(window, out var zoomedPane, out _);
            tabs.SelectedItem = splitTab;

            indicator.IsVisible = true;
            EnterZoom(window, splitTab, zoomedPane);
            Assert.False(indicator.IsVisible);

            indicator.IsVisible = true;
            ExitZoom(window, splitTab);
            Assert.False(indicator.IsVisible);
        });
    }

    /// <summary>Drives the production un-zoom path the zoom command uses.</summary>
    private static void ExitZoom(MainWindow window, TabItem tab)
    {
        var exited = (bool)typeof(MainWindow)
            .GetMethod("ExitPaneZoom", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tab, true })!;
        Assert.True(exited, "Test setup failed: ExitPaneZoom refused to un-zoom the tab.");
    }

    /// <summary>Drives the production zoom path the zoom command uses.</summary>
    private static void EnterZoom(MainWindow window, TabItem tab, TerminalPane pane)
    {
        var entered = (bool)typeof(MainWindow)
            .GetMethod("EnterPaneZoom", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tab, pane, true })!;
        Assert.True(entered, "Test setup failed: EnterPaneZoom refused to zoom the pane.");
    }

    private static AgentSessionRegistration RegistrationFor(TerminalPane pane)
        => Assert.Single(AgentSessionRegistry.Instance.GetRegistrations().Where(r => r.PaneId == pane.PaneId));

    private static TabItem AddBareTab(MainWindow window, string title)
    {
        var tab = new TabItem();
        typeof(MainWindow)
            .GetMethod("ConfigureTabHeader", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tab, title });

        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.Items.Add(tab);
        return tab;
    }

    [AvaloniaFact]
    public void The_indicator_control_exists_and_starts_hidden()
    {
        // Wiring only: the decision itself is covered by the theory above, and
        // this must not touch AgentHostService.Instance directly. But
        // TestMainWindowFactory.Create() runs the real MainWindow constructor,
        // which loads the real on-disk settings.json and then calls
        // AgentHostService.Instance.Apply(settings.AgentAccessObserveEnabled) -
        // transitively reaching the exact singleton this test must avoid. On a
        // machine where that setting is persisted as enabled (e.g. anyone who
        // has exercised this feature for real), Apply(true) would start a real
        // named-pipe/Unix-socket accept loop inside this shared test process.
        // Point NOVATERM_APPDATA_ROOT at a fresh, empty scratch directory for
        // the duration of the test so TerminalSettings.Load() always yields
        // defaults (observe disabled) and Apply() takes its no-op Stop() path,
        // regardless of what is persisted on the machine running the test.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"novaterm_observe_indicator_test_{Guid.NewGuid():N}");
        string? previousRoot = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        Directory.CreateDirectory(tempRoot);

        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", tempRoot);

            var window = TestMainWindowFactory.Create();
            window.Show();

            var indicator = window.FindControl<Button>("AgentObserveIndicator");

            Assert.NotNull(indicator);
            Assert.False(indicator!.IsVisible);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previousRoot);
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }
    /// <summary>
    /// PR #342 turned the title bar into a generated surface: RebuildTitleBar calls
    /// TitleBarViewFactory.Populate, which unconditionally clears TitleBarItemsHost. The indicator
    /// is locked into that bar the same way BtnNewTab is - MainWindow.PlaceAgentObserveIndicator
    /// re-inserts the SAME instance instead of building a new one - and that instance identity is
    /// the whole load-bearing property here. It is what keeps the Click handler wired once in the
    /// constructor alive (a rebuilt button would silently lose it, exactly the hazard main's
    /// RebuildTitleBar_TheNewTabButton_SurvivesRebuildsAsTheSameInstance guards for the + button),
    /// and it is what keeps RefreshAgentObserveIndicator's two FindControl lookups resolving - if
    /// either returned null the light would stop updating with no error anywhere.
    /// </summary>
    [AvaloniaFact]
    public void The_indicator_and_its_dot_survive_title_bar_rebuilds_as_the_same_instances()
    {
        RunIsolatedWindow(window =>
        {
            var indicatorBefore = window.FindControl<Button>("AgentObserveIndicator");
            var dotBefore = window.FindControl<Ellipse>("AgentObserveIndicatorDot");
            Assert.NotNull(indicatorBefore);
            Assert.NotNull(dotBefore);

            InvokeRebuildTitleBar(window);
            InvokeRebuildTitleBar(window);

            Assert.Same(indicatorBefore, window.FindControl<Button>("AgentObserveIndicator"));
            Assert.Same(dotBefore, window.FindControl<Ellipse>("AgentObserveIndicatorDot"));

            // The refresh path is what goes silently dead if those lookups stop resolving, so drive
            // it for real rather than trusting the two Assert.Same above by themselves.
            window.RefreshAgentObserveIndicator();
            Assert.False(indicatorBefore!.IsVisible);
        });
    }

    /// <summary>
    /// The design property that decided the merge: there is no way to silence this indicator. It is
    /// deliberately NOT a TitleBarCatalog entry, because catalog entries are exactly what the
    /// Customize Title Bar UI lets the user set to Overflow or Hidden. Hiding every catalog action
    /// there is must still leave the light in the bar - and last in it, where no layout moves it.
    /// </summary>
    [AvaloniaFact]
    public void No_title_bar_layout_can_remove_or_move_the_indicator()
    {
        RunIsolatedWindow(window =>
        {
            var indicator = window.FindControl<Button>("AgentObserveIndicator");
            Assert.NotNull(indicator);

            var settings = (TerminalSettings)typeof(MainWindow)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;

            foreach (var entry in TitleBarCatalog.GetEntries())
            {
                settings.TitleBarItems[entry.Id] = "Hidden";
            }

            InvokeRebuildTitleBar(window);

            var host = window.FindControl<StackPanel>("TitleBarItemsHost");
            Assert.NotNull(host);
            Assert.Same(indicator, host!.Children[^1]);
        });
    }

    private static void InvokeRebuildTitleBar(MainWindow window)
        => typeof(MainWindow)
            .GetMethod("RebuildTitleBar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

    /// <summary>
    /// The same isolation the two tests above use, factored out: the real MainWindow constructor
    /// loads the real on-disk settings.json and calls
    /// AgentHostService.Instance.Apply(settings.AgentAccessObserveEnabled), which on a machine with
    /// observe persisted as enabled would start a real named-pipe/Unix-socket accept loop inside
    /// this shared test process. A fresh empty NOVATERM_APPDATA_ROOT forces defaults.
    /// </summary>
    private static void RunIsolatedWindow(Action<MainWindow> body)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"novaterm_observe_indicator_test_{Guid.NewGuid():N}");
        string? previousRoot = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        Directory.CreateDirectory(tempRoot);

        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", tempRoot);
            body(TestMainWindowFactory.Create());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previousRoot);
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
