using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.AgentHost;

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
            isAgentActable: false, paneTabId: SelectedTab, selectedTabId: SelectedTab));
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: false, paneTabId: OtherTab, selectedTabId: SelectedTab));
    }

    [Fact]
    public void An_actable_pane_in_the_selected_tab_is_marked()
    {
        // Its segment is on screen and says "agent reading" itself; lighting
        // the window too would double-report one event.
        Assert.False(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: true, paneTabId: SelectedTab, selectedTabId: SelectedTab));
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
            isAgentActable: true, paneTabId: OtherTab, selectedTabId: SelectedTab));
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
            isAgentActable: true, paneTabId: null, selectedTabId: SelectedTab));
    }

    [Fact]
    public void With_no_tab_selected_every_actable_pane_is_unmarked()
    {
        // No selected tab means no tab content is rendered, so no segment is
        // visible anywhere.
        Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
            isAgentActable: true, paneTabId: OtherTab, selectedTabId: null));
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
                selectedTabId: window.GetPersistentTabId(firstTab)));

            // Select a different tab and the same pane becomes unmarked: its
            // segment is now in unrendered tab content.
            var secondTab = AddBareTab(window, "second");
            tabs.SelectedItem = secondTab;

            Assert.True(MainWindow.IsPaneReadInvisibleWithoutWindowLight(
                isAgentActable: true,
                paneTabId: registration.TabId,
                selectedTabId: window.GetPersistentTabId(secondTab)));
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
}
