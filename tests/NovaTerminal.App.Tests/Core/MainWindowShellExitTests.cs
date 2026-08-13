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
}
