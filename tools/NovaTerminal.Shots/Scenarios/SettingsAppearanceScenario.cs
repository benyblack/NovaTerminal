using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using SkiaSharp;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The Appearance tab: theme selection, font family and size, and the live preview swatch that
/// reflects whichever theme is actually selected. Reuses <see cref="SettingsWindowScenario"/>'s
/// open/assert-tab/close scaffolding rather than duplicating it — only the tab index, header and
/// per-tab body differ from <see cref="SettingsAgentAccessScenario"/>.
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
    /// Breathing room, in logical pixels, kept around each cropped section so its border/heading
    /// isn't cut flush against the composed image's edge.
    /// </summary>
    private const double SectionPadding = 24;

    public ShotSpec Spec { get; } = new(
        Name: "settings-appearance",
        Tier: 2,
        LogicalWidth: 1000,
        LogicalHeight: 760,
        Intent: "The settings window on Appearance, showing theme selection, font family and size, " +
                "with a live preview of the chosen theme.");

    public Task RunAsync(ShotContext context) =>
        SettingsWindowScenario.RunAsync(
            context,
            AppearanceTabIndex,
            AppearanceHeader,
            Spec.LogicalWidth,
            Spec.LogicalHeight,
            AssertAndCapture);

    private static void AssertAndCapture(ShotContext context, SettingsWindow settingsWindow)
    {
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

        // Framing: the Theme/Preview section and the Font family/size section sit ~1450 logical
        // pixels apart in the Appearance tab's single scroll, separated by the (long,
        // dynamically-populated) Title Bar customization list and the Window section - neither of
        // which the Intent mentions. A window tall enough to hold both without scrolling produced
        // a 2000x4400 physical master (Task 14's first cut) that Task 15's README/site variants -
        // both derived by scaling this master down to a fixed width - cannot use: the height comes
        // along for the ride and breaks the downstream pipeline. Instead this captures the real
        // window at its own natural, normal size twice - once at the top of the scroll (theme +
        // preview), once scrolled to the font section - and stitches the two on-topic crops into
        // one frame. Both halves are genuine renders of the real, already-asserted-populated
        // control tree; nothing is drawn that the product didn't actually show on screen.
        //
        // Both crops start at the tab content's own left edge, past the tab-switcher sidebar: the
        // sidebar is a fixed column outside the Appearance tab's ScrollViewer, so it renders
        // identically in both captures, and stacking two full-width crops would have shown two
        // copies of it, one cut off mid-list under the other. The sidebar is also not part of what
        // the Intent asks for - cropping it out of both halves keeps the fields left-aligned across
        // the seam instead of one wider tile centred over a narrower one.
        var themePreviewArea = context.Driver.RequireIn<Border>(settingsWindow, "ThemePreviewArea");
        ScrollViewer scrollViewer = themePreviewArea.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "ThemePreviewArea has no ancestor ScrollViewer. SettingsWindow has more than one " +
                "ScrollViewer at once (the tab-switcher sidebar has its own, non-scrolling one), so this " +
                "must be found by walking up from a control inside the tab's own content.");
        int contentLeftPhysical = (int)Math.Round(
            BoundsInWindow(scrollViewer, settingsWindow).Left * context.Run.Scale);

        SKBitmap themeCrop = CaptureThemeSection(context, settingsWindow, themePreviewArea, contentLeftPhysical);
        try
        {
            SKBitmap fontCrop = CaptureFontSection(context, settingsWindow, scrollViewer, fontSizeInput, contentLeftPhysical);
            try
            {
                using SKBitmap composed = PostProcess.StackVertical(
                    [themeCrop, fontCrop],
                    gap: 0,
                    background: new SKColor(0x16, 0x18, 0x1d)); // SettingsWindow's own Background, #16181d.

                context.CaptureComposed(composed, "tab");
            }
            finally
            {
                fontCrop.Dispose();
            }
        }
        finally
        {
            themeCrop.Dispose();
        }
    }

    /// <summary>
    /// Captures the window at its default (top-of-scroll) position and crops down to the header
    /// through the bottom of the theme preview area - the Appearance header stays in, the Title
    /// Bar section that immediately follows does not.
    /// </summary>
    private static SKBitmap CaptureThemeSection(
        ShotContext context, SettingsWindow settingsWindow, Border themePreviewArea, int contentLeftPhysical)
    {
        using SKBitmap frame = Rasterizer.CaptureWindow(settingsWindow, context.Run.Scale);

        double bottomLogical = BoundsInWindow(themePreviewArea, settingsWindow).Bottom + SectionPadding;
        int bottomPhysical = (int)Math.Round(bottomLogical * context.Run.Scale);

        return PostProcess.Crop(frame, new SKRectI(contentLeftPhysical, 0, frame.Width, bottomPhysical));
    }

    /// <summary>
    /// Scrolls the Appearance tab's ScrollViewer so the "FONTS &amp; TEXT" section is at the top
    /// of the viewport, captures the window again, and crops down to just that header through the
    /// bottom of the font-size row - leaving the ligatures/HarfBuzz/notification rows that follow
    /// it (part of the same visual section, but not what the Intent asks for) out of the frame.
    /// </summary>
    private static SKBitmap CaptureFontSection(
        ShotContext context,
        SettingsWindow settingsWindow,
        ScrollViewer scrollViewer,
        NumericUpDown fontSizeInput,
        int contentLeftPhysical)
    {
        TextBlock fontsHeader = FindSectionHeader(settingsWindow, "FONTS & TEXT");

        // Same TranslatePoint-onto-the-ScrollViewer technique MainWindow.EnsureSelectedTabHeaderVisible
        // already uses to scroll a tab strip - here read once at the current (zero) offset, which is
        // exactly the header's distance from the top of the scrollable content.
        Point headerInContent = fontsHeader.TranslatePoint(new Point(0, 0), scrollViewer)
            ?? throw new InvalidOperationException(
                "'FONTS & TEXT' header is not inside the Appearance tab's ScrollViewer.");

        scrollViewer.Offset = new Vector(0, Math.Max(0, headerInContent.Y - SectionPadding));
        context.Driver.Pump(5);

        using SKBitmap frame = Rasterizer.CaptureWindow(settingsWindow, context.Run.Scale);

        double topLogical = Math.Max(0, BoundsInWindow(fontsHeader, settingsWindow).Top - SectionPadding);
        double bottomLogical = BoundsInWindow(fontSizeInput, settingsWindow).Bottom + SectionPadding;

        int topPhysical = (int)Math.Round(topLogical * context.Run.Scale);
        int bottomPhysical = (int)Math.Round(bottomLogical * context.Run.Scale);

        return PostProcess.Crop(frame, new SKRectI(contentLeftPhysical, topPhysical, frame.Width, bottomPhysical));
    }

    private static TextBlock FindSectionHeader(Visual root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Classes.Contains("SectionHeader") && string.Equals(tb.Text, text, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"No 'SectionHeader' TextBlock with text '{text}' found.");

    /// <summary>
    /// <paramref name="control"/>'s bounds translated into <paramref name="window"/>'s coordinate
    /// space, honouring whatever the enclosing ScrollViewer's current offset is.
    /// </summary>
    private static Rect BoundsInWindow(Visual control, Window window)
    {
        Point topLeft = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException(
                $"{control.GetType().Name} is not currently in {window.GetType().Name}'s visual tree.");

        return new Rect(topLeft, control.Bounds.Size);
    }
}
