using Avalonia.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The observe/act opt-in pair. This is the differentiating screenshot, so it shows the real
/// settings surface with act deliberately OFF — the honest default, and the one that makes the
/// separate-opt-in design legible at a glance.
/// </summary>
internal sealed class SettingsAgentAccessScenario : IScenario
{
    private const string AgentAccessHeader = "Agent Access";

    /// <summary>
    /// Agent Access's 0-based index among SettingsWindow's six tabs (Appearance, Profiles,
    /// Shortcuts, Command Assist, Agent Access, SSH — confirmed against SettingsWindow.axaml,
    /// where Agent Access is the fifth &lt;TabItem&gt;). Selected through the constructor rather
    /// than a post-construction TabItem lookup so the sidebar nav gets synced for free
    /// (SyncSidebarFromTabs runs once at construction against whatever tabs.SelectedIndex already
    /// is). The index is trusted but verified: RunAsync asserts the tab this actually selects
    /// carries the "Agent Access" header, and throws if the two have drifted apart, rather than
    /// silently capturing whatever tab index 4 now happens to be.
    /// </summary>
    private const int AgentAccessTabIndex = 4;

    public ShotSpec Spec { get; } = new(
        Name: "settings-agent-access",
        Tier: 2,
        LogicalWidth: 1000,
        LogicalHeight: 760,
        Intent: "The settings window on the Agent Access tab, with the observe toggle on, the act " +
                "toggle visibly off beneath it, and the explanatory text readable.");

    public Task RunAsync(ShotContext context)
    {
        var settingsWindow = new SettingsWindow(initialTab: AgentAccessTabIndex)
        {
            Width = Spec.LogicalWidth,
            Height = Spec.LogicalHeight
        };

        // Show/Pump run inside the try, not before it: Pump runs Dispatcher.UIThread.RunJobs(),
        // which executes work tied to the window just shown, so a throw there would otherwise
        // leak a second live Window past this method with nothing left to close it.
        try
        {
            settingsWindow.Show();
            context.Driver.Pump(5);

            TabControl tabs = settingsWindow.FindControl<TabControl>("MainTabs")
                ?? throw new InvalidOperationException("SettingsWindow has no 'MainTabs' control.");

            string? selectedHeader = (tabs.SelectedItem as TabItem)?.Header as string;
            if (!string.Equals(selectedHeader, AgentAccessHeader, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected initialTab {AgentAccessTabIndex} to select the '{AgentAccessHeader}' tab, but " +
                    $"SettingsWindow selected '{selectedHeader ?? "(none)"}' instead. SettingsWindow.axaml's " +
                    "tab order has drifted from what AgentAccessTabIndex assumes - update the constant rather " +
                    "than silently capturing the wrong tab.");
            }

            context.CaptureOther(settingsWindow, "tab");
        }
        finally
        {
            settingsWindow.Close();
            context.Driver.Pump(3);
        }

        return Task.CompletedTask;
    }
}
