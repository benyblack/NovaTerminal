namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// Five tiles of hero-single's transcript, one per built-in theme, tiled into a single PNG.
/// Unlike every other scenario this one is never run through the normal per-scenario loop:
/// applying a theme only fully takes effect at MainWindow construction time (see
/// <see cref="IScenario.Settings"/>'s remarks), so five different themes need five separate
/// windows, not one window re-themed mid-run. Program special-cases this scenario's name and
/// routes it to a multi-pass composer instead of calling <see cref="RunAsync"/>.
/// </summary>
internal sealed class ThemesGridScenario : IScenario
{
    /// <summary>
    /// The themes tiled by Program's composer, keyed by the JSON "Name" field inside each theme
    /// file - not the filename. ThemeManager.LoadThemes keys its dictionary by that Name field
    /// (ThemeManager.cs:60), and ThemeManager.GetTheme falls back to "Default" *silently* on a
    /// miss (ThemeManager.cs:119-129): no exception, no warning. A filename-shaped guess like
    /// "GitHubDark" (no space) is exactly such a miss - it would not error, it would quietly
    /// render Default's palette and produce a grid with plausible-looking duplicate tiles. The
    /// values below were verified against src/NovaTerminal.App/themes/*.json's own "Name"
    /// fields, spaces included.
    /// </summary>
    internal static readonly IReadOnlyList<string> Themes =
        ["Dracula", "GitHub Dark", "Monokai", "One Half Dark", "Solarized Dark"];

    public ShotSpec Spec { get; } = new(
        Name: "themes-grid",
        Tier: 1,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A 2-column, 3-row grid of five tiles separated by even gaps, each tile showing the " +
                "same terminal transcript (banner, git status, test run) rendered in a different " +
                "built-in theme - Dracula, GitHub Dark, Monokai, One Half Dark and Solarized Dark - " +
                "with each tile's background and text colours visibly distinct from its neighbours' " +
                "and the last cell left as plain background.");

    public Task RunAsync(ShotContext context) =>
        throw new NotSupportedException(
            "themes-grid is composed by Program's multi-pass path (one MainWindow per theme, " +
            "since a theme only fully applies at construction time), not run through the normal " +
            "per-scenario loop. Program excludes this scenario's name from that loop and calls " +
            "its own composer instead.");
}
