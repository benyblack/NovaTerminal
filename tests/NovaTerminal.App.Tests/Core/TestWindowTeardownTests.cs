using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.AgentHost;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// That <see cref="TestMainWindowFactory.DisposeCreatedWindows"/> actually tears panes down, in
/// each of the shapes that defeated an earlier version of it.
/// </summary>
/// <remarks>
/// <para>
/// The teardown is deliberately quiet — it skips a window whose dispatcher is not ours and
/// swallows what disposal throws, because failing there would redden whichever unrelated test
/// happened to be finishing. That quiet is the problem this file solves: without it, a traversal
/// that stopped finding panes would leak shells again and nothing would say so.
/// </para>
/// <para>
/// Observed through <see cref="AgentSessionRegistry"/> rather than through the PTY. Every pane
/// registers itself and <c>DetachFromUiThread</c> removes the entry, so the registry answers
/// "was this pane torn down" without depending on a shell having spawned — which it has not, in a
/// window that was never shown. The registry is process-wide, so each test diffs against a
/// snapshot rather than asserting a total, the same way <c>AgentIndicatorTabRollupTests</c> does.
/// </para>
/// </remarks>
public sealed class TestWindowTeardownTests : IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    public void AWindowsPanes_AreGoneAfterTeardown()
    {
        HashSet<Guid> before = RegisteredPaneIds();
        TestMainWindowFactory.Create();

        Guid[] added = RegisteredPaneIds().Except(before).ToArray();
        Assert.NotEmpty(added);

        TestMainWindowFactory.DisposeCreatedWindows();

        Assert.Empty(RegisteredPaneIds().Intersect(added));
    }

    /// <summary>
    /// Zoom moves the tab's real root off the visual tree and leaves only the zoomed pane in
    /// <c>ti.Content</c>, so a teardown that walks the content of a zoomed tab reaches the zoomed
    /// pane and abandons its siblings. That is exactly what an earlier version of this cleanup did,
    /// which is why the sibling is what this asserts on.
    /// </summary>
    [AvaloniaFact]
    public void TheHiddenSiblingOfAZoomedPane_IsGoneAfterTeardown()
    {
        HashSet<Guid> before = RegisteredPaneIds();
        MainWindow window = TestMainWindowFactory.Create();
        (TabItem tab, TerminalPane zoomed, TerminalPane sibling) = AddSplitTab(window);

        EnterZoom(window, tab, zoomed);

        // The zoom is real: the tab now shows the zoomed pane alone, and the sibling is off-tree.
        Assert.Same(zoomed, tab.Content);
        Guid[] added = RegisteredPaneIds().Except(before).ToArray();
        Assert.Contains(sibling.PaneId, added);

        TestMainWindowFactory.DisposeCreatedWindows();

        Assert.DoesNotContain(sibling.PaneId, RegisteredPaneIds());
        Assert.Empty(RegisteredPaneIds().Intersect(added));
    }

    /// <summary>
    /// A pane whose tab has left the collection is invisible to a walk of <c>Tabs.Items</c>;
    /// <c>MainWindowStartupTests</c> clears the collection outright to build its own strip.
    /// </summary>
    [AvaloniaFact]
    public void ThePanesOfARemovedTab_AreGoneAfterTeardown()
    {
        HashSet<Guid> before = RegisteredPaneIds();
        MainWindow window = TestMainWindowFactory.Create();

        Guid[] added = RegisteredPaneIds().Except(before).ToArray();
        Assert.NotEmpty(added);

        window.FindControl<TabControl>("Tabs")!.Items.Clear();

        TestMainWindowFactory.DisposeCreatedWindows();

        Assert.Empty(RegisteredPaneIds().Intersect(added));
    }

    private static HashSet<Guid> RegisteredPaneIds()
        => AgentSessionRegistry.Instance.GetRegistrations().Select(r => r.PaneId).ToHashSet();

    /// <summary>Adds a two-pane split tab, the shape zoom needs. Mirrors AgentObserveIndicatorTests.</summary>
    private static (TabItem Tab, TerminalPane First, TerminalPane Second) AddSplitTab(MainWindow window)
    {
        var settings = (TerminalSettings)typeof(MainWindow)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

        string shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var session = new TabSession
        {
            Title = "Split",
            Root = new PaneNode
            {
                Type = NodeType.Split,
                SplitOrientation = 0,
                Children =
                {
                    new PaneNode { Type = NodeType.Leaf, Command = shell, Arguments = string.Empty, PaneId = Guid.NewGuid().ToString() },
                    new PaneNode { Type = NodeType.Leaf, Command = shell, Arguments = string.Empty, PaneId = Guid.NewGuid().ToString() }
                },
                Sizes = { "1*", "1*" }
            }
        };

        TabItem tab = SessionManager.CreateRestoredTabItem(session, settings)!;
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.Items.Add(tab);

        typeof(MainWindow)
            .GetMethod("InitializeRestoredTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tabs });

        // RestorePaneTree builds [child0, GridSplitter, child1]; OfType drops the splitter and
        // preserves order.
        List<TerminalPane> panes = ((Grid)tab.Content!).Children.OfType<TerminalPane>().ToList();
        Assert.Equal(2, panes.Count);
        return (tab, panes[0], panes[1]);
    }

    private static void EnterZoom(MainWindow window, TabItem tab, TerminalPane pane)
    {
        var entered = (bool)typeof(MainWindow)
            .GetMethod("EnterPaneZoom", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tab, pane, false })!;

        Assert.True(entered, "Test setup failed: EnterPaneZoom refused to zoom the pane.");
    }
}
