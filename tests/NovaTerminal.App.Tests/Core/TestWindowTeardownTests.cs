using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
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
/// <para>
/// Each test also points <c>NOVATERM_APPDATA_ROOT</c> at a fresh scratch directory, and that is
/// load-bearing rather than tidy — it is the fix for #434. Without it,
/// <c>TestMainWindowFactory.Create()</c> runs the real startup path against the machine's real
/// profile, so what the constructor does depends on a file on disk. With no saved session it
/// calls <c>AddTab</c> and one pane registers synchronously; with one, it takes the restore path
/// instead — and there the only tab built eagerly is the *selected* one, every other tab being a
/// placeholder that holds no <c>TerminalPane</c> until the Background-priority
/// <c>DrainDeferred</c> post runs. So a saved session whose selected tab does not materialize
/// (<c>Root: null</c>, which <c>CaptureSession</c> persists for any tab whose content is not a
/// pane tree) restores to placeholders alone, and the window registers no pane at all before
/// <c>Create()</c> returns. That is what reddened the gate: the two tests below that rely on the
/// window's own startup tab failed their *precondition*, reading as a teardown regression when
/// teardown was never reached. Tests in this assembly close real <c>MainWindow</c>s, and
/// <c>OnClosing</c> saves the session, so the poisoning file is one the suite writes itself.
/// </para>
/// </remarks>
public sealed class TestWindowTeardownTests : IDisposable
{
    private readonly string _appDataRoot =
        Path.Combine(Path.GetTempPath(), $"novaterm_teardown_test_{Guid.NewGuid():N}");

    private readonly string? _previousAppDataRoot =
        Environment.GetEnvironmentVariable(AppDataRootEnvVar);

    private const string AppDataRootEnvVar = "NOVATERM_APPDATA_ROOT";

    public TestWindowTeardownTests()
    {
        Directory.CreateDirectory(_appDataRoot);
        Environment.SetEnvironmentVariable(AppDataRootEnvVar, _appDataRoot);
    }

    /// <remarks>
    /// The windows go first: disposing them is what the tests are about, and it must happen while
    /// the override is still in force so a window that saves anything saves it into the scratch
    /// directory rather than the developer's real profile.
    /// </remarks>
    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        Environment.SetEnvironmentVariable(AppDataRootEnvVar, _previousAppDataRoot);
        try { Directory.Delete(_appDataRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// That the isolated root really does give the deterministic startup path the two
    /// snapshot-diffing tests below assume: exactly one pane, registered before
    /// <see cref="TestMainWindowFactory.Create()"/> returns. Named separately so a regression in
    /// the isolation itself fails here, saying what broke, instead of surfacing as an unexplained
    /// precondition failure in a test about teardown.
    /// </summary>
    [AvaloniaFact]
    public void AFreshWindow_RegistersItsStartupPane_BeforeCreateReturns()
    {
        HashSet<Guid> before = RegisteredPaneIds();

        TestMainWindowFactory.Create();

        Assert.Single(RegisteredPaneIds().Except(before));
    }

    [AvaloniaFact]
    public void AWindowsPanes_AreGoneAfterTeardown()
    {
        HashSet<Guid> before = RegisteredPaneIds();
        TestMainWindowFactory.Create();

        Guid[] added = RegisteredPaneIds().Except(before).ToArray();
        AssertStartupPaneRegistered(added);

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
        AssertStartupPaneRegistered(added);

        window.FindControl<TabControl>("Tabs")!.Items.Clear();

        TestMainWindowFactory.DisposeCreatedWindows();

        Assert.Empty(RegisteredPaneIds().Intersect(added));
    }

    /// <summary>
    /// The same guarantee for <c>DisposeAllTabs</c>, the walk behind <c>ApplySessionSnapshot</c>.
    /// It had the same blind spot and the consequence there is worse than a stale pane:
    /// <c>ResetTabCollections</c> runs on the next line and clears <c>_paneZoomStateByTab</c>, so
    /// the hidden siblings become unreachable and their shells outlive the workspace that owned
    /// them.
    /// </summary>
    /// <remarks>
    /// Housed here rather than in a file of its own on purpose. A separate class for these two
    /// destabilised <c>VerticalTabStripTests</c> and <c>TabRunningCommandTests</c> - the suite's two
    /// heaviest <c>RunJobs()</c> callers - with cross-thread failures inside the compositor, on both
    /// CI lanes and intermittently here. Adding an Avalonia test class shifts what runs next to
    /// what, and something in that neighbourhood is fragile in a way this change did not cause and
    /// has not explained. Keeping the coverage inside a class that already coexists with those
    /// tests buys the assertion without buying the reshuffle.
    /// </remarks>
    [AvaloniaFact]
    public void TheHiddenSiblingOfAZoomedPane_IsGoneAfterTheTabStripIsReplaced()
    {
        MainWindow window = TestMainWindowFactory.Create();
        (TabItem tab, TerminalPane zoomed, TerminalPane sibling) = AddSplitTab(window);

        EnterZoom(window, tab, zoomed);

        Assert.Same(zoomed, tab.Content);
        Assert.Contains(sibling.PaneId, RegisteredPaneIds());

        DisposeAllTabs(window);

        Assert.DoesNotContain(sibling.PaneId, RegisteredPaneIds());
        Assert.DoesNotContain(zoomed.PaneId, RegisteredPaneIds());
    }

    /// <summary>Unzoomed, so the one above is not passing on a harness that never worked.</summary>
    [AvaloniaFact]
    public void ThePanesOfAPlainTab_AreGoneAfterTheTabStripIsReplaced()
    {
        MainWindow window = TestMainWindowFactory.Create();
        (_, TerminalPane first, TerminalPane second) = AddSplitTab(window);

        Guid[] replaced = new[] { first.PaneId, second.PaneId };
        Assert.Equal(replaced.Length, RegisteredPaneIds().Intersect(replaced).Count());

        DisposeAllTabs(window);

        Assert.Empty(RegisteredPaneIds().Intersect(replaced));
    }

    private static void DisposeAllTabs(MainWindow window)
        => typeof(MainWindow)
            .GetMethod("DisposeAllTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window.FindControl<TabControl>("Tabs")! });

    private static HashSet<Guid> RegisteredPaneIds()
        => AgentSessionRegistry.Instance.GetRegistrations().Select(r => r.PaneId).ToHashSet();

    /// <summary>
    /// The precondition of a teardown test: the window registered something to tear down. Says so
    /// in the failure message, because a bare <c>Assert.NotEmpty</c> here reads as "teardown left
    /// panes behind" when it means the opposite — see #434 and this class's remarks.
    /// </summary>
    private static void AssertStartupPaneRegistered(Guid[] added)
        => Assert.True(
            added.Length > 0,
            "Test setup failed: creating a window registered no new panes, so there is nothing for "
            + "the teardown assertion below to be about. The window's startup path built no "
            + "TerminalPane synchronously - check that NOVATERM_APPDATA_ROOT is still redirected "
            + "to an empty scratch directory, since a saved session there sends the constructor "
            + "down the restore path instead of AddTab.");

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

        // Drained here, while the pane is still alive. EnterPaneZoom ends in FocusPaneTerminal
        // with defer, which posts two jobs to the dispatcher the whole assembly shares; left
        // queued, the next class to call RunJobs() runs them against a pane this test has since
        // disposed.
        Dispatcher.UIThread.RunJobs();
    }
}
