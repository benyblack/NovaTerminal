using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using NovaTerminal.Tests.Infra;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// #311. The targeting test is the important one: a shell dying in a background tab — a build, an
/// agent session, exactly the tabs you are not watching — must not close the tab in front of you.
/// </summary>
/// <remarks>
/// Fix round 1 (#311 review finding): a reviewer flagged that this class, the only caller of
/// <c>ClosePaneAsync</c>/<c>CloseActivePaneAsync</c> in the repo, exercised exactly one branch
/// (single-leaf pane, background tab, <c>skipConfirm: true</c>), so the split-promotion and
/// confirmation-gate paths the task brief protects had no actual regression coverage despite the
/// report implying otherwise.
///
/// Fix round 2 (#311 review finding): a second reviewer correctly pointed out that the round-1
/// justification for skipping the declined confirmation branch was wrong — <c>TerminalPane.Session</c>
/// (private setter) is reachable by reflection exactly like the private methods this file already
/// invokes, so a fake session with <c>HasActiveChildProcesses = true</c> is entirely possible. That
/// was tried: attach such a stub to a non-SSH, non-WSL pane and call <c>ClosePaneAsync</c> with
/// <c>skipConfirm: false</c>. <c>ShouldAutoAcceptRunningPaneClose</c> does decline as expected, and
/// control reaches <c>ShowRunningProcessCloseConfirmationAsync</c>'s <c>await dialog.ShowDialog(this)</c>
/// — a real, modal <c>Window.ShowDialog</c> with no owner ever shown and no button for anything to
/// click. That call did not return: the test process (<c>testhost.exe</c> and the app's own test
/// host) sat alive and unresponsive well past when the run should have finished, confirmed by
/// checking process start times and having to <c>Stop-Process -Force</c> both to reclaim the build
/// output lock afterward. Even racing the close task against a <c>Task.Delay</c> timeout inside the
/// test did not help — the UI thread was stuck inside <c>ShowDialog</c> itself, so the timeout's
/// continuation never got a turn. The actual, accurate blocker is exactly what round 1 described as
/// unlikely — dismissing a real modal headlessly — not an inability to fake the session state. The
/// declined branch remains uncovered here for that reason; the auto-accept half is covered instead
/// (<see cref="ClosePaneAsync_WithConfirmationEnabled_AutoAcceptsWhenNoProcessIsRunning"/>), which at
/// least proves the gate is consulted rather than unconditionally bypassed.
/// </remarks>
public sealed class MainWindowShellExitTests
{
    [AvaloniaFact]
    public async Task ClosePaneAsync_ClosesTheGivenPanesTab_NotTheSelectedOne()
    {
        using var fixture = TwoTabFixture.Create();

        bool closed = await fixture.ClosePaneAsync(fixture.BackgroundPane, skipConfirm: true);

        Assert.True(closed);
        Assert.Single(fixture.Tabs.Items);
        Assert.Same(fixture.SelectedTab, fixture.Tabs.Items[0]);
    }

    /// <summary>
    /// Fix round 1 (#311 review finding): the brief protects split-promotion behaviour, but the
    /// original test only ever exercised the single-leaf/tab-fallback branch of
    /// <c>ClosePaneAsync</c>. This drives a real split — built the same way session restore does,
    /// via <c>SessionManager.CreateRestoredTabItem</c> with a <c>NodeType.Split</c> root — and
    /// checks that closing one leaf promotes its sibling into the tab rather than closing the tab.
    /// </summary>
    [AvaloniaFact]
    public async Task ClosePaneAsync_ClosesOneOfASplitPair_PromotesSiblingIntoTheTab()
    {
        using var fixture = SplitPaneFixture.Create();
        int tabCountBefore = fixture.Tabs.Items.Count;

        bool closed = await fixture.ClosePaneAsync(fixture.PaneToClose, skipConfirm: true);

        Assert.True(closed);
        Assert.Equal(tabCountBefore, fixture.Tabs.Items.Count);
        Assert.Contains(fixture.SplitTab, fixture.Tabs.Items.OfType<TabItem>());
        Assert.Same(fixture.Sibling, fixture.SplitTab.Content);
    }

    /// <summary>
    /// Fix round 2 (#311 review finding, item 1): the brief calls out that the zoom-exit at the
    /// top of <c>ClosePaneAsync</c> changed from looking at the selected tab to looking at the
    /// closed pane's own tab, and nothing exercised that line — every prior fixture here zooms
    /// nothing. This drives the real production zoom path (<c>EnterPaneZoom</c>, the same method
    /// <c>TogglePaneZoomForCurrentTab</c> calls) on the split tab, then selects a DIFFERENT tab
    /// before closing: a selection-based zoom-exit (the old bug) would look at the wrong tab and
    /// never clear the zoom or restore the split's Grid parentage, so the split-promotion below it
    /// would silently fall through to closing the whole tab instead. A pane-tab-based zoom-exit
    /// (the fix) clears it regardless of what happens to be selected.
    /// </summary>
    [AvaloniaFact]
    public async Task ClosePaneAsync_WhenItsTabIsZoomed_ExitsZoomForThatTabRegardlessOfSelection()
    {
        using var fixture = SplitPaneFixture.Create();
        fixture.EnterZoomOnSplitTab();
        Assert.True(fixture.IsTabZoomed(fixture.SplitTab), "Test setup failed: split tab did not enter zoom.");

        // The old bug looked at the selected tab; make sure it is NOT the tab being closed.
        fixture.Tabs.SelectedItem = fixture.OtherTab;

        bool closed = await fixture.ClosePaneAsync(fixture.PaneToClose, skipConfirm: true);

        Assert.True(closed);
        Assert.False(fixture.IsTabZoomed(fixture.SplitTab), "ClosePaneAsync must exit zoom on the closed pane's own tab, not the selected one.");
        Assert.Contains(fixture.SplitTab, fixture.Tabs.Items.OfType<TabItem>());
        Assert.Same(fixture.Sibling, fixture.SplitTab.Content);
    }

    /// <summary>
    /// Fix round 1 (#311 review finding): covers the confirmation gate on the
    /// <c>skipConfirm: false</c> path, which the original test never touched at all (it always
    /// passed <c>skipConfirm: true</c>). A restored pane in these tests never actually spawns a
    /// shell (no real PTY is started unless the pane is attached to a live visual tree), so
    /// <c>TerminalPane.IsProcessRunning</c> is false and <c>ShouldAutoAcceptRunningPaneClose</c>
    /// auto-accepts without showing the confirmation dialog. That is the only half of this branch
    /// reachable headlessly — see the class remarks for why the declined half is not covered here.
    /// </summary>
    [AvaloniaFact]
    public async Task ClosePaneAsync_WithConfirmationEnabled_AutoAcceptsWhenNoProcessIsRunning()
    {
        using var fixture = TwoTabFixture.Create();
        Assert.False(fixture.BackgroundPane.IsProcessRunning, "This test's premise is that no real shell is running.");

        bool closed = await fixture.ClosePaneAsync(fixture.BackgroundPane, skipConfirm: false);

        Assert.True(closed);
        Assert.Single(fixture.Tabs.Items);
        Assert.Same(fixture.SelectedTab, fixture.Tabs.Items[0]);
    }

    [AvaloniaFact]
    public void CleanExit_UnderGraceful_ClosesTheDyingPanesTab_AndLeavesTheSelectedOne()
    {
        using var fixture = TwoTabFixture.Create();
        fixture.Settings.ShellExitPolicy = "Graceful";

        fixture.BackgroundPane.HandleSessionExitForTesting(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Single(fixture.Tabs.Items);
        Assert.Same(fixture.SelectedTab, fixture.Tabs.Items[0]);
    }

    [AvaloniaFact]
    public void NonZeroExit_UnderGraceful_KeepsThePaneAndShowsTheBanner()
    {
        using var fixture = TwoTabFixture.Create();
        fixture.Settings.ShellExitPolicy = "Graceful";

        fixture.BackgroundPane.HandleSessionExitForTesting(1);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, fixture.Tabs.Items.Count);
        string visibleText = TerminalBufferText.Visible(fixture.BackgroundPane.Buffer!);
        Assert.Contains("[Shell exited]", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Exit code: 1]", visibleText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void CleanExit_UnderNever_KeepsThePaneAndShowsTheBanner()
    {
        using var fixture = TwoTabFixture.Create();
        fixture.Settings.ShellExitPolicy = "Never";

        fixture.BackgroundPane.HandleSessionExitForTesting(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, fixture.Tabs.Items.Count);
        string visibleText = TerminalBufferText.Visible(fixture.BackgroundPane.Buffer!);
        Assert.Contains("[Shell exited]", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit code", visibleText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void CleanExit_OnAProtectedTab_KeepsThePaneAndFallsBackToTheBanner()
    {
        // A dying shell must not be able to defeat tab protection — and a pane that cannot close
        // still has to say something, which is the whole point of #311.
        using var fixture = TwoTabFixture.Create();
        fixture.Settings.ShellExitPolicy = "Graceful";
        fixture.ProtectBackgroundTab();

        fixture.BackgroundPane.HandleSessionExitForTesting(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, fixture.Tabs.Items.Count);
        Assert.Contains("[Shell exited]", TerminalBufferText.Visible(fixture.BackgroundPane.Buffer!), StringComparison.Ordinal);
    }

    private sealed class TwoTabFixture : IDisposable
    {
        private TwoTabFixture(NovaTerminal.MainWindow window, TabControl tabs, TabItem selectedTab, TerminalPane backgroundPane)
        {
            Window = window;
            Tabs = tabs;
            SelectedTab = selectedTab;
            BackgroundPane = backgroundPane;
        }

        public NovaTerminal.MainWindow Window { get; }
        public TabControl Tabs { get; }
        public TabItem SelectedTab { get; }
        public TerminalPane BackgroundPane { get; }
        public TerminalSettings Settings { get; private init; } = null!;
        public TabItem BackgroundTab { get; private init; } = null!;

        public void ProtectBackgroundTab()
        {
            object state = typeof(NovaTerminal.MainWindow)
                .GetMethod("GetOrCreateTabState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Window, [BackgroundTab])!;
            state.GetType().GetProperty("IsProtected")!.SetValue(state, true);
        }

        public Task<bool> ClosePaneAsync(TerminalPane pane, bool skipConfirm)
        {
            var method = typeof(NovaTerminal.MainWindow)
                .GetMethod("ClosePaneAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task<bool>)method.Invoke(Window, [pane, skipConfirm])!;
        }

        public static TwoTabFixture Create()
        {
            AppServiceBundle bundle = AppServices.BuildForDesigner();
            var window = new NovaTerminal.MainWindow(bundle);
            TabControl tabs = window.FindControl<TabControl>("Tabs")!;
            var settings = (TerminalSettings)typeof(NovaTerminal.MainWindow)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;

            tabs.Items.Clear();
            TabItem background = CreateTab(window, tabs, settings, "Background");
            TabItem selected = CreateTab(window, tabs, settings, "Selected");
            tabs.SelectedItem = selected;

            return new TwoTabFixture(window, tabs, selected, (TerminalPane)background.Content!)
            {
                Settings = settings,
                BackgroundTab = background
            };
        }

        private static TabItem CreateTab(NovaTerminal.MainWindow window, TabControl tabs, TerminalSettings settings, string title)
        {
            var tabSession = new TabSession
            {
                Title = title,
                Root = new PaneNode
                {
                    Type = NodeType.Leaf,
                    Command = "cmd.exe",
                    Arguments = string.Empty,
                    PaneId = Guid.NewGuid().ToString()
                }
            };

            TabItem tab = SessionManager.CreateRestoredTabItem(tabSession, settings)!;
            tabs.Items.Add(tab);

            // The production entry point for restored content — it is what wires the pane's
            // events to the window (ProcessExited included, which Task 4 depends on).
            typeof(NovaTerminal.MainWindow)
                .GetMethod("InitializeRestoredTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [tabs]);

            return tab;
        }

        public void Dispose() => Window.Close();
    }

    /// <summary>
    /// Fix round 1 (#311 review finding): builds a tab holding a real split — the same shape
    /// <c>SessionManager.CreateRestoredTabItem</c> produces from a <c>NodeType.Split</c> root with
    /// two <c>NodeType.Leaf</c> children — so the split-promotion branch of
    /// <c>ClosePaneAsync</c> has an actual regression test.
    /// </summary>
    private sealed class SplitPaneFixture : IDisposable
    {
        private SplitPaneFixture(
            NovaTerminal.MainWindow window,
            TabControl tabs,
            TabItem splitTab,
            TerminalPane paneToClose,
            TerminalPane sibling,
            TabItem otherTab)
        {
            Window = window;
            Tabs = tabs;
            SplitTab = splitTab;
            PaneToClose = paneToClose;
            Sibling = sibling;
            OtherTab = otherTab;
        }

        public NovaTerminal.MainWindow Window { get; }
        public TabControl Tabs { get; }
        public TabItem SplitTab { get; }
        public TerminalPane PaneToClose { get; }
        public TerminalPane Sibling { get; }

        /// <summary>
        /// An unrelated tab, present so the zoom-exit regression test can select it and prove
        /// <c>ClosePaneAsync</c> resolves the zoom to close from the closed pane's own tab, not
        /// from whatever tab happens to be selected.
        /// </summary>
        public TabItem OtherTab { get; }

        public Task<bool> ClosePaneAsync(TerminalPane pane, bool skipConfirm)
        {
            var method = typeof(NovaTerminal.MainWindow)
                .GetMethod("ClosePaneAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task<bool>)method.Invoke(Window, [pane, skipConfirm])!;
        }

        /// <summary>
        /// Drives the actual production zoom path — the same private <c>EnterPaneZoom</c> method
        /// <c>TogglePaneZoomForCurrentTab</c> calls — directly on <see cref="SplitTab"/>, without
        /// requiring it to be the currently selected tab (the public toggle path only ever zooms
        /// the selection).
        /// </summary>
        public void EnterZoomOnSplitTab()
        {
            var method = typeof(NovaTerminal.MainWindow)
                .GetMethod("EnterPaneZoom", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var entered = (bool)method.Invoke(Window, [SplitTab, PaneToClose, true])!;
            if (!entered)
            {
                throw new InvalidOperationException("EnterPaneZoom refused to zoom the split tab's pane.");
            }
        }

        /// <summary>Reads the private <c>_paneZoomStateByTab</c> dictionary this file cannot see the type of.</summary>
        public bool IsTabZoomed(TabItem tab)
        {
            var field = typeof(NovaTerminal.MainWindow)
                .GetField("_paneZoomStateByTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var dictionary = (System.Collections.IDictionary)field.GetValue(Window)!;
            return dictionary.Contains(tab);
        }

        public static SplitPaneFixture Create()
        {
            AppServiceBundle bundle = AppServices.BuildForDesigner();
            var window = new NovaTerminal.MainWindow(bundle);
            TabControl tabs = window.FindControl<TabControl>("Tabs")!;
            var settings = (TerminalSettings)typeof(NovaTerminal.MainWindow)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;

            tabs.Items.Clear();

            var tabSession = new TabSession
            {
                Title = "Split",
                Root = new PaneNode
                {
                    Type = NodeType.Split,
                    SplitOrientation = 0, // horizontal (columns), matches SessionManager's default
                    Children =
                    {
                        new PaneNode
                        {
                            Type = NodeType.Leaf,
                            Command = "cmd.exe",
                            Arguments = string.Empty,
                            PaneId = Guid.NewGuid().ToString()
                        },
                        new PaneNode
                        {
                            Type = NodeType.Leaf,
                            Command = "cmd.exe",
                            Arguments = string.Empty,
                            PaneId = Guid.NewGuid().ToString()
                        }
                    },
                    Sizes = { "1*", "1*" }
                }
            };
            var otherTabSession = new TabSession
            {
                Title = "Other",
                Root = new PaneNode
                {
                    Type = NodeType.Leaf,
                    Command = "cmd.exe",
                    Arguments = string.Empty,
                    PaneId = Guid.NewGuid().ToString()
                }
            };

            TabItem splitTab = SessionManager.CreateRestoredTabItem(tabSession, settings)!;
            tabs.Items.Add(splitTab);
            TabItem otherTab = SessionManager.CreateRestoredTabItem(otherTabSession, settings)!;
            tabs.Items.Add(otherTab);
            tabs.SelectedItem = splitTab;

            // The production entry point for restored content — it is what wires the panes'
            // events to the window, same as the single-leaf fixture above.
            typeof(NovaTerminal.MainWindow)
                .GetMethod("InitializeRestoredTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [tabs]);

            // SessionManager.RestorePaneTree builds the split as a Grid whose Children are
            // [child0, GridSplitter, child1] (splitter inserted before every child but the
            // first) — OfType<TerminalPane>() filters the splitter out and preserves order.
            var grid = (Grid)splitTab.Content!;
            var panes = grid.Children.OfType<TerminalPane>().ToList();
            if (panes.Count != 2)
            {
                throw new InvalidOperationException(
                    $"Expected the split to restore exactly two panes, found {panes.Count}.");
            }

            return new SplitPaneFixture(window, tabs, splitTab, panes[0], panes[1], otherTab);
        }

        public void Dispose() => Window.Close();
    }
}
