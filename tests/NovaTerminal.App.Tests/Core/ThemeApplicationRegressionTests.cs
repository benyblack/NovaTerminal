using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using NovaTerminal.Shell;
using NovaTerminal.Shell.TitleBar;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Regression tests for the theme-application fixes:
///
/// - the Settings title-bar rows and shortcut rows used hardcoded dark hex backgrounds, so light
///   themes left navy cards on an otherwise light page; they now hold the window's NtPanel /
///   NtHairline palette brush instances, which ThemePaletteResources recolors in place.
/// - the Default theme's ANSI blue RGB(0,0,238) was lifted straight into every UI accent brush,
///   producing eye-searing primary buttons; the accent is now softened for chrome use.
/// - MainWindow.RebuildTitleBar replaces the generated title-bar buttons with fresh instances, so
///   the theme foregrounds an earlier ApplyThemeToUI had applied were lost - white icons under the
///   app's default Dark variant, invisible on light themes. The rebuild now re-applies them.
/// </summary>
public sealed class ThemeApplicationRegressionTests
{
    // The Default theme's ANSI blue - the "eye killer".
    private static TermColor PureBlue => TermColor.FromRgb(0, 0, 238);

    [Fact]
    public void SoftenAccent_PureBlue_LandsOnACalmerAccentWithHuePreserved()
    {
        var softened = ThemePaletteResources.SoftenAccent(PureBlue.ToAvaloniaColor());

        // RGB(0,0,238): S clamps 1.0 -> 0.68, L (0.467) is inside the band, hue 240° preserved.
        Assert.Equal(Color.FromRgb(38, 38, 200), softened);
        Assert.Equal(softened.R, softened.G);
        Assert.True(softened.B > softened.R);
    }

    [Fact]
    public void SoftenAccent_AlreadyCalmAccent_IsUnchanged()
    {
        // A mid-lightness, mid-saturation blue passes the caps without material change.
        var calm = Color.FromRgb(0x62, 0x72, 0xa4);

        Assert.Equal(calm, ThemePaletteResources.SoftenAccent(calm));
    }

    [Fact]
    public void Apply_AccentBrushes_TrackTheSoftenedBlueNotTheRawAnsiBlue()
    {
        var resources = new ResourceDictionary();
        var theme = new TerminalTheme { Name = "T", Background = TermColor.Black, Blue = PureBlue };

        ThemePaletteResources.Apply(resources, theme);

        var accent = Assert.IsType<SolidColorBrush>(resources["NtBlue"]);
        Assert.Equal(ThemePaletteResources.SoftenAccent(PureBlue.ToAvaloniaColor()), accent.Color);
        Assert.NotEqual(PureBlue.ToAvaloniaColor(), accent.Color);
    }

    [Fact]
    public void Apply_AccentForeground_ContrastsTheAccent()
    {
        var resources = new ResourceDictionary();

        // Softened Default blue is dark -> white accent foreground.
        ThemePaletteResources.Apply(resources, new TerminalTheme { Name = "Dark", Blue = PureBlue });
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(resources["NtAccentFg"]).Color);

        // A light accent keeps the dark navy foreground the XAML always used.
        resources = new ResourceDictionary();
        ThemePaletteResources.Apply(resources, new TerminalTheme { Name = "Light", Blue = TermColor.FromRgb(0x61, 0xaf, 0xef) });
        Assert.Equal(Color.Parse("#0a1622"), Assert.IsType<SolidColorBrush>(resources["NtAccentFg"]).Color);
    }

    /// <summary>
    /// ThemePaletteResources recolors brushes IN PLACE so every StaticResource holder updates
    /// live; replacing the dictionary entries with new instances instead would silently freeze
    /// all of them on the first theme.
    /// </summary>
    [Fact]
    public void Apply_AcrossThemeSwitches_KeepsBrushInstancesStable()
    {
        var resources = new ResourceDictionary();
        ThemePaletteResources.Apply(resources, new TerminalTheme { Name = "A", Blue = PureBlue });
        var accentBrush = Assert.IsType<SolidColorBrush>(resources["NtBlue"]);
        Color colorBefore = accentBrush.Color;

        ThemePaletteResources.Apply(resources, new TerminalTheme { Name = "B", Blue = TermColor.FromRgb(0x61, 0xaf, 0xef) });

        Assert.Same(accentBrush, resources["NtBlue"]);
        Assert.NotEqual(colorBefore, accentBrush.Color);
    }

    /// <summary>
    /// Both code-built card kinds must hold the window's palette brush instances. Holding
    /// hardcoded hex (the regression) or a copy means light themes render dark navy cards.
    /// </summary>
    [AvaloniaFact]
    public void SettingsWindow_CardRows_UseTheWindowPaletteBrushes()
    {
        var window = new NovaTerminal.SettingsWindow();

        var panelBrush = Assert.IsType<SolidColorBrush>(window.Resources["NtPanel"]);
        var hairlineBrush = Assert.IsType<SolidColorBrush>(window.Resources["NtHairline"]);

        var titleBarRows = window.FindControl<StackPanel>("TitleBarItemsPanel")!.Children.OfType<Border>().ToList();
        Assert.NotEmpty(titleBarRows);
        foreach (var row in titleBarRows)
        {
            Assert.Same(panelBrush, row.Background);
            Assert.Same(hairlineBrush, row.BorderBrush);
        }

        var shortcutRows = window.FindControl<StackPanel>("ShortcutBindingsPanel")!.Children.OfType<Border>().ToList();
        Assert.NotEmpty(shortcutRows);
        foreach (var row in shortcutRows)
        {
            Assert.Same(panelBrush, row.Background);
            Assert.Same(hairlineBrush, row.BorderBrush);
        }
    }

    /// <summary>
    /// A rebuild replaces the generated buttons, so it must itself re-apply the theme foregrounds:
    /// with a light active theme the Tab List button and its icon must come out dark (contrast),
    /// not the Fluent Dark-variant white the fresh instances would otherwise inherit.
    /// </summary>
    [AvaloniaFact]
    public void RebuildTitleBar_LightTheme_TabListButtonAndIconGetContrastForeground()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);

        // Light background -> GetContrastForeground returns black. Mutating the cached ActiveTheme
        // keeps this independent of which theme files exist in the test's theme directory.
        settings.ActiveTheme.Background = TermColor.FromRgb(245, 242, 233);

        InvokeRebuildTitleBar(window);

        var button = GetTitleBarHost(window).Children.OfType<Button>()
            .Single(b => b.Name == TitleBarViewFactory.ButtonName("open_tab_list"));
        Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(button.Foreground).Color);

        var icon = Assert.IsType<PathIcon>(button.Content);
        Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(icon.Foreground).Color);
    }

    private static StackPanel GetTitleBarHost(NovaTerminal.MainWindow window)
    {
        var host = window.FindControl<StackPanel>("TitleBarItemsHost");
        Assert.NotNull(host);
        return host!;
    }

    private static TerminalSettings GetSettings(NovaTerminal.MainWindow window)
    {
        var field = typeof(NovaTerminal.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TerminalSettings)field!.GetValue(window)!;
    }

    private static void InvokeRebuildTitleBar(NovaTerminal.MainWindow window)
    {
        var method = typeof(NovaTerminal.MainWindow).GetMethod("RebuildTitleBar", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, System.Array.Empty<object>());
    }
}
