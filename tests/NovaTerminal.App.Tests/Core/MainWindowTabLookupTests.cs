using System;
using System.Collections.Generic;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// #319 and #314: MainWindow answers "which tab owns this pane" with
/// <c>pane.FindAncestorOfType&lt;TabItem&gt;()</c>, a *visual*-tree lookup that can never
/// resolve — MainWindow.axaml's TabControl template hosts content in
/// <c>PART_SelectedContentHost</c>, a sibling of the header presenter, so a TabItem is never a
/// visual ancestor of its own content. Every site that asked the question that way was silently
/// answering "no tab", which is why the bell indicator never appeared and why a split tab
/// resolved the wrong pane.
/// </summary>
public sealed class MainWindowTabLookupTests
{
    /// <summary>
    /// #319: the cached active pane was discarded on every call, because the staleness check
    /// compared <c>null == tabItem</c>. The fallback then returned the *first* pane in the tab,
    /// so leaving a split tab and coming back activated the wrong pane, and a closed split tab
    /// was attributed to the wrong pane in the agent journal.
    /// </summary>
    [AvaloniaFact]
    public void ResolvePaneForTab_ReturnsTheActivePane_NotTheFirstOneInASplit()
    {
        using var fixture = SplitTabFixture.Create();

        // Exactly what focusing the second pane does in production.
        fixture.Invoke("UpdateActivePane", fixture.SecondPane);

        var resolved = (TerminalPane?)fixture.Invoke("ResolvePaneForTab", fixture.Tab);

        Assert.Same(fixture.SecondPane, resolved);
    }

    /// <summary>
    /// #314 (bell only): a bell in a background tab's pane must mark that tab. The handler
    /// bailed out before it could, because the tab lookup never resolved.
    /// </summary>
    [AvaloniaFact]
    public void Bell_InABackgroundTabsPane_MarksThatTab()
    {
        using var fixture = SplitTabFixture.Create();

        // The handler deliberately ignores bells in the tab you are already looking at, so the
        // fixture's tab must not be selected — which is also the case that matters to a user.
        Assert.NotSame(fixture.Tab, fixture.Tabs.SelectedItem);

        fixture.Invoke("OnPaneBellReceived", fixture.FirstPane);

        Assert.True(fixture.TabHasBell(), "a bell in a background tab's pane must set that tab's bell marker");
    }

    private sealed class SplitTabFixture : IDisposable
    {
        private SplitTabFixture(NovaTerminal.MainWindow window, TabControl tabs, TabItem tab, TerminalPane first, TerminalPane second)
        {
            Window = window;
            Tabs = tabs;
            Tab = tab;
            FirstPane = first;
            SecondPane = second;
        }

        public NovaTerminal.MainWindow Window { get; }
        public TabControl Tabs { get; }
        public TabItem Tab { get; }
        public TerminalPane FirstPane { get; }
        public TerminalPane SecondPane { get; }

        public object? Invoke(string method, params object?[] args)
        {
            MethodInfo m = typeof(NovaTerminal.MainWindow)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException($"MainWindow.{method} not found");
            return m.Invoke(Window, args);
        }

        public bool TabHasBell()
        {
            object state = Invoke("GetOrCreateTabState", Tab)!;
            return (bool)state.GetType().GetProperty("HasBell")!.GetValue(state)!;
        }

        public static SplitTabFixture Create()
        {
            AppServiceBundle bundle = AppServices.BuildForDesigner();
            var window = new NovaTerminal.MainWindow(bundle);
            TabControl tabs = window.FindControl<TabControl>("Tabs")!;
            var settings = (TerminalSettings)typeof(NovaTerminal.MainWindow)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;

            tabs.Items.Clear();

            // A split tab, built the way session restore builds one: a Split root over two
            // leaves. Two panes is the whole point — with one pane, "the first pane" and "the
            // active pane" are the same and #319 is invisible.
            var tabSession = new TabSession
            {
                Title = "Split",
                Root = new PaneNode
                {
                    Type = NodeType.Split,
                    SplitOrientation = 0,
                    Children = new List<PaneNode>
                    {
                        NewLeaf(),
                        NewLeaf(),
                    },
                },
            };

            TabItem tab = SessionManager.CreateRestoredTabItem(tabSession, settings)!;
            tabs.Items.Add(tab);

            // A second tab, selected, so the tab under test is a background tab.
            TabItem other = SessionManager.CreateRestoredTabItem(
                new TabSession { Title = "Other", Root = NewLeaf() }, settings)!;
            tabs.Items.Add(other);
            tabs.SelectedItem = other;

            typeof(NovaTerminal.MainWindow)
                .GetMethod("InitializeRestoredTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [tabs]);

            var panes = new List<TerminalPane>();
            CollectPanes(tab.Content as Control, panes);
            Assert.Equal(2, panes.Count);

            return new SplitTabFixture(window, tabs, tab, panes[0], panes[1]);
        }

        private static PaneNode NewLeaf() => new()
        {
            Type = NodeType.Leaf,
            Command = "cmd.exe",
            Arguments = string.Empty,
            PaneId = Guid.NewGuid().ToString(),
        };

        private static void CollectPanes(Control? control, List<TerminalPane> into)
        {
            switch (control)
            {
                case null:
                    return;
                case TerminalPane pane:
                    into.Add(pane);
                    return;
                case Panel panel:
                    foreach (Control child in panel.Children)
                    {
                        CollectPanes(child, into);
                    }
                    return;
                case ContentControl contentControl:
                    CollectPanes(contentControl.Content as Control, into);
                    return;
                case Decorator decorator:
                    CollectPanes(decorator.Child, into);
                    return;
            }
        }

        public void Dispose() => Window.Close();
    }
}
