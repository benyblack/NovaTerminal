namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The observe/act opt-in pair. This is the differentiating screenshot, so it shows the real
/// settings surface with act deliberately OFF — the honest default, and the one that makes the
/// separate-opt-in design legible at a glance.
/// </summary>
internal sealed class SettingsAgentAccessScenario : IScenario
{
    /// <summary>
    /// Agent Access's 0-based index among SettingsWindow's six tabs (Appearance, Profiles,
    /// Shortcuts, Command Assist, Agent Access, SSH — confirmed against SettingsWindow.axaml,
    /// where Agent Access is the fifth &lt;TabItem&gt;).
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
        // Selected via the constructor's initialTab rather than by walking the visual tree for a
        // TabItem with a matching header. This is the same mechanism the app's own callers use to
        // land on a specific tab (see the SettingsSection-targeted caller in
        // SettingsWindow.axaml.cs), it is exercised by production code instead of a
        // screenshot-only lookup, and it gets the sidebar-nav sync for free: SyncSidebarFromTabs
        // runs once at construction against whatever tabs.SelectedIndex already is, so the
        // sidebar and the tab strip agree in the captured frame without any extra plumbing here.
        var settingsWindow = new SettingsWindow(initialTab: AgentAccessTabIndex)
        {
            Width = Spec.LogicalWidth,
            Height = Spec.LogicalHeight
        };

        settingsWindow.Show();
        context.Driver.Pump(5);

        try
        {
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
