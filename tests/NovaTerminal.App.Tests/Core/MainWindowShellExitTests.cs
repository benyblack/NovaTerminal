using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// #311. The targeting test is the important one: a shell dying in a background tab — a build, an
/// agent session, exactly the tabs you are not watching — must not close the tab in front of you.
/// </summary>
/// <remarks>
/// Fix round 1 (#311 review finding): a reviewer flagged that this class, the only caller of
/// <c>ClosePaneAsync</c>/<c>ClosePaneAsync</c>/<c>CloseActivePane</c> in the repo, exercised
/// exactly one branch (single-leaf pane, background tab, <c>skipConfirm: true</c>), so the
/// split-promotion and confirmation-gate paths the task brief protects had no actual regression
/// coverage despite the report implying otherwise.
///
/// The declined half of the confirmation gate (<c>skipConfirm: false</c> with a pane the policy
/// would actually question) is deliberately NOT covered here. Reaching it requires
/// <c>ShouldAutoAcceptRunningPaneClose</c> to return false, which for a non-SSH, non-WSL pane
/// means it has active child processes or unsafe shell args — and once that gate is not
/// auto-accepted, <c>ShowRunningProcessCloseConfirmationAsync</c> opens a real modal
/// (<c>await dialog.ShowDialog(this)</c>) that only resolves when a button is clicked. Nothing in
/// this headless suite can click that button, and there is no way to fake
/// <c>HasActiveChildProcesses</c> true without either a real child process or a production-code
/// seam that does not exist today, so driving the decline path would hang the test run rather than
/// assert anything. The auto-accept half is covered instead
/// (<see cref="ClosePaneAsync_WithConfirmationEnabled_AutoAcceptsWhenNoProcessIsRunning"/>), which
/// at least proves the gate is consulted rather than unconditionally bypassed.
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
        Assert.Same(fixture.SplitTab, fixture.Tabs.Items[fixture.Tabs.Items.IndexOf(fixture.SplitTab)]);
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

            return new TwoTabFixture(window, tabs, selected, (TerminalPane)background.Content!);
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
            TerminalPane sibling)
        {
            Window = window;
            Tabs = tabs;
            SplitTab = splitTab;
            PaneToClose = paneToClose;
            Sibling = sibling;
        }

        public NovaTerminal.MainWindow Window { get; }
        public TabControl Tabs { get; }
        public TabItem SplitTab { get; }
        public TerminalPane PaneToClose { get; }
        public TerminalPane Sibling { get; }

        public Task<bool> ClosePaneAsync(TerminalPane pane, bool skipConfirm)
        {
            var method = typeof(NovaTerminal.MainWindow)
                .GetMethod("ClosePaneAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task<bool>)method.Invoke(Window, [pane, skipConfirm])!;
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

            TabItem splitTab = SessionManager.CreateRestoredTabItem(tabSession, settings)!;
            tabs.Items.Add(splitTab);
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

            return new SplitPaneFixture(window, tabs, splitTab, panes[0], panes[1]);
        }

        public void Dispose() => Window.Close();
    }
}
