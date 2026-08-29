using Avalonia.Controls;
using Avalonia.Media;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The Appearance tab: theme selection, font family and size, and the live preview swatch that
/// reflects whichever theme is actually selected. Reuses the tab-selection and header-assertion
/// machinery <see cref="SettingsAgentAccessScenario"/> already built for Task 7 rather than
/// duplicating it — only the tab index, header and per-tab assertions differ.
/// </summary>
internal sealed class SettingsAppearanceScenario : IScenario
{
    private const string AppearanceHeader = "Appearance";

    /// <summary>
    /// Appearance's 0-based index among SettingsWindow's six tabs (Appearance, Profiles,
    /// Shortcuts, Command Assist, Agent Access, SSH — confirmed against SettingsWindow.axaml,
    /// where Appearance is the first &lt;TabItem&gt;).
    /// </summary>
    private const int AppearanceTabIndex = 0;

    /// <summary>DemoWorld.SeedSettings pins this theme; see its own remarks for why.</summary>
    private const string SeededThemeName = "GitHub Dark";

    /// <summary>GitHub Dark's Background, straight out of themes/GitHubDark.json.</summary>
    private static readonly Color SeededThemeBackground = Color.Parse("#0d1117");

    /// <summary>GitHub Dark's Foreground, straight out of themes/GitHubDark.json.</summary>
    private static readonly Color SeededThemeForeground = Color.Parse("#c9d1d9");

    /// <summary>
    /// Unusually tall for a settings shot. The Intent asks for theme selection, its live preview,
    /// and font family/size together; on the Appearance tab those sit roughly 1450 logical pixels
    /// apart in the scroll, separated by the (long, dynamically-populated) title bar customization
    /// list. Scrolling to either section alone would drop the other, so the window is tall enough
    /// to hold both without scrolling instead - a taller image being the honest cost of an Intent
    /// that spans two sections a normal window can't show together.
    /// </summary>
    public ShotSpec Spec { get; } = new(
        Name: "settings-appearance",
        Tier: 2,
        LogicalWidth: 1000,
        LogicalHeight: 2200,
        Intent: "The settings window on Appearance, showing theme selection, font family and size, " +
                "with a live preview of the chosen theme.");

    public Task RunAsync(ShotContext context)
    {
        var settingsWindow = new SettingsWindow(initialTab: AppearanceTabIndex)
        {
            Width = Spec.LogicalWidth,
            Height = Spec.LogicalHeight
        };

        try
        {
            settingsWindow.Show();
            context.Driver.Pump(5);

            TabControl tabs = settingsWindow.FindControl<TabControl>("MainTabs")
                ?? throw new InvalidOperationException("SettingsWindow has no 'MainTabs' control.");

            string? selectedHeader = (tabs.SelectedItem as TabItem)?.Header as string;
            if (!string.Equals(selectedHeader, AppearanceHeader, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected initialTab {AppearanceTabIndex} to select the '{AppearanceHeader}' tab, but " +
                    $"SettingsWindow selected '{selectedHeader ?? "(none)"}' instead. SettingsWindow.axaml's " +
                    "tab order has drifted from what AppearanceTabIndex assumes - update the constant rather " +
                    "than silently capturing the wrong tab.");
            }

            // Theme selection: LoadCurrentSettings matches ThemeList's items against
            // _settings.ThemeName by string equality and sets SelectedItem - proof that the
            // combo genuinely reflects the seeded theme rather than sitting on whatever the
            // first item happens to be.
            var themeList = context.Driver.RequireIn<ComboBox>(settingsWindow, "ThemeList");
            if (themeList.SelectedItem is not ComboBoxItem themeItem ||
                !string.Equals(themeItem.Content?.ToString(), SeededThemeName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"ThemeList's selection is '{(themeList.SelectedItem as ComboBoxItem)?.Content}', not " +
                    $"'{SeededThemeName}'. DemoWorld.SeedSettings pins the theme; either the seed drifted or " +
                    "PopulateThemes/LoadCurrentSettings stopped matching it.");
            }

            // Font size: a plain, unconditional assignment from _settings.FontSize, so this is a
            // direct readback of the seeded value rather than a best-effort match.
            var fontSizeInput = context.Driver.RequireIn<NumericUpDown>(settingsWindow, "FontSizeInput");
            if (fontSizeInput.Value != 18m)
            {
                throw new InvalidOperationException(
                    $"FontSizeInput reads {fontSizeInput.Value}, not the 18 DemoWorld.SeedSettings seeds. " +
                    "The font-size row would not be showing real state.");
            }

            // Font family: BuildFontFamilyChoices adds the configured family verbatim as one of the
            // combo's own items (see its remarks), so a genuine seeded value round-trips to a real
            // selection here too, not just to font-size's simpler numeric copy.
            var fontList = context.Driver.RequireIn<ComboBox>(settingsWindow, "FontList");
            if (fontList.SelectedItem is not ComboBoxItem)
            {
                throw new InvalidOperationException(
                    "FontList has no selected item, so the font-family row would be showing an empty " +
                    "combo instead of the seeded font.");
            }

            // Live preview: ThemeList's own SelectionChanged handler is wired before
            // LoadCurrentSettings runs, so setting SelectedItem to "GitHub Dark" during
            // construction already fired it and repainted the sample border/text for real -
            // this is not a static XAML placeholder color.
            var sampleBorder = context.Driver.RequireIn<Border>(settingsWindow, "SampleTextBorder");
            var sampleText = context.Driver.RequireIn<TextBlock>(settingsWindow, "SampleTextBlock");

            if (sampleBorder.Background is not ISolidColorBrush borderBrush || borderBrush.Color != SeededThemeBackground)
            {
                throw new InvalidOperationException(
                    $"SampleTextBorder's background is {(sampleBorder.Background as ISolidColorBrush)?.Color}, " +
                    $"not GitHub Dark's {SeededThemeBackground}. The live preview would not be showing the " +
                    "actually-selected theme.");
            }

            if (sampleText.Foreground is not ISolidColorBrush textBrush || textBrush.Color != SeededThemeForeground)
            {
                throw new InvalidOperationException(
                    $"SampleTextBlock's foreground is {(sampleText.Foreground as ISolidColorBrush)?.Color}, " +
                    $"not GitHub Dark's {SeededThemeForeground}. The live preview would not be showing the " +
                    "actually-selected theme.");
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
