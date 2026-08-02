using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Guards the invariant that every pane a window owns is wired by <c>MainWindow.WirePane</c>, and
/// therefore has the dependencies the PTY spawn path dereferences.
/// </summary>
/// <remarks>
/// Phase 0b originally assigned <c>CommandAssistServices</c> at the three pane-creation sites in
/// <c>MainWindow</c>. Session restore does not create panes there - <c>SessionManager</c> builds
/// them and they reach the window through <c>InitializeRestoredTabs</c> - so every restored pane
/// came up without the graph. With <c>CommandAssistShellIntegrationEnabled</c> defaulting to true
/// independently of the Command Assist master flag, that pane then threw out of
/// <c>ApplyShellIntegrationLaunchPlan</c> while composing its launch, the spawn's catch turned the
/// throw into "[ERROR] Failed to spawn process", and the session was never created: restoring a
/// workspace produced a window full of dead panes.
/// </remarks>
public sealed class MainWindowPaneWiringTests
{
    [AvaloniaFact]
    public void InitializeRestoredTabs_InjectsCommandAssistServicesIntoRestoredPanes()
    {
        using var fixture = RestoredPaneFixture.Create();

        Assert.Same(TestCommandAssistServices.Instance, fixture.Pane.CommandAssistServices);
    }

    /// <summary>
    /// Crosses the gap between "the property is null" and the failure users saw: drives the
    /// restored pane through the shell-integration step of the spawn path, which is the first
    /// thing to dereference the graph.
    /// </summary>
    [AvaloniaFact]
    public void RestoredPane_ComposesShellIntegrationLaunchPlanWithoutThrowing()
    {
        using var fixture = RestoredPaneFixture.Create();

        Assert.True(
            fixture.Settings.CommandAssistShellIntegrationEnabled,
            "This regression only reproduces while shell integration defaults on; if that default " +
            "changed, the test needs to set it explicitly rather than be quietly neutered.");

        MethodInfo applyLaunchPlan = typeof(TerminalPane).GetMethod(
            "ApplyShellIntegrationLaunchPlan",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        // "cmd.exe" resolves to no integration provider, so this returns right after the
        // dereference that used to throw - no bootstrap files are written.
        object?[] arguments = [null, "cmd.exe", string.Empty, Path.GetTempPath()];

        applyLaunchPlan.Invoke(fixture.Pane, arguments);
    }

    private sealed class RestoredPaneFixture : IDisposable
    {
        private RestoredPaneFixture(NovaTerminal.MainWindow window, TerminalSettings settings, TerminalPane pane)
        {
            Window = window;
            Settings = settings;
            Pane = pane;
        }

        public NovaTerminal.MainWindow Window { get; }

        public TerminalSettings Settings { get; }

        public TerminalPane Pane { get; }

        public static RestoredPaneFixture Create()
        {
            // The real composition root, with only the storage roots swapped for temp ones.
            AppServiceBundle bundle = AppServices.BuildForDesigner() with
            {
                CommandAssist = TestCommandAssistServices.Instance
            };

            var window = new NovaTerminal.MainWindow(bundle);
            TabControl tabs = window.FindControl<TabControl>("Tabs")!;
            var settings = (TerminalSettings)typeof(NovaTerminal.MainWindow)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;

            var tabSession = new TabSession
            {
                Title = "Restored",
                Root = new PaneNode
                {
                    Type = NodeType.Leaf,
                    Command = "cmd.exe",
                    Arguments = string.Empty,
                    PaneId = Guid.NewGuid().ToString()
                }
            };

            TabItem restored = SessionManager.CreateRestoredTabItem(tabSession, settings)!;
            tabs.Items.Clear();
            tabs.Items.Add(restored);

            // The production entry point for restored content: MainWindow calls this from
            // ApplySessionSnapshot, TryRestoreStartupSession and HydrateDeferredStartupTab.
            typeof(NovaTerminal.MainWindow)
                .GetMethod("InitializeRestoredTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [tabs]);

            var pane = (TerminalPane)restored.Content!;
            return new RestoredPaneFixture(window, settings, pane);
        }

        public void Dispose()
        {
            Window.Close();
        }
    }
}
