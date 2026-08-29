using Avalonia.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// Shared open/assert-tab/close scaffolding for every scenario that opens
/// <see cref="SettingsWindow"/> on a specific tab. Extracted after
/// <see cref="SettingsAppearanceScenario"/> and <see cref="SettingsAgentAccessScenario"/> were
/// found (Task 14 review) to duplicate this verbatim - window construction, Show/Pump, the
/// MainTabs null-check-and-throw, the selected-header comparison and its descriptive drift
/// message, and the try/finally Close/Pump - down to near-identical wording. Only the tab index,
/// the expected header, the window size and what to do once the tab is confirmed open differ
/// between callers, so those are the only things left to them.
/// </summary>
internal static class SettingsWindowScenario
{
    /// <summary>
    /// Opens <see cref="SettingsWindow"/> on <paramref name="tabIndex"/>, asserts the tab that
    /// actually got selected carries <paramref name="expectedHeader"/> - failing loudly, by name,
    /// if <c>SettingsWindow.axaml</c>'s tab order has drifted from what the caller assumes,
    /// rather than silently capturing the wrong tab - then runs <paramref name="body"/> and
    /// always closes the window afterwards, success or failure.
    /// </summary>
    public static Task RunAsync(
        ShotContext context,
        int tabIndex,
        string expectedHeader,
        double width,
        double height,
        Action<ShotContext, SettingsWindow> body)
    {
        var settingsWindow = new SettingsWindow(initialTab: tabIndex)
        {
            Width = width,
            Height = height
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
            if (!string.Equals(selectedHeader, expectedHeader, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected initialTab {tabIndex} to select the '{expectedHeader}' tab, but " +
                    $"SettingsWindow selected '{selectedHeader ?? "(none)"}' instead. SettingsWindow.axaml's " +
                    "tab order has drifted from what this scenario assumes - update the tab index rather " +
                    "than silently capturing the wrong tab.");
            }

            body(context, settingsWindow);
        }
        finally
        {
            settingsWindow.Close();
            context.Driver.Pump(3);
        }

        return Task.CompletedTask;
    }
}
