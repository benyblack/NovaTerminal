using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NovaTerminal.AgentHost;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// The precise "command in flight" half of the vertical tab dot: MainWindow.
/// RefreshTabStatuses aggregates AgentSessionStatusKind.Running from the
/// process-wide agent-session registry onto each tab's HasRunningCommand flag
/// (keyed by the registration's TabId vs the tab's persistent id), and the
/// existing ResolveTabDot plumbing turns that flag into the theme-blue working
/// dot even when the 2 s output-burst heuristic has already decayed to Idle.
///
/// The window is created through TestMainWindowFactory (real constructor, fresh
/// default settings via AppServices.BuildForDesigner). Its first tab is a real
/// pane whose registration lands in AgentSessionRegistry.Instance — the same
/// process-wide singleton every other window-creating test shares — so these
/// tests follow AgentIndicatorTabRollupTests.RunIsolated: point
/// NOVATERM_APPDATA_ROOT at a scratch directory (so nothing reaches the
/// developer's real app data), snapshot the registry before creating the
/// window, and diff afterwards to isolate the registration this window added.
///
/// Registry cleanup mirrors production CloseTab: Window.Close() alone never
/// disposes panes (the real close path runs DisposeControlTree →
/// TerminalPane.DetachFromUiThread → Unregister), so the finally block
/// unregisters the diffed registration explicitly. Without that, the driven
/// status machine would stay reachable through the shared registry forever.
/// </summary>
public sealed class TabRunningCommandTests : IDisposable
{
    /// <summary>
    /// Disposes the panes of every window this class asked for, and with them the real shells
    /// behind them. xUnit builds a fresh instance per test, so this runs after each one.
    /// </summary>
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static TerminalSettings GetSettings(MainWindow window)
        => (TerminalSettings)typeof(MainWindow)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static void MakeVertical(MainWindow window)
    {
        GetSettings(window).TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A second, pane-less tab (plain factory — no PTY spawn, no registration),
    /// laid out vertically so it renders a header like any other tab.</summary>
    private static TabItem AddPlainVerticalTab(MainWindow window)
    {
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = new TabItem { Content = new Border() };
        tabs.Items.Add(tab);
        window.ApplyTabLayout(); // rebuild headers so the new tab gets a vertical row
        Dispatcher.UIThread.RunJobs();
        return tab;
    }

    private static Avalonia.Media.Color? DotColorOf(TabItem tab)
    {
        var dot = MainWindow.FindTabHeaderDescendant<Avalonia.Controls.Shapes.Ellipse>(tab.Header, "TabStatusDot");
        return (dot?.Fill as Avalonia.Media.ISolidColorBrush)?.Color;
    }

    private static void RunIsolated(Action<MainWindow, AgentSessionRegistration> body)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"novaterm_tab_running_test_{Guid.NewGuid():N}");
        string? previousRoot = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        Directory.CreateDirectory(tempRoot);

        AgentSessionRegistration? registration = null;
        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", tempRoot);

            var before = AgentSessionRegistry.Instance.GetRegistrations();
            var window = TestMainWindowFactory.Create();
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var added = AgentSessionRegistry.Instance.GetRegistrations().Except(before).ToArray();
            registration = Assert.Single(added);

            body(window, registration);
        }
        finally
        {
            // Production CloseTab runs DisposeControlTree → DetachFromUiThread →
            // Unregister; Window.Close() alone never does, so do the unregister half
            // explicitly. This also retires whatever status the test drove the
            // machine to: an unregistered pane's machine is reachable by nobody.
            if (registration != null)
            {
                AgentSessionRegistry.Instance.Unregister(registration.PaneId);
            }

            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previousRoot);
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [AvaloniaFact]
    public void RefreshTabStatuses_RunningRegistration_FlagsOnlyTheOwningTab_AndClearsOnFinish()
    {
        RunIsolated((window, registration) =>
        {
            MakeVertical(window);
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var tabA = tabs.Items.Cast<TabItem>().First(); // the real-pane tab this window opened with
            var tabB = AddPlainVerticalTab(window);

            // The mapping the aggregation relies on: AddTab associated the pane's
            // registration with tab A's persistent id.
            Assert.Equal(window.GetPersistentTabId(tabA), registration.TabId);

            // Baseline: nothing is running, both tabs flag false after a pass.
            window.RefreshTabStatuses();
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsTabRunningCommandForTest(tabA));
            Assert.False(window.IsTabRunningCommandForTest(tabB));

            // Precise tier: a shell-integration command start drives the machine to
            // Running regardless of output (deterministic — no clocks, no sweeps).
            registration.StatusMachine.NotifyCommandStarted();
            Assert.Equal(AgentSessionStatusKind.Running, registration.StatusMachine.Snapshot().Kind);

            window.RefreshTabStatuses();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsTabRunningCommandForTest(tabA), "the tab owning the running pane must be flagged");
            Assert.False(window.IsTabRunningCommandForTest(tabB), "a tab with no running pane must stay unflagged");

            registration.StatusMachine.NotifyCommandFinished(0);
            Assert.NotEqual(AgentSessionStatusKind.Running, registration.StatusMachine.Snapshot().Kind);

            window.RefreshTabStatuses();
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsTabRunningCommandForTest(tabA), "the flag must clear once no pane reports Running");
        });
    }

    [AvaloniaFact]
    public void RefreshTabStatuses_RunningCommand_PaintsWorkingDotWhileHeuristicStaysIdle()
    {
        RunIsolated((window, registration) =>
        {
            MakeVertical(window);
            var tabA = AddPlainVerticalTab(window);

            // The real pane's shell greets asynchronously when its PTY warms up, which
            // races the heuristic's 2 s decay window and would make "RenderedStatus is
            // Idle" a timing assertion. Re-associating the registration with the
            // pane-less tab (the same AgentSessionRegistry.SetTabAssociation production
            // runs when a pane lands in a different tab) pins the mapping input while
            // giving the observed tab a tracker that deterministically never sees
            // output — so only the precise Running flag can paint its dot.
            Assert.True(AgentSessionRegistry.Instance.SetTabAssociation(
                registration.PaneId, window.GetPersistentTabId(tabA)));

            registration.StatusMachine.NotifyCommandStarted();
            window.RefreshTabStatuses();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsTabRunningCommandForTest(tabA));
            Assert.Equal(TabTrackerStatus.Idle, window.GetTabRenderedStatusForTest(tabA));
            // Working dot = the theme-blue workingBrush UpdateTabVisuals passes to
            // UpdateVerticalTabExtras — not the amber attention or agent brushes, and
            // not the heuristic (that rendered Idle).
            var expectedWorking = GetSettings(window).ActiveTheme.Blue.ToAvaloniaColor();
            Assert.Equal(expectedWorking, DotColorOf(tabA));

            // Finish: with the heuristic Idle and no markers, the dot must fall back
            // to transparent — the running flag, not a stale paint, drove the blue.
            registration.StatusMachine.NotifyCommandFinished(0);
            window.RefreshTabStatuses();
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsTabRunningCommandForTest(tabA));
            Assert.Equal(Avalonia.Media.Colors.Transparent, DotColorOf(tabA));
        });
    }
}
